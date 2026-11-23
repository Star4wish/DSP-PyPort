using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx.Logging;

namespace DSPPyPort.Core
{
    /// <summary>
    /// 内容包加载器：manifest.json（模块开关）→ 各模块 items/buildings/recipes/techs.json
    /// + content/locale/*.json。只读 content/，发现可疑数据尽量修复并告警，绝不静默丢弃。
    /// </summary>
    internal static class ContentLoader
    {
        internal const int MaxItemId = 12000;   // ItemProto 硬上限
        internal const int MaxRecipeId = 12000; // IconSet.recipeIconIndex 定长
        internal const int MaxTechId = 12000;   // TechProto 硬上限
        internal const int MaxModelId = 2048;   // modelArray MAX_ARRAY_SIZE

        private static readonly Dictionary<string, string> EmptyLocale = new Dictionary<string, string>();

        internal static LoadedContent LoadAll(string contentRoot, ManualLogSource log)
        {
            return LoadAll(contentRoot, log, null);
        }

        /// <summary>
        /// 加载内容。moduleGate(moduleId, manifestEnabled) 用于用户级分包开关（BepInEx 配置），
        /// 返回 false 时整模块不读文件、不注册（用于降低启动时间）；null 表示完全按 manifest.enabled。
        /// </summary>
        internal static LoadedContent LoadAll(string contentRoot, ManualLogSource log, Func<string, bool, bool> moduleGate)
        {
            LoadedContent content = new LoadedContent();
            content.Root = contentRoot;

            if (!Directory.Exists(contentRoot))
            {
                log.LogError("[PyPort] 内容目录不存在：" + contentRoot + "（MOD 将不注册任何内容）");
                return content;
            }

            string manifestPath = Path.Combine(contentRoot, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                log.LogError("[PyPort] 缺少 manifest.json：" + manifestPath + "（MOD 将不注册任何内容）");
                return content;
            }

            JsonValue manifest;
            try
            {
                manifest = JsonReader.Parse(File.ReadAllText(manifestPath, Encoding.UTF8));
            }
            catch (Exception ex)
            {
                log.LogError("[PyPort] manifest.json 解析失败：" + ex.Message);
                return content;
            }

            content.ManifestVersion = manifest.Int("version", 0);
            if (content.ManifestVersion != 1)
            {
                log.LogWarning("[PyPort] manifest.version=" + content.ManifestVersion + "（本 MOD 按 v1 契约实现）");
            }

            JsonValue modulesNode = manifest.Node("modules");
            if (modulesNode == null)
            {
                modulesNode = manifest.Node("features");
            }
            if (modulesNode == null || modulesNode.Kind != JsonKind.Array)
            {
                log.LogError("[PyPort] manifest.json 缺少 modules/features 数组");
                return content;
            }

            LoadLocale(contentRoot, content.LocaleZh, content.LocaleEn, log);

            for (int i = 0; i < modulesNode.ArrayValue.Count; i++)
            {
                JsonValue moduleNode = modulesNode.ArrayValue[i];
                if (moduleNode == null || moduleNode.Kind != JsonKind.Object)
                {
                    content.Warnings.Add("manifest.modules[" + i + "] 不是对象，已忽略");
                    continue;
                }
                string moduleId = moduleNode.Str("id", null);
                if (string.IsNullOrEmpty(moduleId))
                {
                    content.Warnings.Add("manifest.modules[" + i + "] 缺少 id，已忽略");
                    continue;
                }
                string moduleName = moduleNode.Str("name", moduleId);
                bool manifestEnabled = moduleNode.Bool("enabled", true);
                bool effectiveEnabled = moduleGate != null ? moduleGate(moduleId, manifestEnabled) : manifestEnabled;
                if (!effectiveEnabled)
                {
                    content.DisabledModules.Add(moduleId);
                    log.LogInfo("[PyPort] 模块 " + moduleId + "（" + moduleName + "）已禁用，跳过" +
                                (manifestEnabled ? "（配置关闭）" : "（manifest.enabled=false）"));
                    continue;
                }

                string dir = moduleNode.Str("dir", moduleId);
                ModuleContent module = LoadModule(contentRoot, moduleId, moduleName, dir, log);
                if (module != null)
                {
                    content.Modules.Add(module);
                }
            }

            for (int i = 0; i < content.Warnings.Count; i++)
            {
                log.LogWarning("[PyPort] " + content.Warnings[i]);
            }
            return content;
        }

