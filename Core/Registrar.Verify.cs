using System;
using System.Collections.Generic;
using System.Text;
using CommonAPI.Systems;

namespace DSPPyPort.Core
{
    internal sealed partial class Registrar
    {
        // ================================================================== self check
        private void ReportSelfCheck(bool verbose)
        {
            int missingItems = 0;
            int missingRecipes = 0;
            int missingTechs = 0;
            int overridden = 0;

            for (int i = 0; i < items.Count; i++)
            {
                ItemProto proto = items[i].Proto;
                if (proto == null)
                {
                    missingItems++;
                    continue;
                }
                if (proto.ID != items[i].ExpectedId)
                {
                    overridden++;
                }
                if (LDB.items.Select(proto.ID) == null)
                {
                    missingItems++;
                }
            }
            for (int i = 0; i < buildings.Count; i++)
            {
                RegisteredBuilding building = buildings[i];
                if (building.Item == null || LDB.items.Select(building.Item.ID) == null)
                {
                    missingItems++;
                }
                if (building.Model == null || LDB.models.Select(building.Model.ID) == null)
                {
                    log.LogWarning("[PyPort] 自检：建筑模型 " + building.ExpectedModelId + " 不在 LDB.models 里");
                }
            }
            for (int i = 0; i < recipes.Count; i++)
            {
                RecipeProto proto = recipes[i].Proto;
                if (proto == null)
                {
                    missingRecipes++;
                    continue;
                }
                if (proto.ID != recipes[i].ExpectedId)
                {
                    overridden++;
                }
                if (LDB.recipes.Select(proto.ID) == null)
                {
                    missingRecipes++;
                }
            }
            for (int i = 0; i < techs.Count; i++)
            {
                TechProto proto = techs[i].Proto;
                if (proto == null)
                {
                    missingTechs++;
                    continue;
                }
                if (proto.ID != techs[i].ExpectedId)
                {
                    overridden++;
                }
                if (LDB.techs.Select(proto.ID) == null)
                {
                    missingTechs++;
                }
            }

            int missingItemRefs = 0;
            List<string> refSamples = new List<string>();
            for (int i = 0; i < recipes.Count; i++)
            {
                RecipeDef def = recipes[i].Def;
                for (int j = 0; j < def.Inputs.Length; j++)
                {
                    if (LDB.items.Select(def.Inputs[j]) == null)
                    {
                        missingItemRefs++;
                        if (refSamples.Count < 5)
                        {
                            refSamples.Add("配方 " + def.Id + " 原料 " + def.Inputs[j]);
                        }
                    }
                }
                for (int j = 0; j < def.Outputs.Length; j++)
                {
                    if (LDB.items.Select(def.Outputs[j]) == null)
                    {
                        missingItemRefs++;
                        if (refSamples.Count < 5)
                        {
                            refSamples.Add("配方 " + def.Id + " 产物 " + def.Outputs[j]);
                        }
                    }
                }
            }

            int missingMatrices = 0;
            for (int i = 0; i < techs.Count; i++)
            {
                TechDef def = techs[i].Def;
                for (int j = 0; j < def.Matrices.Length; j++)
                {
                    if (LDB.items.Select(def.Matrices[j]) == null)
                    {
                        missingMatrices++;
                    }
                }
            }

            int orphanCustomTypes = 0;
            if (buildings.Count > 0)
            {
                for (int i = 0; i < recipes.Count; i++)
                {
                    int type = recipes[i].Def.Type;
                    if (type >= BuildingRecipeTypeMin && type <= BuildingRecipeTypeMax &&
                        !usedCustomRecipeTypes.Contains((byte)type))
                    {
                        orphanCustomTypes++;
                    }
                }
            }

            bool changed = missingItems != lastMissingItems || missingRecipes != lastMissingRecipes ||
                           missingTechs != lastMissingTechs;
            if (verbose || changed || !selfCheckLogged)
            {
                selfCheckLogged = true;
                lastMissingItems = missingItems;
                lastMissingRecipes = missingRecipes;
                lastMissingTechs = missingTechs;
                log.LogInfo(string.Format(
                    "[PyPort] 加载完成自检：LDB 物品 {0} / 配方 {1} / 科技 {2} / 模型 {3}；缺失 物品 {4} / 配方 {5} / 科技 {6}；ID 被 LDBTool 改写 {7}",
                    LDB.items.dataArray.Length, LDB.recipes.dataArray.Length, LDB.techs.dataArray.Length,
                    LDB.models.dataArray.Length, missingItems, missingRecipes, missingTechs, overridden));
                log.LogInfo(string.Format(
                    "[PyPort] 引用自检：配方引用缺失物品 {0} 处 / 科技矩阵缺失 {1} 处 / 自定义配方类型无对应建筑 {2} 条",
                    missingItemRefs, missingMatrices, orphanCustomTypes));
            }
            if (missingItemRefs > 0 && refSamples.Count > 0)
            {
                log.LogWarning("[PyPort] 引用缺失样例：" + string.Join("；", refSamples.ToArray()));
            }
            if (overridden > 0)
            {
                log.LogWarning("[PyPort] 有 " + overridden + " 个 proto 的 ID 被 LDBTool.CustomID.cfg 改写；" +
                               "内容侧引用可能错位，必要时删除 BepInEx\\config\\LDBTool\\LDBTool.CustomID.cfg 中对应行");
            }
            if (wideRecipeCount > 0)
            {
                if (DependencyProbe.RecipeSlotsAvailable)
                {
                    log.LogInfo("[PyPort] 宽配方 " + wideRecipeCount + " 条：DSP-RecipeSlots v" +
                                DependencyProbe.RecipeSlotsVersion + " 已就绪");
                }
                else
                {
                    log.LogError("[PyPort] 宽配方 " + wideRecipeCount + " 条（>6 原料或 >2 产物），但缺少 DSP-RecipeSlots：" +
                                 "机器只会吃前 6 项、多余产物会丢失。请安装 DSP_RecipeSlots " +
                                 DependencyProbe.RecipeSlotsMinVersion + "+ 后重启游戏。");
                }
            }
            if (verbose)
            {
                LogBuildingVerification();
                LogWideRecipeVerification();
                LogTechVerification();
                ScanDuplicateIds();
            }
        }

