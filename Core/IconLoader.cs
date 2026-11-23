using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace DSPPyPort.Core
{
    internal enum IconTargetKind
    {
        Item,
        Tech,
        Recipe
    }

    /// <summary>一个待加载的图标规格（icon_file → content/icons/&lt;file&gt;，失败再回退 icon_source/默认图）。</summary>
    internal sealed class IconSpec
    {
        internal IconTargetKind Kind;
        internal int Id;
        internal string FileName;
        internal string FallbackPath;
        internal string DefaultFallback;
        internal bool IsFluid;
    }

    /// <summary>
    /// 图标加载（v0.7.0 起支持懒加载）：
    ///   急切（LazyIcons=false）：onLoadingFinished 时批量 Texture2D.LoadImage + Sprite.Create，反射写 _iconSprite。
    ///   懒加载（LazyIcons=true，默认）：启动时不加载任何 PNG；Harmony Postfix 挂在
    ///   ItemProto/TechProto/RecipeProto 的 iconSprite getter 上，**首次被 UI 访问且 __result==null** 时
    ///   才按需加载对应 PNG（失败回退 icon_source 原版资源）并写回 _iconSprite。
    ///
    /// 懒加载的意义：六包/八包合集有 3000+ 物品 + 700+ 科技，启动时批量 3800+ 次 PNG 解码是主要
    /// 启动耗时来源；而一局游戏实际只会访问其中一部分图标。
    ///
    /// 风险与回退：getter 反射失败 / Harmony 安装失败时自动回落急切模式（Plugin 在注册前决定）；
    /// 若某些 UI 直接读私有字段 _iconSprite 而不走 getter（理论上没有，原版 iconSprite 是只读属性），
    /// 该处会显示空白图标——可用 LazyIcons=false 一键回退。
    /// </summary>
    internal static class IconLoader
    {
        private const int IconSize = 80;

        private static readonly Dictionary<int, IconSpec> itemSpecs = new Dictionary<int, IconSpec>();
        private static readonly Dictionary<int, IconSpec> techSpecs = new Dictionary<int, IconSpec>();
        private static readonly Dictionary<int, IconSpec> recipeSpecs = new Dictionary<int, IconSpec>();
        private static readonly Dictionary<string, Sprite> fileCache = new Dictionary<string, Sprite>(StringComparer.Ordinal);
        private static readonly HashSet<int> recipeResolved = new HashSet<int>();
        private static readonly object sync = new object();

        private static string iconDir;
        private static ManualLogSource log;
        private static bool configured;
        private static int fileLoaded;
        private static int fallbackLoaded;
        private static int missing;

        private static FieldInfo itemIconField;
        private static FieldInfo techIconField;
        private static FieldInfo recipeIconField;

        internal static bool LazyMode { get; private set; }
        internal static bool LazyPatchesInstalled { get; private set; }

        /// <summary>
        /// 图集构建期间（IconSet.Create）抑制懒加载：否则 Create 会为 9664 个 proto 逐个解码 PNG
        /// 并触发 625 格图集溢出（绝大多数索引=0 → picker 全显示同一格）。
        /// 由 IconSetAtlasFix 在 Create Prefix/Postfix 里置位/复位。
        /// </summary>
        internal static bool SuppressLazyLoad;

        internal static Dictionary<int, IconSpec> GetItemSpecs() { return itemSpecs; }
        internal static Dictionary<int, IconSpec> GetTechSpecs() { return techSpecs; }
        internal static Dictionary<int, IconSpec> GetRecipeSpecs() { return recipeSpecs; }
        internal static int ItemSpecCount { get { return itemSpecs.Count; } }
        internal static int TechSpecCount { get { return techSpecs.Count; } }
        internal static int RecipeSpecCount { get { return recipeSpecs.Count; } }

        // ------------------------------------------------------------------ configure
        internal static void Configure(string contentRoot, bool lazyRequested, ManualLogSource logger)
        {
            iconDir = Path.Combine(contentRoot, "icons");
            log = logger;
            LazyMode = lazyRequested;
            configured = true;
            ResolveFields();
            log.LogInfo("[图标] 模式=" + (lazyRequested ? "懒加载（首次访问 iconSprite 时按需加载 PNG）" : "启动时批量加载") +
                        "；目录=" + iconDir + (Directory.Exists(iconDir) ? string.Empty : "（目录不存在！）"));
        }

        /// <summary>懒加载补丁安装失败时回落急切模式（必须在内容注册之前调用）。</summary>
        internal static void DisableLazy(ManualLogSource logger)
        {
            LazyMode = false;
            logger.LogWarning("[图标] 懒加载不可用，已回退启动时批量加载模式（LazyIcons=false 的效果）");
        }

        // ------------------------------------------------------------------ registration
        internal static void RegisterItem(int id, string iconFile, string iconSource, bool isFluid)
        {
            if (!configured)
            {
                return;
            }
            IconSpec spec = new IconSpec();
            spec.Kind = IconTargetKind.Item;
            spec.Id = id;
            spec.FileName = iconFile;
            spec.FallbackPath = iconSource;
            spec.IsFluid = isFluid;
            spec.DefaultFallback = isFluid ? "Icons/ItemRecipe/sulphuric-acid" : "Icons/ItemRecipe/iron-ore";
            itemSpecs[id] = spec;
        }

        internal static void RegisterTech(int id, string iconFile, string iconSource)
        {
            if (!configured)
            {
                return;
            }
            IconSpec spec = new IconSpec();
            spec.Kind = IconTargetKind.Tech;
            spec.Id = id;
            spec.FileName = iconFile;
            spec.FallbackPath = iconSource;
            spec.DefaultFallback = "Icons/ItemRecipe/iron-ore";
            techSpecs[id] = spec;
        }

        internal static void RegisterRecipe(int id, string iconFile, string iconSource)
        {
            if (!configured)
            {
                return;
            }
            IconSpec spec = new IconSpec();
            spec.Kind = IconTargetKind.Recipe;
            spec.Id = id;
            spec.FileName = iconFile;
            spec.FallbackPath = iconSource;
            recipeSpecs[id] = spec;
        }

        /// <summary>
        /// 注册 proto 时传给 RegisterItem/RegisterTech 的 IconPath：
        ///   懒加载 → 返回空串，避免 ItemProto/TechProto.Preload 提前加载原版图标（会挡住自定义 PNG 的懒加载）。
        ///   急切   → 返回 icon_source / 默认原版图标兜底（原行为）。
        /// </summary>
        internal static string ItemOrTechIconPath(string iconFile, string iconSource, bool fluid)
        {
            if (LazyMode)
            {
                return string.Empty;
            }
            if (!string.IsNullOrEmpty(iconSource))
            {
                return iconSource;
            }
            return fluid ? "Icons/ItemRecipe/sulphuric-acid" : "Icons/ItemRecipe/iron-ore";
        }

        /// <summary>
        /// 配方的 IconPath：懒加载时给一个已加载的原版资源（避免 RecipeProto.Preload 触发产物 ItemProto 的懒加载，
        /// 那样会把大量产物图标提前拉起来）；真正的图标由 RecipeProto.get_iconSprite 的 Postfix 首次访问时替换。
        /// </summary>
        internal static string RecipeIconPath(string iconFile, string iconSource)
        {
            if (!LazyMode)
            {
                return string.IsNullOrEmpty(iconFile) ? (iconSource ?? string.Empty) : string.Empty;
            }
            return string.IsNullOrEmpty(iconSource) ? "Icons/ItemRecipe/iron-ore" : iconSource;
        }

        // ------------------------------------------------------------------ lazy patches
        internal static bool InstallLazyPatches(Harmony harmony, ManualLogSource logger)
        {
            if (!LazyMode)
            {
                return false;
            }
            if (!configured || itemIconField == null || techIconField == null || recipeIconField == null)
            {
                logger.LogWarning("[图标] 反射缺少 _iconSprite 字段，懒加载补丁未安装");
                return false;
            }
            bool ok = true;
            ok &= PatchGetter(harmony, typeof(ItemProto), "IconSprite", "ItemIconPostfix", logger);
            ok &= PatchGetter(harmony, typeof(TechProto), "IconSprite", "TechIconPostfix", logger);
            ok &= PatchGetter(harmony, typeof(RecipeProto), "IconSprite", "RecipeIconPostfix", logger);
            LazyPatchesInstalled = ok;
            if (ok)
            {
                logger.LogInfo("[图标] 懒加载 getter 补丁已安装（ItemProto/TechProto/RecipeProto.iconSprite）");
            }
            else
            {
                logger.LogWarning("[图标] 懒加载 getter 补丁安装失败，将回退启动时批量加载");
            }
            return ok;
        }

        private static bool PatchGetter(Harmony harmony, Type protoType, string methodName, string postfixName, ManualLogSource logger)
        {
            try
            {
                MethodInfo getter = AccessTools.PropertyGetter(protoType, "iconSprite");
                if (getter == null)
                {
                    logger.LogWarning("[图标] 找不到 " + protoType.Name + ".iconSprite getter");
                    return false;
                }
                MethodInfo postfix = typeof(IconLoader).GetMethod(postfixName, BindingFlags.Static | BindingFlags.NonPublic);
                if (postfix == null)
                {
                    logger.LogWarning("[图标] 找不到 IconLoader." + postfixName);
                    return false;
                }
                harmony.Patch(getter, postfix: new HarmonyMethod(postfix));
                return true;
            }
            catch (Exception ex)
            {
                logger.LogWarning("[图标] patch " + protoType.Name + ".iconSprite 失败：" + ex.Message);
                return false;
            }
        }

        private static void ItemIconPostfix(ItemProto __instance, ref Sprite __result)
        {
            if (SuppressLazyLoad || __instance == null || __result != null)
            {
                return;
            }
            IconSpec spec;
            if (!itemSpecs.TryGetValue(__instance.ID, out spec))
            {
                return;
            }
            try
            {
                lock (sync)
                {
                    Sprite sprite = LoadBySpec(spec);
                    if (sprite != null)
                    {
                        itemIconField.SetValue(__instance, sprite);
                        __result = sprite;
                    }
                }
            }
            catch (Exception ex)
            {
                LogOnce("[图标] 懒加载物品 " + __instance.ID + " 失败：" + ex.Message);
            }
        }

        private static void TechIconPostfix(TechProto __instance, ref Sprite __result)
        {
            if (SuppressLazyLoad || __instance == null || __result != null)
            {
                return;
            }
            IconSpec spec;
            if (!techSpecs.TryGetValue(__instance.ID, out spec))
            {
                return;
            }
            try
            {
                lock (sync)
                {
                    Sprite sprite = LoadBySpec(spec);
                    if (sprite != null)
                    {
                        techIconField.SetValue(__instance, sprite);
                        __result = sprite;
                    }
                }
            }
            catch (Exception ex)
            {
                LogOnce("[图标] 懒加载科技 " + __instance.ID + " 失败：" + ex.Message);
            }
        }

        /// <summary>配方图标：自定义 PNG 优先；否则跟随产物图标（会触发产物的懒加载）。</summary>
        private static void RecipeIconPostfix(RecipeProto __instance, ref Sprite __result)
        {
            if (SuppressLazyLoad || __instance == null)
            {
                return;
            }
            IconSpec spec;
            if (!recipeSpecs.TryGetValue(__instance.ID, out spec))
            {
                return;
            }
            try
            {
                lock (sync)
                {
                    if (recipeResolved.Contains(spec.Id))
                    {
                        return;
                    }
                    recipeResolved.Add(spec.Id);
                    Sprite sprite = null;
                    if (!string.IsNullOrEmpty(spec.FileName))
                    {
                        sprite = LoadBySpec(spec);
                    }
                    if (sprite == null && __instance.Results != null && __instance.Results.Length > 0)
                    {
                        ItemProto product = LDB.items.Select(__instance.Results[0]);
                        if (product != null)
                        {
                            sprite = product.iconSprite; // 触发 ItemProto 的懒加载
                        }
                    }
                    if (sprite == null)
                    {
                        sprite = LoadFallback(spec);
                    }
                    if (sprite != null)
                    {
                        recipeIconField.SetValue(__instance, sprite);
                        __instance.hasIcon = true;
                        __result = sprite;
                    }
                }
            }
            catch (Exception ex)
            {
                LogOnce("[图标] 懒加载配方 " + __instance.ID + " 失败：" + ex.Message);
            }
        }

        // ------------------------------------------------------------------ eager mode
        /// <summary>急切模式：启动时把全部注册图标一次性加载并写回 _iconSprite（原行为，懒加载失败时回退）。</summary>
        internal static void ApplyAllEager()
        {
            ResolveFields();
            int itemOk = 0;
            int techOk = 0;
            int recipeOk = 0;
            int fail = 0;
            foreach (KeyValuePair<int, IconSpec> pair in itemSpecs)
            {
                ItemProto proto = LDB.items.Select(pair.Key);
                if (proto == null)
                {
                    continue;
                }
                Sprite sprite = LoadBySpec(pair.Value);
                if (sprite != null)
                {
                    itemIconField.SetValue(proto, sprite);
                    itemOk++;
                }
                else
                {
                    fail++;
                }
            }
            foreach (KeyValuePair<int, IconSpec> pair in techSpecs)
            {
                TechProto proto = LDB.techs.Select(pair.Key);
                if (proto == null)
                {
                    continue;
                }
                Sprite sprite = LoadBySpec(pair.Value);
                if (sprite != null)
                {
                    techIconField.SetValue(proto, sprite);
                    techOk++;
                }
                else
                {
                    fail++;
                }
            }
            foreach (KeyValuePair<int, IconSpec> pair in recipeSpecs)
            {
                RecipeProto proto = LDB.recipes.Select(pair.Key);
                if (proto == null)
                {
                    continue;
                }
                Sprite sprite = null;
                if (!string.IsNullOrEmpty(pair.Value.FileName))
                {
                    sprite = LoadBySpec(pair.Value);
                }
                if (sprite == null && proto.Results != null && proto.Results.Length > 0)
                {
                    ItemProto product = LDB.items.Select(proto.Results[0]);
                    if (product != null)
                    {
                        sprite = product.iconSprite;
                    }
                }
                if (sprite == null)
                {
                    sprite = LoadFallback(pair.Value);
                }
                if (sprite != null)
                {
                    recipeIconField.SetValue(proto, sprite);
                    proto.hasIcon = true;
                    recipeOk++;
                }
            }
            log.LogInfo(string.Format(
                "[图标] 启动时批量加载完成：物品 {0}/{1} / 科技 {2}/{3} / 配方 {4}/{5}；文件 {6} / 原版回退 {7} / 缺图 {8}",
                itemOk, itemSpecs.Count, techOk, techSpecs.Count, recipeOk, recipeSpecs.Count,
                fileLoaded, fallbackLoaded, missing + fail));
        }

        // ------------------------------------------------------------------ verify (lazy)
        /// <summary>懒加载验收：抽样调用 getter（模拟 UI 首次访问），断言 _iconSprite 被回填。</summary>
        internal static void VerifyLazy(ManualLogSource logger)
        {
            int checkedCount = 0;
            int okCount = 0;
            List<string> samples = new List<string>();
            try
            {
                int itemTried = 0;
                foreach (KeyValuePair<int, IconSpec> pair in itemSpecs)
                {
                    if (itemTried >= 3)
                    {
                        break;
                    }
                    ItemProto proto = LDB.items.Select(pair.Key);
                    if (proto == null)
                    {
                        continue;
                    }
                    itemTried++;
                    checkedCount++;
                    Sprite sprite = proto.iconSprite; // 触发 Postfix
                    object fieldValue = itemIconField != null ? itemIconField.GetValue(proto) : null;
                    if (sprite != null && ReferenceEquals(sprite, fieldValue))
                    {
                        okCount++;
                        samples.Add("item " + pair.Key);
                    }
                }
                int techTried = 0;
                foreach (KeyValuePair<int, IconSpec> pair in techSpecs)
                {
                    if (techTried >= 2)
                    {
                        break;
                    }
                    TechProto proto = LDB.techs.Select(pair.Key);
                    if (proto == null)
                    {
                        continue;
                    }
                    techTried++;
                    checkedCount++;
                    Sprite sprite = proto.iconSprite;
                    object fieldValue = techIconField != null ? techIconField.GetValue(proto) : null;
                    if (sprite != null && ReferenceEquals(sprite, fieldValue))
                    {
                        okCount++;
                        samples.Add("tech " + pair.Key);
                    }
                }
                int recipeTried = 0;
                foreach (KeyValuePair<int, IconSpec> pair in recipeSpecs)
                {
                    if (recipeTried >= 2)
                    {
                        break;
                    }
                    RecipeProto proto = LDB.recipes.Select(pair.Key);
                    if (proto == null)
                    {
                        continue;
                    }
                    recipeTried++;
                    checkedCount++;
                    Sprite sprite = proto.iconSprite;
                    object fieldValue = recipeIconField != null ? recipeIconField.GetValue(proto) : null;
                    if (sprite != null && ReferenceEquals(sprite, fieldValue))
                    {
                        okCount++;
                        samples.Add("recipe " + pair.Key);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError("[验证] 懒加载图标验证异常：" + ex);
                return;
            }
            bool pass = checkedCount > 0 && okCount == checkedCount;
            logger.LogInfo(string.Format(
                "[验证] 懒加载图标：首次 getter 触发加载 {0}/{1} 成功；文件 {2} / 原版回退 {3} / 缺图 {4}；样例 {5} → {6}",
                okCount, checkedCount, fileLoaded, fallbackLoaded, missing,
                string.Join("、", samples.ToArray()), pass ? "PASS" : "WARN"));
            if (!pass)
            {
                logger.LogWarning("[验证] 懒加载图标抽样未全部成功；若 UI 中图标异常，可把 LazyIcons 改为 false 回退急切加载");
            }
        }

        // ------------------------------------------------------------------ atlas injection helpers
        /// <summary>给 IconSetAtlasFix 用：按 content/icons 文件名加载 Sprite（不写任何 proto 字段）。</summary>
        internal static Sprite LoadSpriteForAtlas(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
            {
                return null;
            }
            string path = ResolvePath(fileName);
            return path != null ? LoadSpriteFile(path) : null;
        }

        /// <summary>
        /// IconSet.Create 之前清空本 MOD 所有 proto 的 _iconSprite：
        /// 避免 9664 个图标挤爆原版 625 格（25×25）图集导致全部索引回落 0。
        /// Create 之后由 IconSetAtlasFix 注入「代表性图标到空闲格」+ 设置索引；急切模式下再补回 sprite。
        /// </summary>
        internal static void ClearOwnSpriteFields()
        {
            ResolveFields();
            int cleared = 0;
            try
            {
                if (itemIconField != null)
                {
                    foreach (int id in itemSpecs.Keys)
                    {
                        ItemProto proto = LDB.items.Select(id);
                        if (proto != null && itemIconField.GetValue(proto) != null)
                        {
                            itemIconField.SetValue(proto, null);
                            cleared++;
                        }
                    }
                }
                if (techIconField != null)
                {
                    foreach (int id in techSpecs.Keys)
                    {
                        TechProto proto = LDB.techs.Select(id);
                        if (proto != null && techIconField.GetValue(proto) != null)
                        {
                            techIconField.SetValue(proto, null);
                            cleared++;
                        }
                    }
                }
                if (recipeIconField != null)
                {
                    foreach (int id in recipeSpecs.Keys)
                    {
                        RecipeProto proto = LDB.recipes.Select(id);
                        if (proto != null && recipeIconField.GetValue(proto) != null)
                        {
                            recipeIconField.SetValue(proto, null);
                            cleared++;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogOnce("[图标] 清空自有 proto 图标失败（图集可能溢出）：" + ex.Message);
            }
            LogOnce("[图标] IconSet 构建前清空自有图标字段 " + cleared + " 个（避免 9664 图标挤爆 625 格图集；随后注入代表性图标）");
        }

        // ------------------------------------------------------------------ resolution
        private static Sprite LoadBySpec(IconSpec spec)
        {
            if (!string.IsNullOrEmpty(spec.FileName))
            {
                string path = ResolvePath(spec.FileName);
                if (path != null)
                {
                    Sprite sprite = LoadSpriteFile(path);
                    if (sprite != null)
                    {
                        fileLoaded++;
                        return sprite;
                    }
                }
            }
            return LoadFallback(spec);
        }

        private static Sprite LoadFallback(IconSpec spec)
        {
            Sprite sprite = LoadResourceSprite(spec.FallbackPath);
            if (sprite != null)
            {
                fallbackLoaded++;
                return sprite;
            }
            sprite = LoadResourceSprite(spec.DefaultFallback);
            if (sprite != null)
            {
                fallbackLoaded++;
                return sprite;
            }
            missing++;
            return null;
        }

        private static Sprite LoadResourceSprite(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }
            return Resources.Load<Sprite>(path);
        }

        private static void ResolveFields()
        {
            if (itemIconField == null)
            {
                itemIconField = typeof(ItemProto).GetField("_iconSprite", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            }
            if (techIconField == null)
            {
                techIconField = typeof(TechProto).GetField("_iconSprite", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            }
            if (recipeIconField == null)
            {
                recipeIconField = typeof(RecipeProto).GetField("_iconSprite", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            }
        }

        private static string ResolvePath(string fileName)
        {
            if (string.IsNullOrEmpty(fileName) || string.IsNullOrEmpty(iconDir))
            {
                return null;
            }
            string name = fileName.Trim();
            if (name.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || Path.IsPathRooted(name))
            {
                return null;
            }
            string candidate = Path.Combine(iconDir, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
            if (!name.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            {
                candidate = Path.Combine(iconDir, name + ".png");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            return null;
        }

        private static Sprite LoadSpriteFile(string path)
        {
            Sprite cached;
            if (fileCache.TryGetValue(path, out cached))
            {
                return cached;
            }
            try
            {
                byte[] data = File.ReadAllBytes(path);
                Texture2D texture = new Texture2D(IconSize, IconSize, TextureFormat.RGBA32, false);
                if (!texture.LoadImage(data))
                {
                    LogOnce("[图标] PNG 解码失败：" + path);
                    return null;
                }
                texture.Apply(false, false);
                texture.name = Path.GetFileNameWithoutExtension(path);
                Sprite sprite = Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height),
                                              new Vector2(0.5f, 0.5f), 100f);
                sprite.name = texture.name;
                fileCache[path] = sprite;
                return sprite;
            }
            catch (Exception ex)
            {
                LogOnce("[图标] 读取失败：" + path + "：" + ex.Message);
                return null;
            }
        }

        private static readonly HashSet<string> logged = new HashSet<string>(StringComparer.Ordinal);

        private static void LogOnce(string message)
        {
            if (!logged.Add(message))
            {
                return;
            }
            if (log != null)
            {
                log.LogWarning(message);
            }
        }
    }
}