        // ------------------------------------------------------------------ module
        private static ModuleContent LoadModule(string contentRoot, string moduleId, string moduleName, string dir, ManualLogSource log)
        {
            string root = Path.Combine(contentRoot, dir);
            if (!Directory.Exists(root))
            {
                log.LogError("[PyPort] 模块 " + moduleId + " 的目录不存在：" + root);
                return null;
            }

            ModuleContent module = new ModuleContent();
            module.Id = moduleId;
            module.Name = moduleName;
            module.Dir = dir;
            module.Root = root;

            try
            {
                LoadItems(root, module, log);
                LoadBuildings(root, module, log);
                LoadRecipes(root, module, log);
                LoadTechs(root, module, log);
                LoadModuleLocale(root, module.Id, log);
                module.GeneratorSkipped = CountSkipped(root);
            }
            catch (Exception ex)
            {
                log.LogError("[PyPort] 模块 " + moduleId + " 加载失败（已跳过该模块）：" + ex);
                return null;
            }

            log.LogInfo(string.Format(
                "[PyPort] 模块 {0}（{1}）读取：物品 {2} / 建筑 {3} / 配方 {4} / 科技 {5} / 生成器跳过 {6}",
                module.Id, module.Name, module.Items.Count, module.Buildings.Count,
                module.Recipes.Count, module.Techs.Count, module.GeneratorSkipped));
            return module;
        }

        private static List<JsonValue> ReadArray(string path)
        {
            if (!File.Exists(path))
            {
                return null;
            }
            JsonValue root = JsonReader.Parse(File.ReadAllText(path, Encoding.UTF8));
            if (root == null)
            {
                return null;
            }
            if (root.Kind == JsonKind.Array)
            {
                return root.ArrayValue;
            }
            if (root.Kind == JsonKind.Object)
            {
                // 兼容 {"items":[...]} / {"data":[...]} 包装
                string fileName = Path.GetFileNameWithoutExtension(path);
                JsonValue inner = root.Node(fileName);
                if (inner == null)
                {
                    inner = root.Node("data");
                }
                if (inner != null && inner.Kind == JsonKind.Array)
                {
                    return inner.ArrayValue;
                }
            }
            throw new JsonParseException(path + " 的根节点不是数组");
        }

        private static int CountSkipped(string root)
        {
            string path = Path.Combine(root, "_skipped.json");
            if (!File.Exists(path))
            {
                return 0;
            }
            try
            {
                JsonValue node = JsonReader.Parse(File.ReadAllText(path, Encoding.UTF8));
                if (node == null)
                {
                    return 0;
                }
                if (node.Kind == JsonKind.Array)
                {
                    return node.Count;
                }
                if (node.Kind == JsonKind.Object)
                {
                    // 生成器的 _skipped.json 是分类字典（summary + 各类明细），优先读 summary 的数值合计。
                    int total = 0;
                    JsonValue summary = node.Node("summary");
                    if (summary != null && summary.Kind == JsonKind.Object)
                    {
                        foreach (KeyValuePair<string, JsonValue> pair in summary.ObjectValue)
                        {
                            if (pair.Value != null && pair.Value.Kind == JsonKind.Number)
                            {
                                total += pair.Value.AsInt(0);
                            }
                        }
                        if (total > 0)
                        {
                            return total;
                        }
                    }
                    foreach (KeyValuePair<string, JsonValue> pair in node.ObjectValue)
                    {
                        if (pair.Value != null && pair.Value.Kind == JsonKind.Array)
                        {
                            total += pair.Value.Count;
                        }
                    }
                    return total;
                }
                return 0;
            }
            catch
            {
                return 0;
            }
        }

