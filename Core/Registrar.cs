using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using CommonAPI.Systems;
using CommonAPI.Systems.ModLocalization;
using UnityEngine;

namespace DSPPyPort.Core
{
    internal sealed class RegisteredItem
    {
        internal ItemDef Def;
        internal ItemProto Proto;
        internal int ExpectedId;
    }

    internal sealed class RegisteredBuilding
    {
        internal BuildingDef Def;
        internal ItemProto Item;
        internal ModelProto Model;
        internal int ExpectedItemId;
        internal int ExpectedModelId;
    }

    internal sealed class RegisteredRecipe
    {
        internal RecipeDef Def;
        internal RecipeProto Proto;
        internal int ExpectedId;
        internal int AssignedGridIndex;
    }

    internal sealed class RegisteredTech
    {
        internal TechDef Def;
        internal TechProto Proto;
        internal int ExpectedId;
    }

    internal sealed class Translation
    {
        internal string En = string.Empty;
        internal string Cn = string.Empty;
    }

    /// <summary>
    /// 内容注册器（partial 拆分）：
    ///   Registrar.cs           字段/入口/汇总/ID 记账/格位分配/占用冲突扫描
    ///   Registrar.Items.cs     物品 + 自建建筑
    ///   Registrar.Content.cs   配方 + 科技
    ///   Registrar.PostLoad.cs  Preload 收尾回填 / Update 探针 / 图标
    ///   Registrar.Verify.cs    自检 + 联调验证输出
    ///
    /// 注册顺序固定（手册 §1）：物品 → 模型/建筑 → 配方 → 科技 → 本地化
    /// →（onLoadingFinished）PrefabDesc/标志回填 →（Update 探针）LDBTool 覆盖后再回填一次。
    /// </summary>
    internal sealed partial class Registrar
    {
        private const int BuildingRecipeTypeMin = 60;
        private const int BuildingRecipeTypeMax = 79;
        private const int RecipeGridFloor = 1101;
        private const int AutoRecipeGridStart = 5000;
        private const int AutoMaterialGridStart = 4001;
        private const int AutoBuildingGridStart = 5001;
        private const int AutoBuildIndexStart = 900;
        private const int AutoModelIdStart = 686;
        private const int MaxModelArraySize = 2048;

        private readonly LoadedContent content;
        private readonly ManualLogSource log;
        private readonly string contentRoot;

        private readonly List<RegisteredItem> items = new List<RegisteredItem>();
        private readonly List<RegisteredBuilding> buildings = new List<RegisteredBuilding>();
        private readonly List<RegisteredRecipe> recipes = new List<RegisteredRecipe>();
        private readonly List<RegisteredTech> techs = new List<RegisteredTech>();

        private readonly HashSet<int> usedItemIds = new HashSet<int>();
        private readonly HashSet<int> usedModelIds = new HashSet<int>();
        private readonly HashSet<int> usedRecipeIds = new HashSet<int>();
        private readonly HashSet<int> usedTechIds = new HashSet<int>();
        private readonly HashSet<byte> usedCustomRecipeTypes = new HashSet<byte>();

        // 注册前 LDB 里已占用的 ID（用于冲突预警与冲突兜底）
        private readonly HashSet<int> preOccupiedItems = new HashSet<int>();
        private readonly HashSet<int> preOccupiedRecipes = new HashSet<int>();
        private readonly HashSet<int> preOccupiedTechs = new HashSet<int>();
        private readonly HashSet<int> preOccupiedModels = new HashSet<int>();

        private readonly Dictionary<string, Translation> translations =
            new Dictionary<string, Translation>(StringComparer.Ordinal);
        private readonly HashSet<int> registeredItemIdList = new HashSet<int>();

        /// <summary>
        /// 已知其它 MOD 的模型号段（不依赖是否已加载，避免插件加载顺序导致撞号）：
        ///   Molecular Assembly Hub 模型 666；Photon Stock Exchange 模型 667；DSP-MechDawn 模型 700-708。
        /// 这些 MOD 的模型是硬编码 ID，且它们可能在我们之后才 Awake（此时 CommonAPI.models 里还看不到），
        /// 所以必须作为「保留段」参与建筑模型段的安全检查（否则 LDB 会出现重复模型 ID）。
        /// </summary>
        private static readonly HashSet<int> ReservedModelIds = new HashSet<int>(
            new int[] { 666, 667, 700, 701, 702, 703, 704, 705, 706, 707, 708 });

        private static FieldInfo commonApiModelsField;
        private bool buildingModelIdsChecked;

        // 物品栏格位分配（2026-11-17 重做）
        // GridIndex 语义（反编译 full.cs）：grid = tab*1000 + (row+1)*100 + (col+1)；
        // 物品筛选器/选择器网格 row 0-7、col 0-13（每 tab 112 格），只渲染 tab == currentType。
        // 策略：tab1-5 合法格确定性分配（优先 tab4/5，避免抢原版 tab1-3 的可见性），
        //       容量耗尽后确定性降级到 tab6+ 顺序格（筛选器不渲染这些格，但不再制造重复格）。
        private static readonly int[] VisibleTabPriority = new int[] { 4, 5, 1, 2, 3 };
        private const int GridRows = 8;                    // row 0..7 → (row+1)=1..8
        private const int GridCols = 14;                   // col 0..13 → (col+1)=1..14
        private const int CellsPerTab = GridRows * GridCols; // 112
        private const int VisibleTabCount = 5;
        private const int VisibleCapacity = CellsPerTab * VisibleTabCount; // 560
        private const int OverflowFirstTab = 6;
        private const int OverflowLastTab = 59;
        private readonly HashSet<int> occupiedItemGrids = new HashSet<int>();
        private readonly HashSet<int> buildingItemGrids = new HashSet<int>();
        private int visibleCellCursor;
        private int overflowCellCursor;
        private int itemGridVisibleCount;
        private int itemGridHiddenCount;
        private int itemGridExhaustedCount;
        private int itemGridExhaustFallback;
        private int itemGridFallbackBuildingCount;
        private int gridScanFromLdb;
        private int gridScanFromCfg;
        private int gridScanSkippedOwnCfg;
        private int gridReservedBuildings;
        private int gridVisibleFreeCapacity;
        private bool gridFinalized;
        private int nextBuildIndex = AutoBuildIndexStart;
        private int nextModelId = AutoModelIdStart;
        private int nextRecipeGrid = AutoRecipeGridStart + 1;
        private int nextTechPositionIndex;

