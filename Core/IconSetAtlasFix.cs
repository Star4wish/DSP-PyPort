using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace DSPPyPort.Core
{
    /// <summary>
    /// 原版 IconSet 图集索引修复（P0-2，2026-11-19）。
    ///
    /// 确诊（full.cs L295865 `IconSet` / L349733 `GameMain.CreateIconSet`）：
    /// - 图标索引 = **图集格子号**：`itemIconIndex[id] / recipeIconIndex[id] / techIconIndex[id]`（uint）；
    /// - 图集固定 2000×2000、25×25=625 格、每格 80×80（`kIconCountPerAxis=25`、`kIconSize=80`），
    ///   `0` 表示「无图标」（直接采样到空格/第 0 格）；
    /// - `IconSet.Create()` 在 `GameMain.Start()`（进入游戏/主菜单 demo 时）执行：
    ///   遍历 LDB 各 proto，取 `proto.iconSprite.texture` 用 `Graphics.CopyTexture` 拷进格子；
    ///   共享同一 Sprite 的 proto 复用同一格（`spriteIndexMap`）；格子用尽（>=625）时 `Debug.LogWarning("图标图集空间不足！")`
    ///   并把剩余索引设为 0。
    /// - 我们的 8 包有 3193 物品 + 5738 配方 + 733 科技，且图标是运行时自建 Sprite（多数在 Preload 之后才懒加载），
    ///   要么 Create 时 iconSprite 为 null、要么 9664 个图标远超 625 格 → **绝大多数索引=0**，
    ///   而 `UIRecipePicker`/`UIItemPicker` 等用 `iconSet.recipeIconIndex[id]` + `iconSet.texture` 采样，
    ///   于是全部显示第 0 格 → 用户看到的「全是铁矿图标」。
    ///
    /// 修复（引擎侧，**不需要 content 改格式**）：
    ///   1. Prefix：Create 期间抑制懒加载并清空本 MOD proto 的 _iconSprite，让图集只收录原版/其它 MOD 图标，
    ///      避免我们 9664 个图标挤爆 625 格（否则原版图标也会被挤掉）；
    ///   2. Postfix：统计图集已用格/空闲格；把 content/icons 里的 PNG 按「使用次数优先」确定性映射到空闲格
    ///      （我们的图标文件数通常远大于空闲格，因此按 usage 排序后 1:1 占用空闲格，剩余文件共享格子——
    ///      保证 picker 里能看到 K 种不同图标而不是同一张）；
    ///      按规则重写 `itemIconIndex/recipeIconIndex/techIconIndex/signalIconIndex` 并 `SetData` 到对应 ComputeBuffer；
    ///      `Graphics`/`SetPixels32` 把 PNG 缩放成 80×80 写进图集空闲格；最后 `texture.Apply(true)`。
    ///   3. 日志打印：已用格 V/625、空闲 F、distinct 图标 D、实际占用 K、冲突/复用数、映射 物品/配方/科技 数量、
    ///      未映射回退数；若无空闲格则 WARN（此时只能保持原版格位，需内容侧合并图标）。
    ///
    /// 限制与后续：图集只有 625 格，且要与原版/其它 MOD 共享；要 100% 一物品一图标，需要内容侧把
    /// 3642 张图标合并到「空闲格数量」以内（或接受部分图标复用）。本补丁保证不同图标可见（不再全是铁矿）。
    /// </summary>
    internal static class IconSetAtlasFix
    {
        internal static bool Installed { get; private set; }
        internal static int LastFreeCells { get; private set; }
        internal static int LastUsedCells { get; private set; }
        internal static bool InjectionDone { get; private set; }

        private static IconSet pendingIconSet;
        private static bool pendingInjection;
        private static int firstItemId = -1;

        private const int CellCount = 625;
        private const int IconSize = 80;
        private const int CellsPerAxis = 25;

        internal static void Install(Harmony harmony, ManualLogSource log)
        {
            try
            {
                MethodInfo create = AccessTools.Method(typeof(IconSet), "Create", new Type[0]);
                if (create == null)
                {
                    log.LogWarning("[图标图集] 找不到 IconSet.Create，图集索引修复未安装");
                    return;
                }
                harmony.Patch(create, prefix: new HarmonyMethod(typeof(IconSetAtlasFix)
                    .GetMethod("CreatePrefix", BindingFlags.Static | BindingFlags.NonPublic)));
                harmony.Patch(create, postfix: new HarmonyMethod(typeof(IconSetAtlasFix)
                    .GetMethod("CreatePostfix", BindingFlags.Static | BindingFlags.NonPublic)));
                Installed = true;
                log.LogInfo("[图标图集] 已安装 IconSet 图集索引修复（Create 后把 PY 图标注入空闲格并重写 item/recipe/techIconIndex）");
            }
            catch (Exception ex)
            {
                log.LogError("[图标图集] 安装失败（配方/物品选择器图标可能仍为同一张）：" + ex);
            }
        }

        private static bool CreatePrefix(IconSet __instance)
        {
            try
            {
                if (__instance == null || __instance.loaded)
                {
                    return true;
                }
                // 抑制懒加载 + 清空自有 proto 图标：让原版/其它 MOD 图标先占图集，避免 9664 个图标把 625 格挤爆。
                IconLoader.SuppressLazyLoad = true;
                IconLoader.ClearOwnSpriteFields();
            }
            catch (Exception ex)
            {
                try
                {
                    Plugin.Log.LogWarning("[图标图集] Create 前处理失败（继续原流程）：" + ex.Message);
                }
                catch
                {
                }
            }
            return true;
        }

        private static void CreatePostfix(IconSet __instance)
        {
            IconLoader.SuppressLazyLoad = false;
            try
            {
                if (__instance == null || __instance.texture == null)
                {
                    return;
                }
                // 注意：GameMain.Start（菜单 demo）里的 CreateIconSet 可能早于 mods 并入 LDB
                // （日志顺序：Create → 之后才 LDBTool Pre Loading）。此时我们的物品还没进 LDB：
                // 先挂起注入，等 Update 里 LDB 完整后再注入（同一 iconSet 对象会被后续 UI 使用）。
                if (firstItemId < 0)
                {
                    foreach (int id in IconLoader.GetItemSpecs().Keys)
                    {
                        firstItemId = id;
                        break;
                    }
                }
                bool ldbReady = firstItemId < 0 || (LDB.items != null && LDB.items.Exist(firstItemId));
                if (ldbReady)
                {
                    Inject(__instance, Plugin.Log);
                    InjectionDone = true;
                }
                else
                {
                    pendingIconSet = __instance;
                    pendingInjection = true;
                    Plugin.Log.LogInfo("[图标图集] IconSet 已创建但 LDB 尚未并入本 MOD 物品，注入推迟到 LDB 就绪后（Update）");
                }
            }
            catch (Exception ex)
            {
                try
                {
                    Plugin.Log.LogError("[图标图集] 注入失败（图标索引保持原版/0）：" + ex);
                }
                catch
                {
                }
            }
            try
            {
                // 急切模式：注入完成后恢复 sprite（懒加载模式保持 null，由 getter 首访加载）
                if (!IconLoader.LazyMode && InjectionDone)
                {
                    IconLoader.ApplyAllEager();
                }
            }
            catch
            {
            }
        }

        /// <summary>Update 探针：推迟注入（LDB 完整后）。</summary>
        internal static void TryInjectPending(ManualLogSource log)
        {
            if (!pendingInjection || pendingIconSet == null || InjectionDone)
            {
                return;
            }
            try
            {
                if (firstItemId < 0)
                {
                    foreach (int id in IconLoader.GetItemSpecs().Keys)
                    {
                        firstItemId = id;
                        break;
                    }
                }
                if (firstItemId >= 0 && (LDB.items == null || !LDB.items.Exist(firstItemId)))
                {
                    return;
                }
                Inject(pendingIconSet, log);
                InjectionDone = true;
                pendingInjection = false;
                if (!IconLoader.LazyMode)
                {
                    IconLoader.ApplyAllEager();
                }
            }
            catch (Exception ex)
            {
                pendingInjection = false;
                log.LogError("[图标图集] 推迟注入失败：" + ex);
            }
        }

        // ------------------------------------------------------------------ injection
        private static void Inject(IconSet iconSet, ManualLogSource log)
        {
            if (iconSet == null || iconSet.texture == null || iconSet.itemIconIndex == null ||
                iconSet.recipeIconIndex == null || iconSet.techIconIndex == null)
            {
                log.LogWarning("[图标图集] IconSet 字段不完整，跳过注入");
                return;
            }

            bool[] used = new bool[CellCount];
            MarkUsed(iconSet.itemIconIndex, used);
            MarkUsed(iconSet.recipeIconIndex, used);
            MarkUsed(iconSet.techIconIndex, used);
            MarkUsed(iconSet.veinIconIndex, used);
            MarkUsed(iconSet.signalIconIndex, used);
            int usedCount = 0;
            for (int i = 0; i < CellCount; i++)
            {
                if (used[i])
                {
                    usedCount++;
                }
            }
            List<int> freeCells = new List<int>();
            for (int cell = 1; cell < CellCount; cell++) // 0 保留为「无图标」语义，不占用
            {
                if (!used[cell])
                {
                    freeCells.Add(cell);
                }
            }
            LastUsedCells = usedCount;
            LastFreeCells = freeCells.Count;
            log.LogInfo("[图标图集] 图集现状：已用格 " + usedCount + "/" + CellCount + "，空闲格 " + freeCells.Count +
                        "（原版/其它 MOD 图标保留；我们的图标将注入空闲格）");
            if (freeCells.Count == 0)
            {
                log.LogWarning("[图标图集] 没有空闲格可注入：我们的物品/配方/科技图标仍会显示图集第 0 格（同一张图）。" +
                               "需要内容侧合并图标到更少数量（<= 空闲格），或减少其它 MOD 的图标占用。");
                return;
            }

            // 统计图标文件使用次数：物品权重 2（配方通常跟随产物，实际复用物品图标），科技 1。
            Dictionary<string, int> usage = new Dictionary<string, int>(StringComparer.Ordinal);
            Dictionary<int, string> itemFile = new Dictionary<int, string>();
            Dictionary<int, string> techFile = new Dictionary<int, string>();
            Dictionary<int, string> recipeFile = new Dictionary<int, string>();
            foreach (KeyValuePair<int, IconSpec> pair in IconLoader.GetItemSpecs())
            {
                if (!string.IsNullOrEmpty(pair.Value.FileName))
                {
                    itemFile[pair.Key] = pair.Value.FileName;
                    int count;
                    usage.TryGetValue(pair.Value.FileName, out count);
                    usage[pair.Value.FileName] = count + 2;
                }
            }
            foreach (KeyValuePair<int, IconSpec> pair in IconLoader.GetTechSpecs())
            {
                if (!string.IsNullOrEmpty(pair.Value.FileName))
                {
                    techFile[pair.Key] = pair.Value.FileName;
                    int count;
                    usage.TryGetValue(pair.Value.FileName, out count);
                    usage[pair.Value.FileName] = count + 1;
                }
            }
            foreach (KeyValuePair<int, IconSpec> pair in IconLoader.GetRecipeSpecs())
            {
                if (!string.IsNullOrEmpty(pair.Value.FileName))
                {
                    recipeFile[pair.Key] = pair.Value.FileName;
                }
            }

            List<string> files = new List<string>(usage.Keys);
            files.Sort(delegate(string a, string b)
            {
                int ua;
                int ub;
                usage.TryGetValue(a, out ua);
                usage.TryGetValue(b, out ub);
                if (ua != ub)
                {
                    return ub.CompareTo(ua); // 使用次数多的优先占独立格
                }
                return string.CompareOrdinal(a, b);
            });

            Dictionary<string, int> fileCell = new Dictionary<string, int>(StringComparer.Ordinal);
            int drawn = 0;
            int reused = 0;
            for (int i = 0; i < files.Count; i++)
            {
                int cell;
                if (i < freeCells.Count)
                {
                    cell = freeCells[i];
                    fileCell[files[i]] = cell;
                    if (DrawFile(iconSet, files[i], cell, log))
                    {
                        drawn++;
                    }
                }
                else
                {
                    // 空闲格不足：剩余文件共享已注入格（按 hash 稳定分配，保证不同文件尽量分散）
                    cell = freeCells[HashToIndex(files[i], freeCells.Count)];
                    fileCell[files[i]] = cell;
                    reused++;
                }
            }
            iconSet.texture.Apply(true);
            int representativeCell = freeCells[0];
            log.LogInfo("[图标图集] 注入完成：distinct 图标文件 " + files.Count + " 个 → 独立占用 " + drawn +
                        " 格、复用 " + reused + " 个（空闲格 " + freeCells.Count + "）；图集纹理已 Apply");

            // ---- 重写索引 ----
            int mappedItems = 0;
            int mappedRecipes = 0;
            int mappedTechs = 0;
            int fallback = 0;
            foreach (KeyValuePair<int, string> pair in itemFile)
            {
                int cell;
                if (fileCell.TryGetValue(pair.Value, out cell))
                {
                    iconSet.itemIconIndex[pair.Key] = (uint)cell;
                    SetSignal(iconSet, pair.Key, cell, 0);
                    mappedItems++;
                }
            }
            foreach (KeyValuePair<int, string> pair in techFile)
            {
                int cell;
                if (fileCell.TryGetValue(pair.Value, out cell))
                {
                    iconSet.techIconIndex[pair.Key] = (uint)cell;
                    SetSignal(iconSet, pair.Key, cell, 40000);
                    mappedTechs++;
                }
            }
            foreach (KeyValuePair<int, string> pair in recipeFile)
            {
                int cell;
                if (fileCell.TryGetValue(pair.Value, out cell))
                {
                    iconSet.recipeIconIndex[pair.Key] = (uint)cell;
                    SetSignal(iconSet, pair.Key, cell, 20000);
                    mappedRecipes++;
                }
            }
            // 无自定义 icon_file 的配方：跟随产物物品的格
            foreach (KeyValuePair<int, IconSpec> pair in IconLoader.GetRecipeSpecs())
            {
                if (!string.IsNullOrEmpty(pair.Value.FileName))
                {
                    continue;
                }
                int cell = 0;
                try
                {
                    RecipeProto proto = LDB.recipes.Select(pair.Key);
                    if (proto != null && proto.Results != null && proto.Results.Length > 0)
                    {
                        ItemProto product = LDB.items.Select(proto.Results[0]);
                        if (product != null)
                        {
                            cell = (int)iconSet.itemIconIndex[product.ID];
                        }
                    }
                }
                catch
                {
                }
                if (cell == 0)
                {
                    cell = representativeCell;
                    fallback++;
                }
                iconSet.recipeIconIndex[pair.Key] = (uint)cell;
                SetSignal(iconSet, pair.Key, cell, 20000);
                mappedRecipes++;
            }
            // 只有 fallback 图（没有 icon_file）的物品/科技 → 代表性格
            int replacedFallback = 0;
            foreach (KeyValuePair<int, IconSpec> pair in IconLoader.GetItemSpecs())
            {
                if (itemFile.ContainsKey(pair.Key))
                {
                    continue;
                }
                iconSet.itemIconIndex[pair.Key] = (uint)representativeCell;
                SetSignal(iconSet, pair.Key, representativeCell, 0);
                replacedFallback++;
            }
            foreach (KeyValuePair<int, IconSpec> pair in IconLoader.GetTechSpecs())
            {
                if (techFile.ContainsKey(pair.Key))
                {
                    continue;
                }
                iconSet.techIconIndex[pair.Key] = (uint)representativeCell;
                SetSignal(iconSet, pair.Key, representativeCell, 40000);
                replacedFallback++;
            }

            // ---- 上传 ComputeBuffer（picker/物流/垃圾桶等 shader 使用）----
            try
            {
                if (iconSet.itemIconIndexBuffer != null)
                {
                    iconSet.itemIconIndexBuffer.SetData(iconSet.itemIconIndex);
                }
                if (iconSet.recipeIconIndexBuffer != null)
                {
                    iconSet.recipeIconIndexBuffer.SetData(iconSet.recipeIconIndex);
                }
                if (iconSet.techIconIndexBuffer != null)
                {
                    iconSet.techIconIndexBuffer.SetData(iconSet.techIconIndex);
                }
                if (iconSet.signalIconIndexBuffer != null)
                {
                    iconSet.signalIconIndexBuffer.SetData(iconSet.signalIconIndex);
                }
            }
            catch (Exception ex)
            {
                log.LogWarning("[图标图集] 上传 ComputeBuffer 失败（部分 UI 可能不生效）：" + ex.Message);
            }

            log.LogInfo(string.Format(
                "[图标图集] 索引重写完成：物品 {0} 个 / 科技 {1} 个 / 配方 {2} 个使用注入图标；无文件回退到代表性格 {3} 个；" +
                "代表格={4}（若图标数量 > 空闲格，部分物品共用同一格，但不会再全部显示第 0 格）",
                mappedItems, mappedTechs, mappedRecipes, replacedFallback + fallback, representativeCell));
            if (files.Count > freeCells.Count)
            {
                log.LogWarning(string.Format(
                    "[图标图集] 图标去重文件 {0} 个 > 空闲图集格 {1} 个：已按使用次数优先分配独立格，" +
                    "其余复用（picker 仍可见 {1} 种不同图标）。若要 1:1 图标，需要内容侧合并到 <= {1} 个代表性图标。",
                    files.Count, freeCells.Count));
            }

            // ---- 复核：我们的物品/配方/科技图标索引是否已从 0 变成注入格 ----
            try
            {
                int itemTotal = 0;
                int itemOk = 0;
                foreach (int id in IconLoader.GetItemSpecs().Keys)
                {
                    itemTotal++;
                    if (iconSet.itemIconIndex[id] > 0)
                    {
                        itemOk++;
                    }
                }
                int techTotal = 0;
                int techOk = 0;
                foreach (int id in IconLoader.GetTechSpecs().Keys)
                {
                    techTotal++;
                    if (iconSet.techIconIndex[id] > 0)
                    {
                        techOk++;
                    }
                }
                int recipeTotal = 0;
                int recipeOk = 0;
                foreach (int id in IconLoader.GetRecipeSpecs().Keys)
                {
                    recipeTotal++;
                    if (iconSet.recipeIconIndex[id] > 0)
                    {
                        recipeOk++;
                    }
                }
                log.LogInfo(string.Format(
                    "[图标图集] 索引复核：物品 {0}/{1}、科技 {2}/{3}、配方 {4}/{5} 的图集索引 > 0（0 表示会显示第 0 格）",
                    itemOk, itemTotal, techOk, techTotal, recipeOk, recipeTotal));
                if (itemOk == 0 && itemTotal > 0)
                {
                    log.LogWarning("[图标图集] 复核未通过：所有物品索引仍为 0，请把该日志发给 mod-dev");
                }
            }
            catch
            {
            }
        }

        private static void SetSignal(IconSet iconSet, int id, int cell, int offset)
        {
            try
            {
                int index = id + offset;
                if (iconSet.signalIconIndex != null && index >= 0 && index < iconSet.signalIconIndex.Length)
                {
                    iconSet.signalIconIndex[index] = (uint)cell;
                }
            }
            catch
            {
            }
        }

        private static void MarkUsed(uint[] array, bool[] used)
        {
            if (array == null)
            {
                return;
            }
            for (int i = 0; i < array.Length; i++)
            {
                uint value = array[i];
                if (value > 0 && value < CellCount)
                {
                    used[value] = true;
                }
            }
        }

        private static int HashToIndex(string text, int count)
        {
            int hash = 17;
            for (int i = 0; i < text.Length; i++)
            {
                hash = hash * 31 + text[i];
            }
            int index = hash % count;
            if (index < 0)
            {
                index += count;
            }
            return index;
        }

        private static bool DrawFile(IconSet iconSet, string fileName, int cell, ManualLogSource log)
        {
            try
            {
                Sprite sprite = IconLoader.LoadSpriteForAtlas(fileName);
                if (sprite == null)
                {
                    return false;
                }
                Texture2D source = sprite.texture;
                if (source == null)
                {
                    return false;
                }
                Color32[] src = source.GetPixels32();
                int srcW = source.width;
                int srcH = source.height;
                if (srcW <= 0 || srcH <= 0 || src.Length < srcW * srcH)
                {
                    return false;
                }
                Color32[] dst = new Color32[IconSize * IconSize];
                for (int y = 0; y < IconSize; y++)
                {
                    int sy = y * srcH / IconSize;
                    int srcRow = sy * srcW;
                    int dstRow = y * IconSize;
                    for (int x = 0; x < IconSize; x++)
                    {
                        int sx = x * srcW / IconSize;
                        dst[dstRow + x] = src[srcRow + sx];
                    }
                }
                int cellX = (cell % CellsPerAxis) * IconSize;
                int cellY = (cell / CellsPerAxis) * IconSize;
                iconSet.texture.SetPixels32(cellX, cellY, IconSize, IconSize, dst);
                return true;
            }
            catch (Exception ex)
            {
                try
                {
                    log.LogWarning("[图标图集] 绘制 " + fileName + " 失败：" + ex.Message);
                }
                catch
                {
                }
                return false;
            }
        }
    }
}