        // ------------------------------------------------------------------ items
        private static void LoadItems(string root, ModuleContent module, ManualLogSource log)
        {
            List<JsonValue> rows = ReadArray(Path.Combine(root, "items.json"));
            if (rows == null)
            {
                return;
            }
            for (int i = 0; i < rows.Count; i++)
            {
                JsonValue row = rows[i];
                if (row == null || row.Kind != JsonKind.Object)
                {
                    module.Warn("items.json[" + i + "] 不是对象，已忽略");
                    continue;
                }
                ItemDef def = new ItemDef();
                def.ModuleId = module.Id;
                def.Id = row.Int("id", 0);
                def.Key = row.Str("key", null);
                def.NameZh = row.Str("name_zh", null);
                def.NameEn = row.Str("name_en", null);
                def.DescZh = row.Str("desc_zh", row.Str("desc", null));
                def.IconFile = row.Str("icon_file", null);
                def.IconSource = row.Str("icon_source", null);
                def.IsFluid = row.Bool("is_fluid", false);
                def.IsBuilding = row.Bool("is_building", false);
                def.Source = row.Str("source", "py");
                def.Stack = row.Int("stack", 0);
                def.ItemType = row.Int("item_type", 2);
                def.GridIndex = row.Int("grid_index", 0);

                if (def.Id <= 0)
                {
                    def.Skip = true;
                    def.SkipReason = "缺少 id";
                }
                else if (def.Id >= MaxItemId)
                {
                    def.Skip = true;
                    def.SkipReason = "id " + def.Id + " >= " + MaxItemId + "（引擎硬上限）";
                }
                else if (!string.IsNullOrEmpty(def.Source) && def.Source.Trim().ToLowerInvariant() == "mapped")
                {
                    def.Skip = true;
                    def.SkipReason = "source=mapped（映射到原版物品，不注册）";
                }

                if (!def.Skip)
                {
                    if (def.Stack <= 0)
                    {
                        def.Stack = def.IsFluid ? 200 : 100;
                    }
                    if (def.ItemType < 0 || def.ItemType > 11)
                    {
                        module.Warn("物品 " + def.Id + " 的 item_type=" + def.ItemType + " 不是合法 EItemType（0-11），按 Material(2) 注册");
                        def.ItemType = 2;
                    }
                }

                module.Items.Add(def);
            }
        }

