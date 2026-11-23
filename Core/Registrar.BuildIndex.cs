using System;
using System.Collections.Generic;
using System.Reflection;
using CommonAPI.Systems;

namespace DSPPyPort.Core
{
    internal sealed partial class Registrar
    {
        // ------------------------------------------------------------------ build menu index
        /// <summary>
        /// 建造菜单 BuildIndex 自动分配（task-15 追加，2026-11-18）。
        ///
        /// 原版 `UIBuildMenu.StaticLoad()`（full.cs L383512）：
        ///   page = BuildIndex/100（0..15），slot = BuildIndex%100（**slot <= 12 才收录**）；
        /// 我们的建筑原来用 BuildIndex 900..916 → page9 slot0..16，其中 913-916（4 座）被丢弃，
        /// 且 page9 slot1（901）与原版建筑冲突。可见分类页实际为 page 1..9（categoryButtons[10]/[11]
        /// 是拆除/升级模式按钮），因此这里在 page 1..9、slot 0..12 的合法空位里确定性分配。
        ///
        /// 分配时机放在注册期（Preload 之前）→ UIBuildMenu._OnCreate 里的 StaticLoad 直接读到正确值；
        /// 并在 Update 探针里做一次反射校验/修复（若 staticLoaded 后槽位被其它 MOD 覆盖）。
        /// </summary>
        internal const int BuildPageMax = 9;
        internal const int BuildSlotMax = 12;

        private readonly HashSet<int> occupiedBuildSlots = new HashSet<int>(); // key = page*100+slot
        private readonly Dictionary<int, int> buildingBuildIndexes = new Dictionary<int, int>(); // itemId -> buildIndex
        private readonly bool autoBuildIndex;
        private bool buildMenuRepairDone;
        private int buildMenuRepairAttempts;
        private int buildMenuRepairedCount;

        private static readonly int[] BuildPagePriority = new int[] { 2, 1, 3, 4, 5, 6, 7, 8, 9 };