        // ================================================================== 联调验证输出
        /// <summary>
        /// 验证 a：每座自建建筑的 prefabDesc.assemblerRecipeType 是否 = 契约 60-67，
        /// 以及 LDB 里该 type 的配方数（可见性判据 recipe.Type == prefabDesc.assemblerRecipeType）。
        /// 同时打印建造菜单字段（CanBuild / BuildIndex / GridIndex），主菜单阶段即可核对。
        /// </summary>
        private void LogBuildingVerification()
        {
            if (buildings.Count == 0)
            {
                return;
            }
            for (int i = 0; i < buildings.Count; i++)
            {
                RegisteredBuilding building = buildings[i];
                if (building.Item == null || building.Item.prefabDesc == null)
                {
                    log.LogWarning("[验证] 建筑 item " + building.ExpectedItemId + " 的 prefabDesc 为空，无法核对 assemblerRecipeType");
                    continue;
                }
                int type = (int)(byte)building.Item.prefabDesc.assemblerRecipeType;
                int sameType = 0;
                if (LDB.recipes != null && LDB.recipes.dataArray != null)
                {
                    for (int j = 0; j < LDB.recipes.dataArray.Length; j++)
                    {
                        RecipeProto proto = LDB.recipes.dataArray[j];
                        if (proto != null && (int)(byte)proto.Type == type)
                        {
                            sameType++;
                        }
                    }
                }
                log.LogInfo(string.Format(
                    "[验证] 建筑 item={0} model={1} → assemblerRecipeType={2}；LDB 中 type={2} 配方 {3} 条；" +
                    "CanBuild={4} BuildIndex={5} GridIndex={6}（可见性判据 recipe.Type == prefabDesc.assemblerRecipeType）",
                    building.ExpectedItemId, building.ExpectedModelId, type, sameType,
                    building.Item.CanBuild, building.Item.BuildIndex, building.Item.GridIndex));
            }
        }