        // ------------------------------------------------------------------ buildings
        private static void LoadBuildings(string root, ModuleContent module, ManualLogSource log)
        {
            List<JsonValue> rows = ReadArray(Path.Combine(root, "buildings.json"));
            if (rows == null)
            {
                return;
            }
            for (int i = 0; i < rows.Count; i++)
            {
                JsonValue row = rows[i];
                if (row == null || row.Kind != JsonKind.Object)
                {
                    module.Warn("buildings.json[" + i + "] 不是对象，已忽略");
                    continue;
                }
                BuildingDef def = new BuildingDef();
                def.ModuleId = module.Id;
                def.ItemId = row.Int("item_id", 0);
                def.ModelId = row.Int("model_id", 0);
                def.RecipeType = row.Int("recipe_type", 0);
                def.CloneFrom = row.Str("clone_from", null);
                def.ClonePrefabModelId = row.Int("clone_prefab_model_id", 0);
                def.AssemblerSpeed = row.Dbl("assembler_speed", -1.0);
                def.WorkEnergyPerTick = row.Long("work_energy_per_tick", -1L);
                def.IdleEnergyPerTick = row.Long("idle_energy_per_tick", -1L);
                def.GridIndex = row.Int("grid_index", 0);
                def.BuildIndex = row.Int("build_index", 0);
                def.PreTech = row.Int("pre_tech", row.Int("pretech", 0));
                def.Key = row.Str("key", null);
                def.NameZh = row.Str("name_zh", null);
                def.NameEn = row.Str("name_en", null);
                def.DescZh = row.Str("desc_zh", row.Str("desc", null));
                def.IconFile = row.Str("icon_file", null);
                def.IconSource = row.Str("icon_source", null);

                if (def.ItemId <= 0)
                {
                    def.Skip = true;
                    def.SkipReason = "缺少 item_id";
                }
                else if (def.ItemId >= MaxItemId)
                {
                    def.Skip = true;
                    def.SkipReason = "item_id " + def.ItemId + " >= " + MaxItemId;
                }
                else if (def.ModelId < 0 || def.ModelId >= MaxModelId)
                {
                    def.Skip = true;
                    def.SkipReason = "model_id " + def.ModelId + " 超出 1-" + (MaxModelId - 1);
                }
                else if (string.IsNullOrEmpty(def.CloneFrom) && def.ClonePrefabModelId <= 0)
                {
                    def.Skip = true;
                    def.SkipReason = "缺少 clone_from / clone_prefab_model_id";
                }
                else if (def.RecipeType != 0 && (def.RecipeType < 60 || def.RecipeType > 79))
                {
                    module.Warn("建筑 item " + def.ItemId + " 的 recipe_type=" + def.RecipeType +
                                " 不在契约段 60-79；不会改写 prefabDesc.assemblerRecipeType");
                }

                module.Buildings.Add(def);
            }
        }

        // ------------------------------------------------------------------ recipes
        private static void LoadRecipes(string root, ModuleContent module, ManualLogSource log)
        {
            List<JsonValue> rows = ReadArray(Path.Combine(root, "recipes.json"));
            if (rows == null)
            {
                return;
            }
            for (int i = 0; i < rows.Count; i++)
            {
                JsonValue row = rows[i];
                if (row == null || row.Kind != JsonKind.Object)
                {
                    module.Warn("recipes.json[" + i + "] 不是对象，已忽略");
                    continue;
                }
                RecipeDef def = new RecipeDef();
                def.ModuleId = module.Id;
                def.Id = row.Int("id", 0);
                def.Key = row.Str("key", null);
                def.Type = row.Int("type", 0);
                def.TimeTicks = row.Int("time_ticks", 0);
                def.TechId = row.Int("tech_id", 0);
                def.GridIndex = row.Int("grid_index", 0);
                def.Handcraft = row.Bool("handcraft", false);
                def.NameZh = row.Str("name_zh", null);
                def.NameEn = row.Str("name_en", null);
                def.DescZh = row.Str("desc_zh", row.Str("desc", null));
                def.IconFile = row.Str("icon_file", null);
                def.IconSource = row.Str("icon_source", null);
                def.Category = row.Str("category", null);

                List<int> inputs = row.IntList("inputs");
                List<int> inCounts = row.IntList("in_counts");
                List<int> outputs = row.IntList("outputs");
                List<int> outCounts = row.IntList("out_counts");

                if (def.Id <= 0)
                {
                    def.Skip = true;
                    def.SkipReason = "缺少 id";
                }
                else if (def.Id >= MaxRecipeId)
                {
                    def.Skip = true;
                    def.SkipReason = "id " + def.Id + " >= " + MaxRecipeId + "（IconSet 定长数组）";
                }
                else if (outputs.Count == 0)
                {
                    def.Skip = true;
                    def.SkipReason = "outputs 为空（CommonAPI RegisterRecipe 会 throw）";
                }
                else
                {
                    if (inCounts.Count != inputs.Count)
                    {
                        module.Warn("配方 " + def.Id + " 的 in_counts 长度(" + inCounts.Count +
                                    ") != inputs 长度(" + inputs.Count + ")，已按 1 补齐/截断");
                        inCounts = ResizeCounts(inCounts, inputs.Count);
                    }
                    if (outCounts.Count != outputs.Count)
                    {
                        module.Warn("配方 " + def.Id + " 的 out_counts 长度(" + outCounts.Count +
                                    ") != outputs 长度(" + outputs.Count + ")，已按 1 补齐/截断");
                        outCounts = ResizeCounts(outCounts, outputs.Count);
                    }
                    if (def.TimeTicks < 1)
                    {
                        module.Warn("配方 " + def.Id + " 的 time_ticks=" + def.TimeTicks + "（DSP 最小 1 tick），已改为 1");
                        def.TimeTicks = 1;
                    }
                    if (def.Type <= 0)
                    {
                        module.Warn("配方 " + def.Id + " 缺少 type，按 Assemble(4) 注册");
                        def.Type = 4;
                    }
                    else if (def.Type > 255)
                    {
                        module.Warn("配方 " + def.Id + " 的 type=" + def.Type + " 超出 byte 范围，按 255 注册");
                        def.Type = 255;
                    }
                    if (def.GridIndex < 1101)
                    {
                        module.Warn("配方 " + def.Id + " 的 grid_index=" + def.GridIndex +
                                    "（<1101 不会出现在选择器），将由 MOD 自动补到 5000+ 段");
                        def.GridIndex = 0;
                    }
                }

                def.Inputs = inputs.ToArray();
                def.InCounts = inCounts.ToArray();
                def.Outputs = outputs.ToArray();
                def.OutCounts = outCounts.ToArray();
                module.Recipes.Add(def);
            }
        }

