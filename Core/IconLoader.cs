using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx.Logging;
using UnityEngine;

namespace DSPPyPort.Core
{
    internal enum IconTargetKind
    {
        Item,
        Tech,
        Recipe
    }

    /// <summary>一个待应用的自定义图标请求（icon_file → content/icons/&lt;file&gt;）。</summary>
    internal sealed class IconRequest
    {
        internal IconTargetKind Kind;
        internal Proto Target;
        internal string FileName;
        internal int Id;
    }

    /// <summary>
    /// PNG 图标加载（手册 §8.3 B2 的「零 Unity」路线，DSP-MechDawn/CustomIcons 已实机验证）：
    ///   content/icons/&lt;file&gt;.png → Texture2D.LoadImage → Sprite.Create → 反射写 &lt;Proto&gt;._iconSprite。
    /// 必须在 onLoadingFinished（CommonAPI Preload 之后）应用；失败只降级回原版图标，不阻断加载。
    /// </summary>
    internal static class IconLoader
    {
        private const int IconSize = 80;

        private static FieldInfo itemIconField;
        private static FieldInfo techIconField;
        private static FieldInfo recipeIconField;
        private static readonly Dictionary<string, Sprite> cache = new Dictionary<string, Sprite>(StringComparer.Ordinal);

        internal static void ApplyAll(List<IconRequest> requests, List<RecipeProto> recipesFollowProduct,
                                      string contentRoot, ManualLogSource log)
        {
            cache.Clear();
            ResolveFields(log);

            string iconDir = Path.Combine(contentRoot, "icons");
            if (!Directory.Exists(iconDir))
            {
                log.LogWarning("[图标] 未找到图标目录：" + iconDir + "（全部沿用原版近似图标）");
            }

            int appliedItems = 0;
            int appliedTechs = 0;
            int appliedRecipes = 0;
            int missing = 0;
            List<string> missingSamples = new List<string>();

            for (int i = 0; i < requests.Count; i++)
            {
                IconRequest request = requests[i];
                string path = ResolvePath(iconDir, request.FileName);
                if (path == null)
                {
                    missing++;
                    if (missingSamples.Count < 8)
                    {
                        missingSamples.Add(request.FileName);
                    }
                    continue;
                }
                Sprite sprite = LoadSprite(path, log);
                if (sprite == null)
                {
                    missing++;
                    continue;
                }
                try
                {
                    switch (request.Kind)
                    {
                        case IconTargetKind.Item:
                            if (itemIconField != null)
                            {
                                itemIconField.SetValue(request.Target, sprite);
                                appliedItems++;
                            }
                            break;
                        case IconTargetKind.Tech:
                            if (techIconField != null)
                            {
                                techIconField.SetValue(request.Target, sprite);
                                appliedTechs++;
                            }
                            break;
                        case IconTargetKind.Recipe:
                            if (recipeIconField != null)
                            {
                                recipeIconField.SetValue(request.Target, sprite);
                                RecipeProto recipe = request.Target as RecipeProto;
                                if (recipe != null)
                                {
                                    recipe.hasIcon = true;
                                }
                                appliedRecipes++;
                            }
                            break;
                    }
                }
                catch (Exception ex)
                {
                    log.LogWarning("[图标] 写 " + request.Kind + " " + request.Id + " 图标失败：" + ex.Message);
                }
            }

            // 无自定义图标的配方：图标跟随产物（ItemProto.Preload 时产物还没有自定义图标，这里补一次）
            int followed = 0;
            if (recipeIconField != null)
            {
                for (int i = 0; i < recipesFollowProduct.Count; i++)
                {
                    RecipeProto recipe = recipesFollowProduct[i];
                    if (recipe == null || recipe.Results == null || recipe.Results.Length == 0)
                    {
                        continue;
                    }
                    ItemProto product = LDB.items.Select(recipe.Results[0]);
                    if (product == null || product.iconSprite == null)
                    {
                        continue;
                    }
                    try
                    {
                        recipeIconField.SetValue(recipe, product.iconSprite);
                        recipe.hasIcon = true;
                        followed++;
                    }
                    catch
                    {
                        // 配方图标只是观感，失败不打断
                    }
                }
            }

            string message = string.Format(
                "[图标] 自定义图标已应用：物品 {0} / 科技 {1} / 配方 {2}（跟随产物 {3}）；缺图 {4} 个（沿用原版图标）",
                appliedItems, appliedTechs, appliedRecipes, followed, missing);
            if (missing > 0 && missingSamples.Count > 0)
            {
                log.LogWarning(message + "；缺图样例：" + string.Join("、", missingSamples.ToArray()));
            }
            else
            {
                log.LogInfo(message);
            }
        }

        private static void ResolveFields(ManualLogSource log)
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
            if (itemIconField == null || techIconField == null || recipeIconField == null)
            {
                log.LogWarning("[图标] 反射缺少 _iconSprite 字段（Item=" + (itemIconField != null) +
                               " Tech=" + (techIconField != null) + " Recipe=" + (recipeIconField != null) +
                               "），自定义图标降级为原版图标");
            }
        }

        private static string ResolvePath(string iconDir, string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
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

        private static Sprite LoadSprite(string path, ManualLogSource log)
        {
            Sprite cached;
            if (cache.TryGetValue(path, out cached))
            {
                return cached;
            }
            try
            {
                byte[] data = File.ReadAllBytes(path);
                Texture2D texture = new Texture2D(IconSize, IconSize, TextureFormat.RGBA32, false);
                if (!texture.LoadImage(data))
                {
                    log.LogWarning("[图标] PNG 解码失败：" + path);
                    return null;
                }
                texture.Apply(false, false);
                texture.name = Path.GetFileNameWithoutExtension(path);
                Sprite sprite = Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height),
                                              new Vector2(0.5f, 0.5f), 100f);
                sprite.name = texture.name;
                cache[path] = sprite;
                return sprite;
            }
            catch (Exception ex)
            {
                log.LogWarning("[图标] 读取失败：" + path + "：" + ex.Message);
                return null;
            }
        }
    }
}