        // 计数
        private int skippedInvalid;
        private int mappedCount;
        private int fluidCount;
        private int wideRecipeCount;
        private int localeCount;
        private int missingNameCount;
        private int missingIconFallbacks;
        private int skippedGenerator;

        // 收尾状态
        private bool postLoadReached;
        private bool postEditsApplied;
        private int probeAttempts;
        private bool probeTimeoutReported;
        private int lastBuildingApplied = -1;
        private bool selfCheckLogged;
        private int lastMissingItems = -1;
        private int lastMissingRecipes = -1;
        private int lastMissingTechs = -1;

        internal Registrar(LoadedContent content, ManualLogSource log, string contentRoot)
            : this(content, log, contentRoot, true)
        {
        }

        internal Registrar(LoadedContent content, ManualLogSource log, string contentRoot, bool autoBuildIndexMode)
        {
            this.content = content;
            this.log = log;
            this.contentRoot = contentRoot;
            this.autoBuildIndex = autoBuildIndexMode;
            ReportOccupiedIdsBeforeRegistration();
            InitItemGridAllocation();
            InitBuildIndexAllocation();
        }

        // ================================================================== entry
        internal void RegisterAll()
        {
            for (int m = 0; m < content.Modules.Count; m++)
            {
                ModuleContent module = content.Modules[m];
                skippedGenerator += module.GeneratorSkipped;
                if (module.Refused)
                {
                    log.LogError("[PyPort] 模块 " + module.Id + " 因 ID 冲突拒绝注册（" + module.RefuseReason +
                                 "）；本模块的物品/建筑/配方/科技全部不注册");
                }
            }
            log.LogInfo("[PyPort] 开始注册原型（模块 " + DescribeModules() + "）");
            Perf.Mark(log, "注册准备（manifest/冲突扫描/格位扫描）");
            RegisterItems();
            Perf.Mark(log, "物品注册");
            RegisterBuildings();
            FinalizeGridAllocation();
            Perf.Mark(log, "建筑注册 + 格位定稿");
            LogBuildIndexSummary();
            RegisterRecipes();
            Perf.Mark(log, "配方注册");
            RegisterTechs();
            Perf.Mark(log, "科技注册");
            RegisterLocalization();
            Perf.Mark(log, "本地化注册");
            CleanupIconDescs();
            Perf.Mark(log, "图标描述清理");
            try
            {
                EnsureModelArrayCapacity();
            }
            catch (Exception ex)
            {
                log.LogError("[PyPort] 预扩容 LDB.models.modelArray 失败（CommonAPI Preload 可能越界）：" + ex);
            }
            LogSummary();
        }

        /// <summary>
        /// 预扩容 `LDB.models.modelArray`（CommonAPI Preload 之前的保险，2026-11-18 实测 bug）。
        ///
        /// CommonAPI 1.6.7 的 OnPostAdd 模型循环里：`if (model.ID > LDB.models.modelArray.Length)
        /// Array.Resize(ref modelArray, model.ID + 64);` —— 条件是 `&gt;` 而不是 `&gt;=`，
        /// 当模型 ID 恰好等于数组长度（初始 664+64=728）时不会扩容，随后 `modelArray[ID] = model` 越界，
        /// 导致整个 OnPostAdd 中断（本次 720-737 的内容在 ID=728 处触发）。
        ///
        /// 这里在本插件注册完成后、CommonAPI Preload 之前，把 modelArray 主动扩到 `maxModelID + 64`，
        /// 覆盖所有本 MOD 模型；其它 MOD 的模型仍由 CommonAPI 自身逻辑处理（它们 ID 较小时不受影响）。
        /// </summary>
        private void EnsureModelArrayCapacity()
        {
            if (LDB.models == null)
            {
                return;
            }
            int maxId = 0;
            for (int i = 0; i < buildings.Count; i++)
            {
                RegisteredBuilding building = buildings[i];
                if (building.ExpectedModelId > maxId)
                {
                    maxId = building.ExpectedModelId;
                }
                if (building.Def != null && building.Def.ModelId > maxId)
                {
                    maxId = building.Def.ModelId;
                }
            }
            foreach (int id in usedModelIds)
            {
                if (id > maxId)
                {
                    maxId = id;
                }
            }
            if (maxId <= 0)
            {
                return;
            }
            int required = maxId + 64;
            ModelProto[] current = LDB.models.modelArray;
            int currentLength = current != null ? current.Length : 0;
            if (currentLength >= required)
            {
                log.LogInfo("[兼容] LDB.models.modelArray 容量足够：length=" + currentLength + "，最大模型 ID=" + maxId);
                return;
            }
            ModelProto[] expanded = new ModelProto[required];
            if (current != null)
            {
                Array.Copy(current, expanded, Math.Min(current.Length, expanded.Length));
            }
            LDB.models.modelArray = expanded;
            log.LogInfo(string.Format(
                "[兼容] 已预扩容 LDB.models.modelArray：{0} → {2}（最大模型 ID={1}，避开 CommonAPI `ID > Length` 的 off-by-one 越界）",
                currentLength, maxId, required));
        }

        private string DescribeModules()
        {
            if (content.Modules.Count == 0)
            {
                return "无";
            }
            string text = string.Empty;
            for (int i = 0; i < content.Modules.Count; i++)
            {
                if (i > 0)
                {
                    text += "、";
                }
                ModuleContent module = content.Modules[i];
                text += module.Id + "(" + module.Name + (module.Refused ? "，已拒绝" : string.Empty) + ")";
            }
            return text;
        }