        private static List<int> ResizeCounts(List<int> counts, int length)
        {
            List<int> result = new List<int>(length);
            for (int i = 0; i < length; i++)
            {
                result.Add(i < counts.Count && counts[i] > 0 ? counts[i] : 1);
            }
            return result;
        }

        // ------------------------------------------------------------------ techs
        private static void LoadTechs(string root, ModuleContent module, ManualLogSource log)
        {
            List<JsonValue> rows = ReadArray(Path.Combine(root, "techs.json"));
            if (rows == null)
            {
                return;
            }
            for (int i = 0; i < rows.Count; i++)
            {
                JsonValue row = rows[i];
                if (row == null || row.Kind != JsonKind.Object)
                {
                    module.Warn("techs.json[" + i + "] 不是对象，已忽略");
                    continue;
                }
                TechDef def = new TechDef();
                def.ModuleId = module.Id;
                def.Id = row.Int("id", 0);
                def.Key = row.Str("key", null);
                def.NameZh = row.Str("name_zh", null);
                def.NameEn = row.Str("name_en", null);
                def.DescZh = row.Str("desc_zh", row.Str("desc", null));
                def.ConclusionZh = row.Str("conclusion_zh", row.Str("conclusion", null));
                def.IconFile = row.Str("icon_file", null);
                def.IconSource = row.Str("icon_source", null);
                def.HashNeeded = row.Long("hash_needed", 0L);
                def.Hidden = row.Bool("hidden", row.Bool("is_hidden", false));
                def.PosX = (float)row.Dbl("pos_x", double.NaN);
                def.PosY = (float)row.Dbl("pos_y", double.NaN);
                def.HasPosition = !float.IsNaN(def.PosX) && !float.IsNaN(def.PosY);

                List<int> preTechs = row.IntList("pre_techs");
                List<int> matrices = row.IntList("matrices");
                List<int> matrixPoints = row.IntList("matrix_points");
                List<int> unlockRecipes = row.IntList("unlock_recipes");

                if (def.Id <= 0)
                {
                    def.Skip = true;
                    def.SkipReason = "缺少 id";
                }
                else if (def.Id >= MaxTechId)
                {
                    def.Skip = true;
                    def.SkipReason = "id " + def.Id + " >= " + MaxTechId + "（引擎硬上限）";
                }
                else if (matrices.Count == 0)
                {
                    def.Skip = true;
                    def.SkipReason = "matrices 为空（研究站科技必须有矩阵消耗）";
                }
                else
                {
                    if (matrixPoints.Count != matrices.Count)
                    {
                        module.Warn("科技 " + def.Id + " 的 matrix_points 长度(" + matrixPoints.Count +
                                    ") != matrices 长度(" + matrices.Count + ")，已按 10 补齐/截断");
                        matrixPoints = ResizePoints(matrixPoints, matrices.Count);
                    }
                    if (def.HashNeeded <= 0)
                    {
                        module.Warn("科技 " + def.Id + " 的 hash_needed=" + def.HashNeeded + "，按 1000 注册");
                        def.HashNeeded = 1000L;
                    }
                    if (!def.HasPosition)
                    {
                        module.Warn("科技 " + def.Id + " 缺少 pos_x/pos_y，将由 MOD 自动排布");
                    }
                }

                def.PreTechs = preTechs.ToArray();
                def.Matrices = matrices.ToArray();
                def.MatrixPoints = matrixPoints.ToArray();
                def.UnlockRecipes = unlockRecipes.ToArray();
                module.Techs.Add(def);
            }
        }