        /// <summary>验证 b：宽配方长度未被截断（>6 原料 / >2 产物），并抽样联调指定的 recipe。</summary>
        private void LogWideRecipeVerification()
        {
            if (recipes.Count == 0)
            {
                return;
            }
            int wideIn = 0;
            int wideOut = 0;
            int bestInIndex = -1;
            int bestOutIndex = -1;
            for (int i = 0; i < recipes.Count; i++)
            {
                RecipeProto proto = recipes[i].Proto;
                if (proto == null)
                {
                    continue;
                }
                int inLen = proto.Items != null ? proto.Items.Length : 0;
                int outLen = proto.Results != null ? proto.Results.Length : 0;
                if (inLen > 6)
                {
                    wideIn++;
                }
                if (outLen > 2)
                {
                    wideOut++;
                }
                if (bestInIndex < 0 || inLen > Len(recipes[bestInIndex].Proto.Items))
                {
                    bestInIndex = i;
                }
                if (bestOutIndex < 0 || outLen > Len(recipes[bestOutIndex].Proto.Results))
                {
                    bestOutIndex = i;
                }
            }
            if (bestInIndex >= 0)
            {
                RecipeProto proto = recipes[bestInIndex].Proto;
                log.LogInfo(string.Format(
                    "[验证] 宽配方（原料最多）：ID={0} Items.Length={1} Results.Length={2} Type={3} GridIndex={4}",
                    proto.ID, Len(proto.Items), Len(proto.Results), (int)(byte)proto.Type, proto.GridIndex));
            }
            if (bestOutIndex >= 0)
            {
                RecipeProto proto = recipes[bestOutIndex].Proto;
                log.LogInfo(string.Format(
                    "[验证] 宽配方（产物最多）：ID={0} Items.Length={1} Results.Length={2} Type={3} GridIndex={4}",
                    proto.ID, Len(proto.Items), Len(proto.Results), (int)(byte)proto.Type, proto.GridIndex));
            }
            // 联调指名对象：recipe 4009（12 原料的 aluminium-mine）
            RecipeProto named = LDB.recipes != null ? LDB.recipes.Select(4009) : null;
            if (named != null)
            {
                log.LogInfo(string.Format(
                    "[验证] 指定宽配方 4009：Items.Length={0}（期望 12，未被截断）Results.Length={1} Type={2} GridIndex={3}",
                    Len(named.Items), Len(named.Results), (int)(byte)named.Type, named.GridIndex));
            }
            log.LogInfo(string.Format("[验证] 宽配方统计：>6 原料 {0} 条，>2 产物 {1} 条（注册口径 {2} 条）",
                wideIn, wideOut, wideRecipeCount));
        }

        /// <summary>验证 c：科技 IsLabTech / jellos / hash / 位置 / 分页（ID>=2000 → page=1 升级页）。</summary>
        private void LogTechVerification()
        {
            if (techs.Count == 0)
            {
                return;
            }
            int labTechs = 0;
            int techPage1 = 0;
            List<int> ids = new List<int>();
            for (int i = 0; i < techs.Count; i++)
            {
                TechProto proto = techs[i].Proto;
                if (proto == null)
                {
                    continue;
                }
                if (proto.IsLabTech)
                {
                    labTechs++;
                }
                if (proto.ID >= 2000)
                {
                    techPage1++;
                }
                if (ids.Count < 8)
                {
                    ids.Add(proto.ID);
                }
            }
            log.LogInfo("[验证] 科技 IsLabTech=true：" + labTechs + "/" + techs.Count +
                        "；ID>=2000（升级页 page=1）：" + techPage1 + "/" + techs.Count +
                        "；科技 ID 段 " + DescribeRange(TechIdList()) + "；样例 " + JoinInts(ids.ToArray()));
            LogOneTech(techs[0].Proto, "注册的第一条科技");
            TechProto named = LDB.techs != null ? LDB.techs.Select(2000) : null;
            if (named != null)
            {
                LogOneTech(named, "指定科技 2000");
            }
        }

        private void LogOneTech(TechProto proto, string label)
        {
            if (proto == null)
            {
                return;
            }
            log.LogInfo(string.Format(
                "[验证] {0}：ID={1} IsLabTech={2} jellos={3} [{4}] ItemPoints={5} [{6}] UnlockRecipes={7} HashNeeded={8} Position=({9:0},{10:0}) 期望页={11}",
                label, proto.ID, proto.IsLabTech,
                Len(proto.Items), JoinInts(proto.Items),
                Len(proto.ItemPoints), JoinInts(proto.ItemPoints),
                Len(proto.UnlockRecipes), proto.HashNeeded,
                proto.Position.x, proto.Position.y, proto.ID >= 2000 ? 1 : 0));
        }

        private static int Len(int[] array)
        {
            return array != null ? array.Length : 0;
        }

        private static string JoinInts(int[] values)
        {
            if (values == null || values.Length == 0)
            {
                return string.Empty;
            }
            StringBuilder builder = new StringBuilder();
            int limit = values.Length < 12 ? values.Length : 12;
            for (int i = 0; i < limit; i++)
            {
                if (i > 0)
                {
                    builder.Append(',');
                }
                builder.Append(values[i]);
            }
            if (values.Length > limit)
            {
                builder.Append("…");
            }
            return builder.ToString();
        }