        // ================================================================== pre-registration id scan
        /// <summary>
        /// 注册前扫描 LDB 里已有的 proto ID（本机实测为 vanilla 全量；其它 MOD 的 proto 要到 Preload
        /// 才并入 LDB，因此对已加载 MOD 的冲突靠注册后的重复 ID 扫描兜底），并对内容包计划使用的 ID
        /// 做冲突预警。
        ///
        /// 2026-11-16 实测（游戏 0.10.35）：**原版升级页科技占用到 6106**（1901-1904、2101-2106、
        /// 2201-2208 … 6101-6106 共 315 条），手册「原版科技全部 &lt;2000」的旧结论不成立。
        /// 内容包的科技段必须先确认空段再用（pyrawores 最终落在 4207-4318）。
        /// 任一类别冲突时**拒绝整个内容模块**：不注册半组内容，避免配方/科技引用悬空、污染存档。
        /// </summary>
        private void ReportOccupiedIdsBeforeRegistration()
        {
            List<int> plannedItems = new List<int>();
            List<int> plannedBuildings = new List<int>();
            List<int> plannedModels = new List<int>();
            List<int> plannedRecipes = new List<int>();
            List<int> plannedTechs = new List<int>();
            for (int m = 0; m < content.Modules.Count; m++)
            {
                ModuleContent module = content.Modules[m];
                for (int i = 0; i < module.Items.Count; i++)
                {
                    if (!module.Items[i].Skip && !module.Items[i].IsBuilding)
                    {
                        plannedItems.Add(module.Items[i].Id);
                    }
                }
                for (int i = 0; i < module.Buildings.Count; i++)
                {
                    BuildingDef def = module.Buildings[i];
                    if (def.Skip)
                    {
                        continue;
                    }
                    plannedBuildings.Add(def.ItemId);
                    if (def.ModelId > 0)
                    {
                        plannedModels.Add(def.ModelId);
                    }
                }
                for (int i = 0; i < module.Recipes.Count; i++)
                {
                    if (!module.Recipes[i].Skip)
                    {
                        plannedRecipes.Add(module.Recipes[i].Id);
                    }
                }
                for (int i = 0; i < module.Techs.Count; i++)
                {
                    if (!module.Techs[i].Skip)
                    {
                        plannedTechs.Add(module.Techs[i].Id);
                    }
                }
            }

            bool[] occupiedItems = new bool[ContentLoader.MaxItemId];
            bool[] occupiedRecipes = new bool[ContentLoader.MaxRecipeId];
            bool[] occupiedTechs = new bool[ContentLoader.MaxTechId];
            bool[] occupiedModels = new bool[MaxModelArraySize];
            int existingItems = ScanOccupied(LDB.items != null ? LDB.items.dataArray : null, occupiedItems, preOccupiedItems);
            int existingRecipes = ScanOccupied(LDB.recipes != null ? LDB.recipes.dataArray : null, occupiedRecipes, preOccupiedRecipes);
            int existingTechs = ScanOccupied(LDB.techs != null ? LDB.techs.dataArray : null, occupiedTechs, preOccupiedTechs);
            int existingModels = ScanOccupied(LDB.models != null ? LDB.models.dataArray : null, occupiedModels, preOccupiedModels);
            log.LogInfo(string.Format("[冲突检查] 注册前 LDB 已有 proto：物品 {0} / 配方 {1} / 科技 {2} / 模型 {3}",
                existingItems, existingRecipes, existingTechs, existingModels));
            log.LogInfo("[冲突检查] 已占用物品段：" + DescribeIdRanges(occupiedItems, 0, 16));
            log.LogInfo("[冲突检查] 已占用配方段：" + DescribeIdRanges(occupiedRecipes, 0, 16));
            log.LogInfo("[冲突检查] 已占用科技段：" + DescribeIdRanges(occupiedTechs, 0, 32));
            log.LogInfo("[冲突检查] 已占用模型段：" + DescribeIdRanges(occupiedModels, 0, 16));

            int conflicts = 0;
            conflicts += CheckPlannedIds("物品", plannedItems, LDB.items != null ? LDB.items.dataArray : null).Count;
            conflicts += CheckPlannedIds("配方", plannedRecipes, LDB.recipes != null ? LDB.recipes.dataArray : null).Count;
            conflicts += CheckPlannedIds("模型", plannedModels, LDB.models != null ? LDB.models.dataArray : null).Count;
            List<int> techConflicts = CheckPlannedIds("科技", FilterTechSlotsToReplace(plannedTechs),
                LDB.techs != null ? LDB.techs.dataArray : null);
            conflicts += techConflicts.Count;

            log.LogInfo("[冲突检查] 内容计划段：物品 " + DescribePlannedRange(plannedItems) +
                        " / 配方 " + DescribePlannedRange(plannedRecipes) +
                        " / 科技 " + DescribePlannedRange(plannedTechs) +
                        " / 模型 " + DescribePlannedRange(plannedModels));
            log.LogInfo("[冲突检查] 2000-11999 科技空闲段（长度>=112，按长度降序）：" +
                        DescribeFreeRanges(occupiedTechs, 2000, ContentLoader.MaxTechId, 112, 6));
            log.LogInfo("[冲突检查] 2000-11999 科技空闲段（长度>=50）：" +
                        DescribeFreeRanges(occupiedTechs, 2000, ContentLoader.MaxTechId, 50, 12));

            if (conflicts > 0)
            {
                for (int m = 0; m < content.Modules.Count; m++)
                {
                    content.Modules[m].Refused = true;
                    content.Modules[m].RefuseReason = "内容 ID 与已有 proto 冲突（见上方 [冲突检查]）";
                }
                log.LogError("[冲突检查] 共 " + conflicts + " 个计划 ID 与已有 proto 冲突：本次拒绝注册全部内容模块，" +
                             "避免半组注册造成存档污染。请让生成器把冲突类别迁到空闲段后重新部署。");
                if (techConflicts.Count > 0)
                {
                    log.LogError("[冲突检查] 科技冲突 ID " + DescribeIdList(techConflicts) +
                                 " 与原版升级页科技重叠；可用空段首选 " +
                                 FirstFreeRangeSuggestion(occupiedTechs, 2000, ContentLoader.MaxTechId, plannedTechs.Count));
                }
            }
            else
            {
                log.LogInfo("[冲突检查] 计划 ID 与已有 proto 无冲突，继续注册");
            }
        }

        private int ScanOccupied<T>(T[] array, bool[] flags, HashSet<int> target) where T : Proto
        {
            if (array == null)
            {
                return 0;
            }
            int count = 0;
            for (int i = 0; i < array.Length; i++)
            {
                Proto proto = array[i];
                if (proto == null)
                {
                    continue;
                }
                count++;
                if (target != null)
                {
                    target.Add(proto.ID);
                }
                if (flags != null && proto.ID >= 0 && proto.ID < flags.Length)
                {
                    flags[proto.ID] = true;
                }
            }
            return count;
        }