        private static List<int> ResizePoints(List<int> points, int length)
        {
            List<int> result = new List<int>(length);
            for (int i = 0; i < length; i++)
            {
                result.Add(i < points.Count && points[i] > 0 ? points[i] : 10);
            }
            return result;
        }

        // ------------------------------------------------------------------ locale
        private static void LoadLocale(string contentRoot, Dictionary<string, string> zh, Dictionary<string, string> en, ManualLogSource log)
        {
            string localeDir = Path.Combine(contentRoot, "locale");
            MergeLocaleFile(Path.Combine(localeDir, "zh-CN.json"), zh, log);
            MergeLocaleFile(Path.Combine(localeDir, "zh_cn.json"), zh, log);
            MergeLocaleFile(Path.Combine(localeDir, "en-US.json"), en, log);
            MergeLocaleFile(Path.Combine(localeDir, "en_us.json"), en, log);
            if (zh.Count == 0 && en.Count == 0)
            {
                log.LogWarning("[PyPort] 未找到 content/locale/ 文案（将回退 items/recipes/techs.json 内联 name_zh/name_en）");
            }
        }

        private static void LoadModuleLocale(string moduleRoot, string moduleId, ManualLogSource log)
        {
            // 模块级 locale 目录为可选扩展（契约默认在 content/locale/）；这里只做存在性探测并提示，
            // 实际合并发生在 LoadAll 里的全局 locale（模块级文件由生成器保证不重复提供）。
            string dir = Path.Combine(moduleRoot, "locale");
            if (Directory.Exists(dir))
            {
                log.LogWarning("[PyPort] 模块 " + moduleId + " 自带 locale/ 目录（契约默认 content/locale/），当前版本不合并模块级文案");
            }
        }

        private static void MergeLocaleFile(string path, Dictionary<string, string> target, ManualLogSource log)
        {
            if (!File.Exists(path))
            {
                return;
            }
            try
            {
                JsonValue root = JsonReader.Parse(File.ReadAllText(path, Encoding.UTF8));
                if (root == null || root.Kind != JsonKind.Object)
                {
                    log.LogWarning("[PyPort] 文案文件根节点不是对象：" + path);
                    return;
                }
                int count = 0;
                foreach (KeyValuePair<string, JsonValue> pair in root.ObjectValue)
                {
                    if (pair.Value == null || pair.Value.Kind != JsonKind.String)
                    {
                        continue;
                    }
                    target[pair.Key] = pair.Value.StringValue;
                    count++;
                }
                log.LogInfo("[PyPort] 文案 " + Path.GetFileName(path) + "：" + count + " 条");
            }
            catch (Exception ex)
            {
                log.LogError("[PyPort] 文案文件解析失败：" + path + "：" + ex.Message);
            }
        }
    }
}