        // ================================================================== model id capacity self-check
        /// <summary>
        /// 模型 ID 容量自检（崩溃根因证明，task-13）：
        /// 原版若干处 `new T[LDB.models.dataArray.Length + 64]` 后按**模型 ID** 下标写入
        /// （SkillSystem..ctor 10 个数组 / PrefabDescByModelIndex 2 处）。
        /// 只要 maxModelID + 1 &lt;= dataArray.Length + 64，就不会越界；否则 WARN 并提示会崩溃。
        /// 本插件已用 SkillSystemCtorFix / PrefabDescArrayFix 兜底，但内容侧应保持在安全余量内。
        /// </summary>
        private void CheckModelIdCapacity()
        {
            if (LDB.models == null || LDB.models.dataArray == null)
            {
                return;
            }
            ModelProto[] dataArray = LDB.models.dataArray;
            int maxId = 0;
            for (int i = 0; i < dataArray.Length; i++)
            {
                ModelProto proto = dataArray[i];
                if (proto != null && proto.ID > maxId)
                {
                    maxId = proto.ID;
                }
            }
            int capacity = dataArray.Length + 64;
            bool pass = maxId + 1 <= capacity;
            log.LogInfo(string.Format(
                "[验证] 模型 ID 容量自检：models.dataArray.Length={0}，最大模型 ID={1}，原版容量(len+64)={2}，" +
                "本 MOD 建筑模型 {3} 个 → {4}（SkillSystem..ctor / PrefabDescByModelIndex 按 ID 写数组，maxID+1 必须 <= 容量）",
                dataArray.Length, maxId, capacity, buildings.Count, pass ? "PASS" : "WARN"));
            if (!pass)
            {
                log.LogWarning("[验证] 模型 ID 超出原版容量 " + (maxId + 1 - capacity) +
                               " 项：已由 SkillSystemCtorFix/PrefabDescArrayFix 兜底，但若补丁失效会建新档/读档崩溃；" +
                               "建议内容侧把建筑模型 ID 降到安全段（如 666-683）");
            }
        }

        // ================================================================== duplicate id scan        /// <summary>
        /// LDB 数组重复 ID 扫描：其它 MOD（如 UXAssist 的 ProductionCatalog）会用 ToDictionary(ID) 建索引，
        /// LDB 里若出现重复 ID 会让它们的 UI 初始化抛 ArgumentException。这里只报错不改数据。
        /// 注册前扫描只能看到 vanilla，已加载 MOD 的 proto 要到 Preload 才并入 LDB，所以必须再做这一次兜底。
        /// </summary>
        private void ScanDuplicateIds()
        {
            ScanDuplicateIds("物品", LDB.items != null ? LDB.items.dataArray : null);
            ScanDuplicateIds("配方", LDB.recipes != null ? LDB.recipes.dataArray : null);
            ScanDuplicateIds("科技", LDB.techs != null ? LDB.techs.dataArray : null);
            ScanDuplicateIds("模型", LDB.models != null ? LDB.models.dataArray : null);
        }

        private void ScanDuplicateIds(string kind, Proto[] array)
        {
            if (array == null)
            {
                return;
            }
            Dictionary<int, int> counts = new Dictionary<int, int>();
            List<int> duplicates = new List<int>();
            for (int i = 0; i < array.Length; i++)
            {
                Proto proto = array[i];
                if (proto == null)
                {
                    continue;
                }
                int count;
                if (counts.TryGetValue(proto.ID, out count))
                {
                    counts[proto.ID] = count + 1;
                    if (count + 1 == 2)
                    {
                        duplicates.Add(proto.ID);
                    }
                }
                else
                {
                    counts[proto.ID] = 1;
                }
            }
            if (duplicates.Count == 0)
            {
                log.LogInfo("[自检] LDB." + kind + " 无重复 ID（" + array.Length + " 项）");
                return;
            }
            StringBuilder ids = new StringBuilder();
            for (int i = 0; i < duplicates.Count && i < 24; i++)
            {
                if (i > 0)
                {
                    ids.Append(',');
                }
                ids.Append(duplicates[i]);
            }
            log.LogError(string.Format("[自检] LDB.{0} 存在重复 ID {1} 个：{2}（会让其它 MOD 的 ToDictionary(ID) 抛重复键异常）",
                kind, duplicates.Count, ids));
            for (int i = 0; i < duplicates.Count && i < 3; i++)
            {
                StringBuilder names = new StringBuilder();
                for (int j = 0; j < array.Length; j++)
                {
                    Proto proto = array[j];
                    if (proto != null && proto.ID == duplicates[i])
                    {
                        if (names.Length > 0)
                        {
                            names.Append(" | ");
                        }
                        names.Append(proto.Name);
                    }
                }
                log.LogError("[自检] LDB." + kind + " 重复 ID " + duplicates[i] + " 的条目：" + names);
            }
        }
    }
}