        private List<int> CheckPlannedIds(string kind, List<int> planned, Proto[] existing)
        {
            List<int> conflicts = new List<int>();
            if (existing == null || planned.Count == 0)
            {
                return conflicts;
            }
            HashSet<int> existingIds = new HashSet<int>();
            for (int i = 0; i < existing.Length; i++)
            {
                Proto proto = existing[i];
                if (proto != null)
                {
                    existingIds.Add(proto.ID);
                }
            }
            List<string> samples = new List<string>();
            for (int i = 0; i < planned.Count; i++)
            {
                if (!existingIds.Contains(planned[i]))
                {
                    continue;
                }
                conflicts.Add(planned[i]);
                if (samples.Count < 20)
                {
                    Proto proto = null;
                    try
                    {
                        if (kind == "物品") proto = LDB.items.Select(planned[i]);
                        else if (kind == "配方") proto = LDB.recipes.Select(planned[i]);
                        else if (kind == "科技") proto = LDB.techs.Select(planned[i]);
                        else if (kind == "模型") proto = LDB.models.Select(planned[i]);
                    }
                    catch
                    {
                    }
                    samples.Add(planned[i] + (proto != null ? "(现有: " + proto.Name + ")" : string.Empty));
                }
            }
            if (conflicts.Count > 0)
            {
                log.LogError("[冲突检查] " + kind + "计划 ID 冲突 " + conflicts.Count + " 个，样例：" + string.Join("、", samples.ToArray()));
            }
            return conflicts;
        }

        private static string DescribeIdList(List<int> ids)
        {
            StringBuilder builder = new StringBuilder();
            int limit = ids.Count < 24 ? ids.Count : 24;
            for (int i = 0; i < limit; i++)
            {
                if (i > 0)
                {
                    builder.Append(',');
                }
                builder.Append(ids[i]);
            }
            if (ids.Count > limit)
            {
                builder.Append("…");
            }
            return builder.ToString();
        }

        private static string DescribePlannedRange(List<int> ids)
        {
            if (ids.Count == 0)
            {
                return "无";
            }
            int min = ids[0];
            int max = ids[0];
            for (int i = 1; i < ids.Count; i++)
            {
                if (ids[i] < min) min = ids[i];
                if (ids[i] > max) max = ids[i];
            }
            return min + "-" + max + "（" + ids.Count + " 个）";
        }

        /// <summary>把 bool 占用表压成区间串（最多 maxRanges 段，超出用 …）。</summary>
        private static string DescribeIdRanges(bool[] flags, int minId, int maxRanges)
        {
            StringBuilder builder = new StringBuilder();
            int ranges = 0;
            int i = minId < 0 ? 0 : minId;
            while (i < flags.Length)
            {
                if (!flags[i])
                {
                    i++;
                    continue;
                }
                int start = i;
                while (i < flags.Length && flags[i])
                {
                    i++;
                }
                if (ranges >= maxRanges)
                {
                    builder.Append("…");
                    break;
                }
                if (ranges > 0)
                {
                    builder.Append(',');
                }
                builder.Append(start);
                if (i - 1 > start)
                {
                    builder.Append('-').Append(i - 1);
                }
                ranges++;
            }
            return builder.Length == 0 ? "无" : builder.ToString();
        }

        /// <summary>列出 [from,to) 内长度 &gt;= minLength 的空闲区间（按长度降序，最多 maxCount 个）。</summary>
        private static string DescribeFreeRanges(bool[] flags, int from, int to, int minLength, int maxCount)
        {
            List<int[]> ranges = new List<int[]>();
            int i = from;
            while (i < to)
            {
                if (flags[i])
                {
                    i++;
                    continue;
                }
                int start = i;
                while (i < to && !flags[i])
                {
                    i++;
                }
                int length = i - start;
                if (length >= minLength)
                {
                    ranges.Add(new int[] { start, length });
                }
            }
            ranges.Sort(delegate(int[] a, int[] b) { return b[1].CompareTo(a[1]); });
            StringBuilder builder = new StringBuilder();
            for (int k = 0; k < ranges.Count && k < maxCount; k++)
            {
                if (k > 0)
                {
                    builder.Append("；");
                }
                builder.Append(ranges[k][0]).Append('-').Append(ranges[k][0] + ranges[k][1] - 1)
                       .Append("（长 ").Append(ranges[k][1]).Append("）");
            }
            return builder.Length == 0 ? "无" : builder.ToString();
        }

        /// <summary>给出一个可容纳 count 个连续 ID 的空闲段建议。</summary>
        private static string FirstFreeRangeSuggestion(bool[] occupied, int from, int to, int count)
        {
            int i = from;
            while (i < to)
            {
                if (occupied[i])
                {
                    i++;
                    continue;
                }
                int start = i;
                while (i < to && !occupied[i])
                {
                    i++;
                }
                int length = i - start;
                if (length >= count)
                {
                    return start + "-" + (start + count - 1) + "（长 " + length + " 可容纳 " + count + " 条）";
                }
            }
            return "无（未找到长度 >= " + count + " 的空闲段）";
        }