        /// <summary>注册前统计已占用 (page,slot)：LDB 已有物品（原版）+ 先加载 MOD 已注册物品。</summary>
        private void InitBuildIndexAllocation()
        {
            int fromLdb = 0;
            try
            {
                if (LDB.items != null && LDB.items.dataArray != null)
                {
                    for (int i = 0; i < LDB.items.dataArray.Length; i++)
                    {
                        ItemProto proto = LDB.items.dataArray[i];
                        if (proto != null && MarkBuildSlotIfValid(proto.BuildIndex))
                        {
                            fromLdb++;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                log.LogWarning("[BuildIndex] 读取 LDB.items 建造槽失败：" + ex.Message);
            }

            int fromMods = 0;
            try
            {
                if (commonApiItemsField == null)
                {
                    commonApiItemsField = typeof(ProtoRegistry).GetField("items", BindingFlags.NonPublic | BindingFlags.Static);
                }
                object dict = commonApiItemsField != null ? commonApiItemsField.GetValue(null) : null;
                System.Collections.IDictionary itemDict = dict as System.Collections.IDictionary;
                if (itemDict != null)
                {
                    foreach (System.Collections.DictionaryEntry entry in itemDict)
                    {
                        ItemProto proto = entry.Value as ItemProto;
                        if (proto != null && MarkBuildSlotIfValid(proto.BuildIndex))
                        {
                            fromMods++;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                log.LogWarning("[BuildIndex] 读取已注册 MOD 建造槽失败：" + ex.Message);
            }
            log.LogInfo(string.Format("[BuildIndex] 建造菜单槽位占用扫描：原版 LDB {0} 个 + 已加载 MOD {1} 个（合法范围 page1-{2} slot0-{3}）；自动分配={4}",
                fromLdb, fromMods, BuildPageMax, BuildSlotMax, autoBuildIndex ? "开" : "关"));
        }

        private bool MarkBuildSlotIfValid(int buildIndex)
        {
            if (buildIndex <= 0)
            {
                return false;
            }
            int page = buildIndex / 100;
            int slot = buildIndex % 100;
            if (page < 0 || page > 15 || slot > BuildSlotMax)
            {
                return false;
            }
            return occupiedBuildSlots.Add(page * 100 + slot);
        }

        /// <summary>在 page1-9 / slot0-12 里确定性分配一个空闲槽；按 page2→page1→page3... 优先级。</summary>
        private int AllocateBuildIndex(out int page, out int slot)
        {
            for (int p = 0; p < BuildPagePriority.Length; p++)
            {
                int candidatePage = BuildPagePriority[p];
                for (int candidateSlot = 0; candidateSlot <= BuildSlotMax; candidateSlot++)
                {
                    int key = candidatePage * 100 + candidateSlot;
                    if (occupiedBuildSlots.Contains(key))
                    {
                        continue;
                    }
                    occupiedBuildSlots.Add(key);
                    page = candidatePage;
                    slot = candidateSlot;
                    return candidatePage * 100 + candidateSlot;
                }
            }
            page = 0;
            slot = 0;
            return 0;
        }

        private static bool IsBuildIndexVisible(int buildIndex)
        {
            if (buildIndex <= 0)
            {
                return false;
            }
            int page = buildIndex / 100;
            int slot = buildIndex % 100;
            return page >= 0 && page <= 15 && slot <= BuildSlotMax;
        }

        /// <summary>取本次注册实际使用的 BuildIndex（给注册流程用）。</summary>
        private int ResolveBuildIndex(BuildingDef def, string moduleId)
        {
            if (autoBuildIndex)
            {
                if (def.BuildIndex > 0)
                {
                    int page = def.BuildIndex / 100;
                    int slot = def.BuildIndex % 100;
                    if (page >= 1 && page <= BuildPageMax && slot <= BuildSlotMax &&
                        !occupiedBuildSlots.Contains(def.BuildIndex))
                    {
                        occupiedBuildSlots.Add(def.BuildIndex);
                        return def.BuildIndex;
                    }
                    log.LogWarning("[BuildIndex] 建筑 item " + def.ItemId + " 的 build_index=" + def.BuildIndex +
                                   " 非法/已占用（合法 page1-" + BuildPageMax + " slot0-" + BuildSlotMax + "），改为自动分配");
                }
                int p;
                int s;
                int allocated = AllocateBuildIndex(out p, out s);
                if (allocated <= 0)
                {
                    log.LogError("[BuildIndex] 建造菜单 page1-" + BuildPageMax + " 已无空位，建筑 item " +
                                 def.ItemId + " 将不可见（请减少建筑或扩展分类页）");
                    return 0;
                }
                return allocated;
            }

            // AutoBuildIndex=false：保持旧的 900+ 顺序分配（会触发原版 slot<=12 限制）
            int legacy = def.BuildIndex > 0 ? def.BuildIndex : nextBuildIndex++;
            if (!IsBuildIndexVisible(legacy))
            {
                log.LogWarning("[BuildIndex] AutoBuildIndex=false，建筑 item " + def.ItemId + " 的 build_index=" +
                               legacy + " 会被原版建造菜单丢弃（slot>12）");
            }
            return legacy;
        }

        /// <summary>注册完成后打印分配表（含每页槽位）。</summary>
        private void LogBuildIndexSummary()
        {
            if (buildingBuildIndexes.Count == 0)
            {
                return;
            }
            List<int> pageKeys = new List<int>(buildingBuildIndexes.Values);
            pageKeys.Sort();
            string text = string.Empty;
            int invalid = 0;
            for (int i = 0; i < pageKeys.Count; i++)
            {
                int bi = pageKeys[i];
                int page = bi / 100;
                int slot = bi % 100;
                if (!IsBuildIndexVisible(bi) || page > BuildPageMax)
                {
                    invalid++;
                }
                if (i > 0)
                {
                    text += ", ";
                }
                text += "p" + page + "s" + slot;
            }
            log.LogInfo("[BuildIndex] 建筑 BuildIndex 分配（" + pageKeys.Count + " 座，合法 page1-" + BuildPageMax +
                        " slot0-" + BuildSlotMax + "）：" + text);
            if (invalid > 0)
            {
                log.LogWarning("[BuildIndex] 有 " + invalid + " 座建筑 BuildIndex 不在可见范围，建造菜单可能不可见");
            }
        }

        /// <summary>
        /// 建造菜单槽位校验/修复（Update 探针，等 UIBuildMenu.StaticLoad 之后）：
        /// 若某座建筑在 `UIBuildMenu.protos[page,slot]` 中不是自己（被其它 MOD/后加载物品覆盖），
        /// 重新分配一个空闲槽并直接写回 protos[,] + ItemProto.BuildIndex。
        /// </summary>
        internal void TryRepairBuildMenu()
        {
            if (!autoBuildIndex || buildMenuRepairDone || buildings.Count == 0)
            {
                return;
            }
            // 队列 2：若已成功绑到 BuildBarTool 第二排（BuildIndex 已置 0），则不做建造菜单槽位修复。
            if (BuildBarIntegration.Tier2Applied)
            {
                buildMenuRepairDone = true;
                return;
            }
            try
            {
                if (buildMenuRepairAttempts++ > 1200)
                {
                    buildMenuRepairDone = true;
                    log.LogWarning("[BuildIndex] 等待 UIBuildMenu.StaticLoad 超时，跳过建造菜单槽位校验");
                    return;
                }
                FieldInfo loadedField = typeof(UIBuildMenu).GetField("staticLoaded", BindingFlags.Static | BindingFlags.NonPublic);
                FieldInfo protosField = typeof(UIBuildMenu).GetField("protos", BindingFlags.Static | BindingFlags.NonPublic);
                if (loadedField == null || protosField == null)
                {
                    buildMenuRepairDone = true;
                    log.LogWarning("[BuildIndex] 反射缺少 UIBuildMenu.staticLoaded/protos，跳过建造菜单槽位校验");
                    return;
                }
                if (!(bool)loadedField.GetValue(null))
                {
                    return; // UIBuildMenu 还没创建/StaticLoad
                }
                ItemProto[,] protos = (ItemProto[,])protosField.GetValue(null);
                if (protos == null)
                {
                    return;
                }

                // 收集当前槽位占用（以 LDB.items 为准，排除我们自己的建筑 item）
                HashSet<int> occupied = new HashSet<int>();
                HashSet<int> ourItemIds = new HashSet<int>();
                for (int i = 0; i < buildings.Count; i++)
                {
                    if (buildings[i].Item != null)
                    {
                        ourItemIds.Add(buildings[i].Item.ID);
                    }
                }
                if (LDB.items != null && LDB.items.dataArray != null)
                {
                    for (int i = 0; i < LDB.items.dataArray.Length; i++)
                    {
                        ItemProto proto = LDB.items.dataArray[i];
                        if (proto == null || ourItemIds.Contains(proto.ID))
                        {
                            continue;
                        }
                        int bi = proto.BuildIndex;
                        int page = bi / 100;
                        int slot = bi % 100;
                        if (bi > 0 && page <= 15 && slot <= BuildSlotMax)
                        {
                            occupied.Add(page * 100 + slot);
                        }
                    }
                }

                int repaired = 0;
                for (int i = 0; i < buildings.Count; i++)
                {
                    RegisteredBuilding building = buildings[i];
                    ItemProto item = building.Item;
                    if (item == null)
                    {
                        continue;
                    }
                    int bi = item.BuildIndex;
                    int page = bi / 100;
                    int slot = bi % 100;
                    bool visible = bi > 0 && page <= 15 && slot <= BuildSlotMax;
                    if (visible && page < protos.GetLength(0) && slot < protos.GetLength(1) &&
                        ReferenceEquals(protos[page, slot], item))
                    {
                        continue; // 已正确在建造菜单里
                    }
                    // 重新分配：优先 page 2/1/3... 的空闲槽
                    int newBuildIndex = 0;
                    for (int p = 0; p < BuildPagePriority.Length && newBuildIndex == 0; p++)
                    {
                        int candidatePage = BuildPagePriority[p];
                        for (int candidateSlot = 0; candidateSlot <= BuildSlotMax; candidateSlot++)
                        {
                            int key = candidatePage * 100 + candidateSlot;
                            if (occupied.Contains(key))
                            {
                                continue;
                            }
                            if (candidatePage < protos.GetLength(0) && candidateSlot < protos.GetLength(1) &&
                                protos[candidatePage, candidateSlot] != null)
                            {
                                continue;
                            }
                            occupied.Add(key);
                            newBuildIndex = candidatePage * 100 + candidateSlot;
                            if (visible && page < protos.GetLength(0) && slot < protos.GetLength(1) &&
                                ReferenceEquals(protos[page, slot], item))
                            {
                                protos[page, slot] = null;
                            }
                            item.BuildIndex = newBuildIndex;
                            protos[candidatePage, candidateSlot] = item;
                            break;
                        }
                    }
                    if (newBuildIndex > 0)
                    {
                        repaired++;
                        log.LogWarning("[BuildIndex] 建筑 item " + item.ID + " 的建造菜单槽位被占用/不可见，已改分配 " +
                                       bi + " → " + newBuildIndex + "（page" + (newBuildIndex / 100) + " slot" + (newBuildIndex % 100) + "）");
                    }
                    else
                    {
                        log.LogError("[BuildIndex] 建筑 item " + item.ID + " 找不到空闲建造槽位，建造菜单不可见");
                    }
                }
                buildMenuRepairDone = true;
                buildMenuRepairedCount = repaired;
                log.LogInfo("[BuildIndex] 建造菜单校验完成：18 座建筑中 " + (buildings.Count - repaired) + " 座槽位正确、修复 " +
                            repaired + " 座（page1-" + BuildPageMax + " slot0-" + BuildSlotMax + "）");
            }
            catch (Exception ex)
            {
                buildMenuRepairDone = true;
                log.LogWarning("[BuildIndex] 建造菜单校验/修复失败（不影响其它功能）：" + ex.Message);
            }
        }

        private static FieldInfo commonApiItemsField;

        // ------------------------------------------------------------------ registered id accessors (sandbox unlock)
        internal List<int> GetRegisteredRecipeIds()
        {
            List<int> ids = new List<int>(recipes.Count);
            for (int i = 0; i < recipes.Count; i++)
            {
                if (recipes[i].Proto != null)
                {
                    ids.Add(recipes[i].Proto.ID);
                }
            }
            return ids;
        }

        internal List<int> GetRegisteredTechIds()
        {
            List<int> ids = new List<int>(techs.Count + replacedTechs.Count);
            for (int i = 0; i < techs.Count; i++)
            {
                if (techs[i].Proto != null)
                {
                    ids.Add(techs[i].Proto.ID);
                }
            }
            // 科技替换模式：被原位覆盖的原版主页科技也是本 MOD 的科技，沙盒解锁必须包含
            for (int i = 0; i < replacedTechs.Count; i++)
            {
                if (replacedTechs[i].Proto != null)
                {
                    ids.Add(replacedTechs[i].Proto.ID);
                }
            }
            return ids;
        }
    }
}