        // ================================================================== item grid allocation
        /// <summary>
        /// 物品栏格位分配（2026-11-17 重做，task-10）：
        ///   - 扫描 LDB.items 现有 GridIndex（本机实测为 vanilla；其它 MOD 的 proto 要到 Preload 才并入 LDB）；
        ///   - 扫描 LDBTool.CustomGridIndex.cfg 持久化格位（覆盖本次未加载/已注册的 MOD 物品），
        ///     但**跳过 PY_ 前缀的历史记录**——那是本 MOD 以前分配的格位（如旧的 4001-5593），
        ///     若参与占用会让新分配随机漂移；LDBTool 的 Bind 会在注册时覆盖回旧值，
        ///     因此收尾阶段会把我们自己物品的 GridIndex 再写回分配值（见 ReapplyItemGridIndexes）。
        ///   - 建筑保留格位：buildings.json 的 grid_index（本次 5011-5025）全部预留；
        ///     grid_index=0 的建筑物在此处确定性补格（tab5 优先）。
        ///   - 物品在 tab1-5 的合法格（8×14，优先 tab4/5）确定性分配；容量耗尽后降级 tab6+ 顺序格。
        /// </summary>
        private void InitItemGridAllocation()
        {
            // 1) LDB.items 现有 proto 的 GridIndex
            try
            {
                if (LDB.items != null && LDB.items.dataArray != null)
                {
                    for (int i = 0; i < LDB.items.dataArray.Length; i++)
                    {
                        ItemProto proto = LDB.items.dataArray[i];
                        if (proto != null && proto.GridIndex > 0 && occupiedItemGrids.Add(proto.GridIndex))
                        {
                            gridScanFromLdb++;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                log.LogWarning("[PyPort] 读取 LDB.items 格位失败（继续分配）：" + ex.Message);
            }

            // 2) LDBTool.CustomGridIndex.cfg 持久化格位（跳过本 MOD 历史项）
            try
            {
                string cfg = Path.Combine(Paths.ConfigPath, "LDBTool", "LDBTool.CustomGridIndex.cfg");
                if (File.Exists(cfg))
                {
                    string[] lines = File.ReadAllLines(cfg);
                    for (int i = 0; i < lines.Length; i++)
                    {
                        string line = lines[i].Trim();
                        if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                        {
                            continue;
                        }
                        int eq = line.IndexOf('=');
                        if (eq <= 0)
                        {
                            continue;
                        }
                        string name = line.Substring(0, eq).Trim();
                        if (name.StartsWith("PY_", StringComparison.OrdinalIgnoreCase))
                        {
                            gridScanSkippedOwnCfg++;
                            continue;
                        }
                        int grid;
                        if (int.TryParse(line.Substring(eq + 1).Trim(), out grid) && grid > 0 && occupiedItemGrids.Add(grid))
                        {
                            gridScanFromCfg++;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                log.LogWarning("[PyPort] 读取 LDBTool 格位登记失败（继续分配）：" + ex.Message);
            }

            // 3) 建筑格位：显式 grid_index 全部预留；grid_index=0 的建筑物确定性补格（tab5 优先）
            for (int m = 0; m < content.Modules.Count; m++)
            {
                ModuleContent module = content.Modules[m];
                for (int i = 0; i < module.Buildings.Count; i++)
                {
                    BuildingDef def = module.Buildings[i];
                    if (def.Skip)
                    {
                        continue;
                    }
                    if (def.GridIndex > 0)
                    {
                        if (!buildingItemGrids.Add(def.GridIndex))
                        {
                            log.LogError("[GridIndex] 建筑 item " + def.ItemId + " 的 grid_index=" + def.GridIndex +
                                         " 与其它建筑重复，请检查 content（MOD 不会自动改 JSON）");
                        }
                        if (occupiedItemGrids.Add(def.GridIndex))
                        {
                            gridReservedBuildings++;
                        }
                    }
                }
            }
            for (int m = 0; m < content.Modules.Count; m++)
            {
                ModuleContent module = content.Modules[m];
                for (int i = 0; i < module.Buildings.Count; i++)
                {
                    BuildingDef def = module.Buildings[i];
                    if (def.Skip || def.GridIndex > 0)
                    {
                        continue;
                    }
                    int cell = NextVisibleCell();
                    if (cell == 0)
                    {
                        int overflow = NextOverflowCell();
                        if (overflow == 0)
                        {
                            log.LogError("[GridIndex] 建筑 item " + def.ItemId + " 无可用格位（tab1-59 已满）");
                            continue;
                        }
                        def.GridIndex = overflow;
                    }
                    else
                    {
                        def.GridIndex = cell;
                    }
                    buildingItemGrids.Add(def.GridIndex);
                    itemGridFallbackBuildingCount++;
                }
            }

            // 4) 统计 tab1-5 空闲格数（用于容量/降级说明）
            gridVisibleFreeCapacity = CountFreeVisibleCells();
            log.LogInfo(string.Format(
                "[GridIndex] 占用扫描：LDB 物品 {0} 格 + LDBTool 登记 {1} 格（跳过本 MOD 历史 PY_ 记录 {2} 条）+ 建筑预留 {3} 格（其中建筑自动补格 {4} 座）；" +
                "tab1-5 合法格 {5}，当前空闲 {6} 格；超出后降级 tab6+",
                gridScanFromLdb, gridScanFromCfg, gridScanSkippedOwnCfg, gridReservedBuildings,
                itemGridFallbackBuildingCount, VisibleCapacity, gridVisibleFreeCapacity));
        }

        /// <summary>统计 tab1-5 合法格中尚未占用的数量（只读，不改变分配状态）。</summary>
        private int CountFreeVisibleCells()
        {
            int free = 0;
            for (int t = 0; t < VisibleTabCount; t++)
            {
                int tab = VisibleTabPriority[t];
                for (int row = 0; row < GridRows; row++)
                {
                    for (int col = 0; col < GridCols; col++)
                    {
                        int cell = tab * 1000 + (row + 1) * 100 + (col + 1);
                        if (!occupiedItemGrids.Contains(cell))
                        {
                            free++;
                        }
                    }
                }
            }
            return free;
        }

        /// <summary>按「优先 tab4/5、再 tab1/2/3；每 tab row 0-7、col 0-13」的顺序取下一个空闲可见格。</summary>
        private int NextVisibleCell()
        {
            while (visibleCellCursor < VisibleCapacity)
            {
                int index = visibleCellCursor++;
                int tab = VisibleTabPriority[index / CellsPerTab];
                int rem = index % CellsPerTab;
                int row = rem / GridCols;
                int col = rem % GridCols;
                int cell = tab * 1000 + (row + 1) * 100 + (col + 1);
                if (occupiedItemGrids.Add(cell))
                {
                    return cell;
                }
            }
            return 0;
        }

        /// <summary>tab1-5 用尽后的确定性降级格（tab6+，筛选器网格不渲染，但保证全局唯一、不与已有格重复）。</summary>
        private int NextOverflowCell()
        {
            int capacity = CellsPerTab * (OverflowLastTab - OverflowFirstTab + 1);
            while (overflowCellCursor < capacity)
            {
                int index = overflowCellCursor++;
                int tab = OverflowFirstTab + (index / CellsPerTab);
                int rem = index % CellsPerTab;
                int row = rem / GridCols;
                int col = rem % GridCols;
                int cell = tab * 1000 + (row + 1) * 100 + (col + 1);
                if (occupiedItemGrids.Add(cell))
                {
                    return cell;
                }
            }
            return 0;
        }

        private int AllocateItemGrid()
        {
            int cell = NextVisibleCell();
            if (cell != 0)
            {
                itemGridVisibleCount++;
                return cell;
            }
            cell = NextOverflowCell();
            if (cell != 0)
            {
                itemGridHiddenCount++;
                return cell;
            }
            // 极端情况：tab1-59 全部用尽（未发生过）。用 90000+ 的确定性独立段兜底，保证不产生重复格。
            itemGridExhaustedCount++;
            int fallback = 90000 + itemGridExhaustFallback++;
            occupiedItemGrids.Add(fallback);
            log.LogError("[GridIndex] 物品栏格位耗尽（tab1-59 已满），物品改用兜底格位 " + fallback + "（不会进入任何筛选器 UI）");
            return fallback;
        }

        /// <summary>物品/建筑注册完成后做一次 GridIndex 交叉校验并输出统计（含降级 WARN）。</summary>
        private void FinalizeGridAllocation()
        {
            if (gridFinalized)
            {
                return;
            }
            gridFinalized = true;

            Dictionary<int, int> itemCounts = new Dictionary<int, int>();
            int itemTotal = 0;
            int duplicateItems = 0;
            List<int> duplicateSamples = new List<int>();
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i].Def == null)
                {
                    continue;
                }
                itemTotal++;
                int grid = items[i].Def.GridIndex;
                int count;
                if (itemCounts.TryGetValue(grid, out count))
                {
                    itemCounts[grid] = count + 1;
                    duplicateItems++;
                    if (duplicateSamples.Count < 8)
                    {
                        duplicateSamples.Add(grid);
                    }
                }
                else
                {
                    itemCounts[grid] = 1;
                }
            }

            int intersect = 0;
            List<int> intersectSamples = new List<int>();
            HashSet<int> buildingSeen = new HashSet<int>();
            int duplicateBuildings = 0;
            for (int i = 0; i < buildings.Count; i++)
            {
                int grid = buildings[i].Def.GridIndex;
                if (itemCounts.ContainsKey(grid))
                {
                    intersect++;
                    if (intersectSamples.Count < 8)
                    {
                        intersectSamples.Add(grid);
                    }
                }
                if (!buildingSeen.Add(grid))
                {
                    duplicateBuildings++;
                }
            }

            bool pass = duplicateItems == 0 && intersect == 0 && duplicateBuildings == 0;
            log.LogInfo(string.Format(
                "[GridIndex] 分配完成：PY 物品 {0} 个（tab1-5 可见 {1}，tab6+ 降级 {2}，兜底 {3}）；建筑 {4} 座（JSON 固定预留 {5} / 自动补格 {6}）；tab1-5 空闲容量 {7} 格",
                itemTotal, itemGridVisibleCount, itemGridHiddenCount, itemGridExhaustedCount,
                buildings.Count, gridReservedBuildings, itemGridFallbackBuildingCount, gridVisibleFreeCapacity));
            log.LogInfo(string.Format(
                "[验证] GridIndex 交叉校验：PY 物品格重复 {0} 个{1}；物品∩建筑 {2} 个{3}；建筑格重复 {4} 个 → {5}",
                duplicateItems,
                duplicateSamples.Count > 0 ? "（样例 " + JoinGrids(duplicateSamples) + "）" : string.Empty,
                intersect,
                intersectSamples.Count > 0 ? "（样例 " + JoinGrids(intersectSamples) + "）" : string.Empty,
                duplicateBuildings,
                pass ? "PASS" : "FAIL"));
            if (!pass)
            {
                log.LogError("[GridIndex] 交叉校验 FAIL：存在重复/交叠格位，请检查分配器与 content 的 grid_index");
            }
            if (itemGridHiddenCount > 0)
            {
                log.LogWarning(string.Format(
                    "[GridIndex] 有 {0} 个 PY 物品分配到 tab6+ 顺序格（筛选器网格不渲染）：DSP 物品筛选器只渲染 tab1-5 的 8×14 格，" +
                    "而 tab1-5 扣除原版/其它 MOD/建筑后只剩 {1} 格，本次需容纳 {2} 个物品。这是六包合集的已知限制" +
                    "（这些物品仍可通过物品选择器/配方产物/建造栏访问），不是重复格位。",
                    itemGridHiddenCount, gridVisibleFreeCapacity, itemTotal));
            }
        }

        private static string JoinGrids(List<int> grids)
        {
            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < grids.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(',');
                }
                builder.Append(grids[i]);
            }
            return builder.ToString();
        }

        /// <summary>
        /// LDBTool 的 GridIndexBind 会在 PreAddProto 时用 CustomGridIndex.cfg 的旧值覆盖我们传入的 GridIndex；
        /// 收尾阶段（onLoadingFinished / Update 探针）把分配值写回，保证本次运行内格位与统计一致。
        /// </summary>
        private void ReapplyItemGridIndexes(bool verbose)
        {
            int changed = 0;
            for (int i = 0; i < items.Count; i++)
            {
                ItemProto proto = items[i].Proto;
                if (proto == null)
                {
                    continue;
                }
                if (proto.GridIndex != items[i].Def.GridIndex)
                {
                    proto.GridIndex = items[i].Def.GridIndex;
                    changed++;
                }
            }
            for (int i = 0; i < buildings.Count; i++)
            {
                ItemProto proto = buildings[i].Item;
                if (proto == null)
                {
                    continue;
                }
                if (proto.GridIndex != buildings[i].Def.GridIndex)
                {
                    proto.GridIndex = buildings[i].Def.GridIndex;
                    changed++;
                }
            }
            if (verbose || changed > 0)
            {
                log.LogInfo("[GridIndex] LDBTool 覆盖后回填：恢复 " + changed + " 个物品/建筑格位到分配值");
            }
        }

        // ================================================================== id bookkeeping
        private bool MarkItemId(int id, string moduleId, string kind)
        {
            if (preOccupiedItems.Contains(id))
            {
                log.LogError("[PyPort] " + kind + " ID " + id + " 与已有 proto 冲突，跳过；内容 ID 需要迁移");
                skippedInvalid++;
                return false;
            }
            if (!usedItemIds.Add(id))
            {
                log.LogError("[PyPort] " + kind + " ID " + id + " 重复（模块 " + moduleId + "），已跳过");
                skippedInvalid++;
                return false;
            }
            return true;
        }

        private bool MarkModelId(int id, string moduleId)
        {
            if (preOccupiedModels.Contains(id))
            {
                log.LogError("[PyPort] 模型 ID " + id + " 与已有 proto 冲突，建筑已跳过；内容 ID 需要迁移");
                skippedInvalid++;
                return false;
            }
            if (!usedModelIds.Add(id))
            {
                log.LogError("[PyPort] 模型 ID " + id + " 重复（模块 " + moduleId + "），建筑已跳过");
                skippedInvalid++;
                return false;
            }
            return true;
        }

        private bool MarkRecipeId(int id, string moduleId)
        {
            if (preOccupiedRecipes.Contains(id))
            {
                log.LogError("[PyPort] 配方 ID " + id + " 与已有 proto 冲突，跳过；内容 ID 需要迁移");
                skippedInvalid++;
                return false;
            }
            if (!usedRecipeIds.Add(id))
            {
                log.LogError("[PyPort] 配方 ID " + id + " 重复（模块 " + moduleId + "），已跳过");
                skippedInvalid++;
                return false;
            }
            return true;
        }

        private bool MarkTechId(int id, string moduleId)
        {
            if (preOccupiedTechs.Contains(id) && !IsTechSlotToReplace(id))
            {
                log.LogError("[PyPort] 科技 ID " + id + " 与原版/其它 MOD 冲突，跳过；内容 ID 需要迁移");
                skippedInvalid++;
                return false;
            }
            if (!usedTechIds.Add(id))
            {
                log.LogError("[PyPort] 科技 ID " + id + " 重复（模块 " + moduleId + "），已跳过");
                skippedInvalid++;
                return false;
            }
            return true;
        }

        private int AllocateModelId()
        {
            for (int id = nextModelId; id < MaxModelArraySize; id++)
            {
                if (!usedModelIds.Contains(id) && !preOccupiedModels.Contains(id) &&
                    !ReservedModelIds.Contains(id) && !IsCommonApiModelIdTaken(id))
                {
                    nextModelId = id + 1;
                    return id;
                }
            }
            log.LogError("[PyPort] 模型 ID 安全段已用尽");
            return 0;
        }

        /// <summary>
        /// 建筑模型 ID 安全性检查：若内容包模型段与「原版已占 / 已知 MOD 保留段 / 其它 MOD 已注册」冲突，
        /// 把**整段**确定性迁移到第一个空闲连续块（只改运行期模型 ID：buildings.json 的 model_id 不被其它
        /// JSON 引用，迁移后仍一一对应）。这样即使 content 侧选段不当也不会产生 LDB 重复模型 ID /
        /// 建筑注册失败（本机实测 content 666-683 与 MolecularHub(666)/PhotonStockExchange(667) 冲突）。
        /// </summary>
        private void EnsureSafeBuildingModelIds()
        {
            if (buildingModelIdsChecked)
            {
                return;
            }
            buildingModelIdsChecked = true;

            List<BuildingDef> defs = new List<BuildingDef>();
            for (int m = 0; m < content.Modules.Count; m++)
            {
                ModuleContent module = content.Modules[m];
                if (module.Refused)
                {
                    continue;
                }
                for (int i = 0; i < module.Buildings.Count; i++)
                {
                    BuildingDef def = module.Buildings[i];
                    if (!def.Skip && def.ModelId > 0)
                    {
                        defs.Add(def);
                    }
                }
            }
            if (defs.Count == 0)
            {
                return;
            }
            List<string> reasons = new List<string>();
            for (int i = 0; i < defs.Count; i++)
            {
                int id = defs[i].ModelId;
                if (preOccupiedModels.Contains(id))
                {
                    reasons.Add(id + "（原版 LDB 已占）");
                }
                else if (ReservedModelIds.Contains(id))
                {
                    reasons.Add(id + "（已知 MOD 保留段）");
                }
                else if (IsCommonApiModelIdTaken(id))
                {
                    reasons.Add(id + "（其它 MOD 已注册）");
                }
            }
            if (reasons.Count == 0)
            {
                return;
            }

            int minId = int.MaxValue;
            int maxId = int.MinValue;
            for (int i = 0; i < defs.Count; i++)
            {
                if (defs[i].ModelId < minId) minId = defs[i].ModelId;
                if (defs[i].ModelId > maxId) maxId = defs[i].ModelId;
            }
            int count = defs.Count;
            int baseId = -1;
            for (int candidate = 666; candidate + count - 1 < MaxModelArraySize; candidate++)
            {
                bool ok = true;
                for (int k = 0; k < count; k++)
                {
                    int id = candidate + k;
                    if (preOccupiedModels.Contains(id) || ReservedModelIds.Contains(id) || IsCommonApiModelIdTaken(id))
                    {
                        ok = false;
                        break;
                    }
                }
                if (ok)
                {
                    baseId = candidate;
                    break;
                }
            }
            if (baseId < 0)
            {
                log.LogError("[PyPort] 找不到可容纳 " + count + " 个建筑模型的连续空闲模型段；模型冲突会导致注册失败");
                return;
            }
            int delta = baseId - minId;
            for (int i = 0; i < defs.Count; i++)
            {
                defs[i].ModelId += delta;
            }
            log.LogWarning(string.Format(
                "[PyPort] 建筑模型 ID 段与已有占用冲突（{0}）；为避免 LDB 重复模型 ID，整段确定性迁移 {1}-{2} → {3}-{4}" +
                "（模型 ID 仅被模型自身使用，buildings.json 不被其它数据引用；内容侧仍建议把模型段改到安全区）",
                string.Join("、", reasons.ToArray()), minId, maxId, baseId, baseId + count - 1));
            int capacity = (LDB.models != null ? LDB.models.dataArray.Length : 0) + 64;
            if (baseId + count > capacity)
            {
                log.LogWarning("[PyPort] 迁移后最大模型 ID=" + (baseId + count - 1) +
                               " 超过原版容量 " + capacity + "（SkillSystemCtorFix/PrefabDescArrayFix 会兜底，但建议内容侧选更低段）");
            }
        }

        /// <summary>CommonAPI 的模型注册表（internal static Dictionary&lt;int, ModelProto&gt;）里是否已有该 ID。</summary>
        private static bool IsCommonApiModelIdTaken(int modelId)
        {
            try
            {
                if (commonApiModelsField == null)
                {
                    commonApiModelsField = typeof(ProtoRegistry).GetField(
                        "models", BindingFlags.NonPublic | BindingFlags.Static);
                }
                if (commonApiModelsField == null)
                {
                    return false;
                }
                object dict = commonApiModelsField.GetValue(null);
                if (dict == null)
                {
                    return false;
                }
                MethodInfo contains = dict.GetType().GetMethod("ContainsKey", new Type[] { typeof(int) });
                return contains != null && (bool)contains.Invoke(dict, new object[] { modelId });
            }
            catch
            {
                return false;
            }
        }

        // ================================================================== translation helpers
        private void AddTranslation(string key, string en, string cn)
        {
            if (string.IsNullOrEmpty(key))
            {
                return;
            }
            Translation translation;
            if (!translations.TryGetValue(key, out translation))
            {
                translation = new Translation();
                translations[key] = translation;
            }
            if (!string.IsNullOrEmpty(en))
            {
                translation.En = en;
            }
            if (!string.IsNullOrEmpty(cn))
            {
                translation.Cn = cn;
            }
        }

        private void AddNamedTranslation(string key, string zh, string en)
        {
            if (string.IsNullOrEmpty(zh) && string.IsNullOrEmpty(en))
            {
                // 名称可能由 content/locale/*.json 补齐（真实内容包里 recipe 就是这种结构），只登记 key 占位。
                missingNameCount++;
                AddTranslation(key, null, null);
                return;
            }
            AddTranslation(key, en, zh);
        }

        private string ResolveIconPath(string iconFile, string iconSource, bool fluid, string label)
        {
            // 图标路径策略统一由 IconLoader 决定（懒加载模式下返回空串，避免 Preload 提前加载原版图标）。
            if (!IconLoader.LazyMode && string.IsNullOrEmpty(iconFile) && string.IsNullOrEmpty(iconSource))
            {
                missingIconFallbacks++;
            }
            return IconLoader.ItemOrTechIconPath(iconFile, iconSource, fluid);
        }

        // ================================================================== summary
        private void LogSummary()
        {
            log.LogInfo(string.Format(
                "[PyPort] 注册汇总：物品 {0}（液体 {1}）/ 建筑 {2} / 配方 {3}（宽配方 {4}）/ 科技 {5} / 本地化 {6} 条 / 无效跳过 {7} 条 / 生成器标记跳过降级 {8} 条",
                items.Count, fluidCount, buildings.Count, recipes.Count, wideRecipeCount, techs.Count + replacedTechs.Count,
                localeCount, skippedInvalid + mappedCount, skippedGenerator));

            for (int m = 0; m < content.Modules.Count; m++)
            {
                ModuleContent module = content.Modules[m];
                int mi = 0;
                int mb = 0;
                int mr = 0;
                int mt = 0;
                int ms = 0;
                for (int i = 0; i < items.Count; i++) if (items[i].Def.ModuleId == module.Id) mi++;
                for (int i = 0; i < buildings.Count; i++) if (buildings[i].Def.ModuleId == module.Id) mb++;
                for (int i = 0; i < recipes.Count; i++) if (recipes[i].Def.ModuleId == module.Id) mr++;
                for (int i = 0; i < techs.Count; i++) if (techs[i].Def.ModuleId == module.Id) mt++;
                for (int i = 0; i < replacedTechs.Count; i++) if (replacedTechs[i].Def.ModuleId == module.Id) mt++;
                for (int i = 0; i < module.Items.Count; i++) if (module.Items[i].Skip) ms++;
                for (int i = 0; i < module.Buildings.Count; i++) if (module.Buildings[i].Skip) ms++;
                for (int i = 0; i < module.Recipes.Count; i++) if (module.Recipes[i].Skip) ms++;
                for (int i = 0; i < module.Techs.Count; i++) if (module.Techs[i].Skip) ms++;
                ms += module.GeneratorSkipped;
                log.LogInfo(string.Format(
                    "[PyPort] 模块 {0} 注册计数：items={1} buildings={2} recipes={3} techs={4} skipped={5}{6}",
                    module.Id, mi, mb, mr, mt, ms, module.Refused ? "（模块被拒绝注册）" : string.Empty));
            }

            log.LogInfo("[PyPort] ID 使用：物品 " + DescribeRange(ItemIdList()) +
                        " / 模型 " + DescribeRange(ModelIdList()) +
                        " / 配方 " + DescribeRange(RecipeIdList()) +
                        " / 科技 " + DescribeRange(TechIdList()));
            if (missingIconFallbacks > 0)
            {
                log.LogWarning("[PyPort] 有 " + missingIconFallbacks +
                               " 个物品/建筑既没有 icon_file 也没有 icon_source，注册时用原版近似图标兜底");
            }
        }

        private List<int> ItemIdList()
        {
            List<int> list = new List<int>();
            for (int i = 0; i < items.Count; i++) list.Add(items[i].ExpectedId);
            for (int i = 0; i < buildings.Count; i++) list.Add(buildings[i].ExpectedItemId);
            return list;
        }

        private List<int> ModelIdList()
        {
            List<int> list = new List<int>();
            for (int i = 0; i < buildings.Count; i++) list.Add(buildings[i].ExpectedModelId);
            return list;
        }

        private List<int> RecipeIdList()
        {
            List<int> list = new List<int>();
            for (int i = 0; i < recipes.Count; i++) list.Add(recipes[i].ExpectedId);
            return list;
        }

        private List<int> TechIdList()
        {
            List<int> list = new List<int>();
            for (int i = 0; i < techs.Count; i++) list.Add(techs[i].ExpectedId);
            for (int i = 0; i < replacedTechs.Count; i++) list.Add(replacedTechs[i].ExpectedId);
            return list;
        }

        private static string DescribeRange(List<int> ids)
        {
            if (ids.Count == 0)
            {
                return "无";
            }
            int min = ids[0];
            int max = ids[0];
            for (int i = 1; i < ids.Count; i++)
            {
                if (ids[i] < min) min = ids[i];
                if (ids[i] > max) max = ids[i];
            }
            return ids.Count == 1 ? min.ToString() : (min + "-" + max + "（" + ids.Count + " 个）");
        }

        // ================================================================== icon descs cleanup
        /// <summary>
        /// CommonAPI 的 IconSetPatch 会按 itemIconDescs 的图标索引写定长数组，高编号图标会越界中断
        /// IconSet.Create（手册 §8.4）。照抄 DSP-MechDawn 的反射清理。
        /// </summary>
        private void CleanupIconDescs()
        {
            try
            {
                FieldInfo field = typeof(ProtoRegistry).GetField("itemIconDescs", BindingFlags.NonPublic | BindingFlags.Static);
                if (field == null)
                {
                    log.LogWarning("[图标] 找不到 ProtoRegistry.itemIconDescs，跳过图标描述清理");
                    return;
                }
                object dict = field.GetValue(null);
                if (dict == null)
                {
                    return;
                }
                MethodInfo remove = dict.GetType().GetMethod("Remove", new Type[] { typeof(int) });
                if (remove == null)
                {
                    log.LogWarning("[图标] itemIconDescs 没有 Remove(int)，跳过清理");
                    return;
                }
                int removed = 0;
                foreach (int id in registeredItemIdList)
                {
                    if ((bool)remove.Invoke(dict, new object[] { id }))
                    {
                        removed++;
                    }
                }
                log.LogInfo("[PyPort] 图标描述清理：移除 " + removed + "/" + registeredItemIdList.Count + " 条（避免 IconSet 越界）");
            }
            catch (Exception ex)
            {
                log.LogWarning("[PyPort] 图标描述清理失败（不影响注入）：" + ex.Message);
            }
        }
    }
}
