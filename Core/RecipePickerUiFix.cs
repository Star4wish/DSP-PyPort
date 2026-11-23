using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Events;
using UnityEngine.UI;

namespace DSPPyPort.Core
{
    /// <summary>
    /// 配方选择器 UI（队列 3 / task-18，2026-11-19，合并原 RecipePickerPagingFix 后仍只有一套 patch）。
    ///
    /// 原版 `UIRecipePicker`（14 列 × 8 行 = 112 格）：
    ///   - `RefreshIcons()` 只在 `GridIndex/1000 == currentType`（currentType 仅 1/2 两个按钮）时渲染；
    ///   - 格子内容是 `RawImage` + ComputeBuffer 索引到 `GameMain.iconSet` 图集；
    ///   - 我们的配方按类型分页后 page 到 15，原版 UI 无法翻页，且图集只有 64 个空闲格 → 图标近似。
    ///
    /// 本补丁（创世之书风格）：
    ///   1. `_OnCreate` 后置：在 112 个格位创建独立 `UI.Image`（Sprite 直出 → PY 图标 1:1 精确显示；
    ///      原版图集 RawImage 改为全透明，仅保留其 EventTrigger 的悬停/点击路径，零改动复用原版交互）；
    ///   2. `RefreshIcons` 前缀：按「页签分组」收集当前 `filter` 类型下所有已解锁配方，按 GridIndex 排序后
    ///      切 112 条/页填 `protoArray`（原版悬停提示/点击选择/高亮全部继续工作）；
    ///   3. 页签：分类页签带（带 RectMask2D，可横向滚轮滚动）。filter≠None 时按 PY 配方大类
    ///      （content JSON 的 category，171 种归并为 8 大类）分组；filter=None 时按 recipe type 合并分组；
    ///   4. 右侧纵向滚动条（拖拽/点击跳页）+ 页码文本「第 X/Y 页 · N 条」，替代原版 1/2 翻页按钮；
    ///   5. 鼠标滚轮在配方区翻页、在页签带滚动页签。
    ///
    /// 兼容性：`GridIndex` 语义与原有分页数据完全保留（只是读取顺序），不改任何 ID/JSON。
    /// 字段/方法结构不匹配时 WARN 并回退原版行为。
    /// </summary>
    internal static class RecipePickerUiFix
    {
        internal const int Cols = 14;
        internal const int Rows = 8;
        internal const int CellCount = Cols * Rows;
        internal const float TabWidth = 76f;

        internal static bool Enabled = true;
        internal static bool Installed { get; private set; }

        private static ManualLogSource log;
        private static FieldInfo indexArrayField;
        private static FieldInfo protoArrayField;
        private static FieldInfo filterField;
        private static FieldInfo currentTypeField;
        private static MethodInfo refreshMethod;
        private static PickerUi ui;
        private static bool contentReady;

        // ------------------------------------------------------------------ 大类定义
        private sealed class MacroDef
        {
            internal readonly string Zh;
            internal readonly string En;
            internal readonly string[] Keywords;

            internal MacroDef(string zh, string en, params string[] keywords)
            {
                Zh = zh;
                En = en;
                Keywords = keywords;
            }
        }

        /// <summary>配方大类（页签固定顺序，共 8 个 + 全部）。</summary>
        private static readonly MacroDef[] Macros =
        {
            new MacroDef("采掘", "Mining", "mining", "frack", "coalbed", "drill", "quarry", "excavat", "deposit", "well", "ore-"),
            new MacroDef("冶炼", "Smelt", "smelt", "casting", "foundry", "blast", "eaf", "anneal", "sinter", "kiln", "glass", "flotation"),
            new MacroDef("化工", "Chem", "chem", "fluid", "gas-", "distil", "evaporat", "mixer", "refin", "fraction", "centrif",
                "carbon", "catalyst", "ammonia", "oil", "petroleum", "plastic", "acid", "solvent", "electrolys", "hydrogen",
                "chlor", "sulfur", "sulphur", "nitr", "oxygen", "steam", "water", "syngas", "cracking", "tower", "combustion"),
            new MacroDef("组装", "Craft", "craft", "electronic", "pcb", "chip", "assembl", "gear", "frame", "part", "motor",
                "computer", "circuit", "module", "machine", "tool", "structure", "construc", "packag", "barrel", "canister", "crate"),
            new MacroDef("生物", "Bio", "bio", "compost", "slaughter", "nurser", "incubat", "creature", "farm", "animal",
                "ulric", "grod", "korlex", "arqad", "simik", "pulp", "wood", "plant", "algae", "fish", "meat", "spore",
                "moss", "mushroom", "fungus", "seed", "tree", "cell", "tissue", "organ", "gene"),
            new MacroDef("能源", "Power", "power", "nuclear", "fusion", "solar", "batter", "generat", "turbin", "fuel",
                "energy", "reactor", "fission", "neutron", "mox"),
            new MacroDef("科研", "Sci", "research", "data-array", "genlab", "lab", "science", "matrix", "quantum", "study",
                "analysis", "observ", "experiment"),
            new MacroDef("其它", "Misc")
        };

        // ------------------------------------------------------------------ 页签分组
        private sealed class Group
        {
            internal string Zh;
            internal string En;
            internal int Macro = -1;
            internal int TypeFilter = -1;
            internal int[] Types;

            internal bool Matches(RecipeProto proto)
            {
                if (proto == null)
                {
                    return false;
                }
                int type = (int)proto.Type;
                if (TypeFilter >= 0 && type != TypeFilter)
                {
                    return false;
                }
                if (Types != null && Array.IndexOf(Types, type) < 0)
                {
                    return false;
                }
                if (Macro >= 0 && MacroOf(RecipeCategories.Get(proto.ID)) != Macro)
                {
                    return false;
                }
                return true;
            }
        }

        // 无 filter（其它 MOD 直接调用 Popup）时按 recipe type 合并分组
        private static readonly string[][] TypeGroupZh = new string[][]
        {
            new string[] { "原版", "Vanilla" },
            new string[] { "基础原料", "Raw" },
            new string[] { "煤化工", "Coal" },
            new string[] { "高科技", "HiTech" },
            new string[] { "替代能源", "AltEnergy" },
            new string[] { "异星生命", "Alien" }
        };
        private static readonly int[][] TypeGroupTypes = new int[][]
        {
            new int[] { 1, 4 },
            new int[] { 60, 61, 62, 63, 64, 65, 66, 67 },
            new int[] { 68, 69, 70, 71, 72 },
            new int[] { 73, 74 },
            new int[] { 75, 76 },
            new int[] { 77 }
        };

        // ------------------------------------------------------------------ per-picker UI 状态
        private sealed class PickerUi
        {
            internal UIRecipePicker Picker;
            internal bool Built;
            internal RectTransform IconRect;
            internal readonly Image[] CellImages = new Image[CellCount];
            internal GameObject TabViewport;
            internal RectTransform TabViewportRect;
            internal RectTransform TabContent;
            internal readonly List<GameObject> TabObjects = new List<GameObject>();
            internal readonly List<UIButton> TabButtons = new List<UIButton>();
            internal readonly List<Text> TabLabels = new List<Text>();
            internal float TabScrollX;
            internal Font Font;
            internal int FontSize = 11;
            internal GameObject PageLabelObject;
            internal Text PageLabel;
            internal GameObject TrackObject;
            internal RectTransform TrackRect;
            internal Image TrackImage;
            internal GameObject ThumbObject;
            internal RectTransform ThumbRect;
            internal Image ThumbImage;
            internal readonly List<Group> Groups = new List<Group>();
            internal bool GroupsBuilt;
            internal ERecipeType ActiveFilter;
            internal int GroupIndex;
            internal int Page = 1;
            internal int TotalPages = 1;
            internal int TotalRecipes;
            internal int LastOpenLogFrame = -1;
        }

        // ------------------------------------------------------------------ install
        internal static void Install(Harmony harmony, ManualLogSource logger)
        {
            log = logger;
            try
            {
                Type type = typeof(UIRecipePicker);
                indexArrayField = type.GetField("indexArray", BindingFlags.Instance | BindingFlags.NonPublic);
                protoArrayField = type.GetField("protoArray", BindingFlags.Instance | BindingFlags.NonPublic);
                filterField = type.GetField("filter", BindingFlags.Instance | BindingFlags.NonPublic);
                currentTypeField = type.GetField("currentType", BindingFlags.Instance | BindingFlags.NonPublic);
                refreshMethod = type.GetMethod("RefreshIcons", BindingFlags.Instance | BindingFlags.NonPublic);
                MethodInfo createMethod = type.GetMethod("_OnCreate", BindingFlags.Instance | BindingFlags.NonPublic);
                MethodInfo openMethod = type.GetMethod("_OnOpen", BindingFlags.Instance | BindingFlags.NonPublic);
                MethodInfo clickMethod = type.GetMethod("OnTypeButtonClick", BindingFlags.Instance | BindingFlags.NonPublic);
                MethodInfo updateMethod = type.GetMethod("_OnUpdate", BindingFlags.Instance | BindingFlags.NonPublic);

                if (indexArrayField == null || protoArrayField == null || filterField == null ||
                    currentTypeField == null || refreshMethod == null || createMethod == null || clickMethod == null)
                {
                    log.LogWarning("[选择器UI] UIRecipePicker 结构不匹配（字段/方法改名？），配方选择器 UI 补丁未安装；" +
                                   "回退原版行为（只显示 GridIndex 千位 1/2 的配方）");
                    return;
                }

                harmony.Patch(createMethod, postfix: new HarmonyMethod(
                    typeof(RecipePickerUiFix).GetMethod("CreatePostfix", BindingFlags.Static | BindingFlags.NonPublic)));
                harmony.Patch(refreshMethod, prefix: new HarmonyMethod(
                    typeof(RecipePickerUiFix).GetMethod("RefreshIconsPrefix", BindingFlags.Static | BindingFlags.NonPublic)));
                harmony.Patch(clickMethod, prefix: new HarmonyMethod(
                    typeof(RecipePickerUiFix).GetMethod("OnTypeButtonClickPrefix", BindingFlags.Static | BindingFlags.NonPublic)));
                if (openMethod != null)
                {
                    harmony.Patch(openMethod, postfix: new HarmonyMethod(
                        typeof(RecipePickerUiFix).GetMethod("OpenPostfix", BindingFlags.Static | BindingFlags.NonPublic)));
                }
                if (updateMethod != null)
                {
                    harmony.Patch(updateMethod, postfix: new HarmonyMethod(
                        typeof(RecipePickerUiFix).GetMethod("UpdatePostfix", BindingFlags.Static | BindingFlags.NonPublic)));
                }
                Installed = true;
                log.LogInfo("[选择器UI] 配方选择器 UI 补丁已安装（Sprite 直出图标 + 分类页签 + 纵向滚动条 + 页码；" +
                            "配置 [UI] EnableRecipePickerUi 可关闭）");
            }
            catch (Exception ex)
            {
                log.LogError("[选择器UI] 安装失败（回退原版行为）：" + ex);
            }
        }

        /// <summary>内容注册完成后调用：打印分组数据可用性（category 覆盖率/每类型页数）。</summary>
        internal static void LogSummary(ManualLogSource logger)
        {
            contentReady = true;
            try
            {
                RecipeProto[] dataArray = LDB.recipes != null ? LDB.recipes.dataArray : null;
                if (dataArray == null)
                {
                    return;
                }
                int total = 0;
                int withCategory = 0;
                Dictionary<int, int> macroCounts = new Dictionary<int, int>();
                Dictionary<int, int> typePages = new Dictionary<int, int>();
                for (int i = 0; i < dataArray.Length; i++)
                {
                    RecipeProto proto = dataArray[i];
                    if (proto == null || proto.GridIndex < 1101)
                    {
                        continue;
                    }
                    total++;
                    if (!string.IsNullOrEmpty(RecipeCategories.Get(proto.ID)))
                    {
                        withCategory++;
                    }
                    int macro = MacroOf(RecipeCategories.Get(proto.ID));
                    int c;
                    macroCounts.TryGetValue(macro, out c);
                    macroCounts[macro] = c + 1;
                    int page = proto.GridIndex / 1000;
                    int t = (int)proto.Type;
                    int p;
                    typePages.TryGetValue(t, out p);
                    if (page > p)
                    {
                        typePages[t] = page;
                    }
                }
                string macros = string.Empty;
                for (int m = 0; m < Macros.Length; m++)
                {
                    int c;
                    macroCounts.TryGetValue(m, out c);
                    if (c > 0)
                    {
                        if (macros.Length > 0)
                        {
                            macros += "、";
                        }
                        macros += Macros[m].Zh + "=" + c;
                    }
                }
                logger.LogInfo("[选择器UI] 页签分组数据：配方 " + total + " 条，其中带 PY category " + withCategory + " 条" +
                               "（大类分布：" + macros + "）；按类型的最大页数（前 8 个）：" + TopPages(typePages));
            }
            catch (Exception ex)
            {
                logger.LogWarning("[选择器UI] 分组数据统计失败：" + ex.Message);
            }
        }

        private static string TopPages(Dictionary<int, int> typePages)
        {
            List<KeyValuePair<int, int>> list = new List<KeyValuePair<int, int>>(typePages);
            list.Sort(delegate(KeyValuePair<int, int> a, KeyValuePair<int, int> b)
            {
                return b.Value.CompareTo(a.Value);
            });
            string result = string.Empty;
            for (int i = 0; i < list.Count && i < 8; i++)
            {
                if (result.Length > 0)
                {
                    result += "、";
                }
                result += "type" + list[i].Key + ":" + list[i].Value + "页";
            }
            return result;
        }

        // ------------------------------------------------------------------ 分组
        private static int MacroOf(string category)
        {
            if (string.IsNullOrEmpty(category))
            {
                return Macros.Length - 1; // 其它
            }
            string c = category.ToLowerInvariant();
            for (int m = 0; m < Macros.Length; m++)
            {
                string[] keywords = Macros[m].Keywords;
                if (keywords == null)
                {
                    continue;
                }
                for (int k = 0; k < keywords.Length; k++)
                {
                    if (c.IndexOf(keywords[k], StringComparison.Ordinal) >= 0)
                    {
                        return m;
                    }
                }
            }
            return Macros.Length - 1;
        }

        private static void RebuildGroupsIfNeeded(PickerUi u)
        {
            ERecipeType filter = filterField != null ? (ERecipeType)filterField.GetValue(u.Picker) : ERecipeType.None;
            if (u.GroupsBuilt && (int)u.ActiveFilter == (int)filter)
            {
                return;
            }
            u.ActiveFilter = filter;
            u.GroupsBuilt = true;
            u.Groups.Clear();
            u.GroupIndex = 0;
            u.Page = 1;
            if ((int)filter != 0)
            {
                BuildCategoryGroups(u, filter);
            }
            else
            {
                BuildTypeGroups(u);
            }
            RebuildTabButtons(u);
            if (log != null)
            {
                log.LogInfo("[选择器UI] 分组重建：filter=" + (int)filter + "（" + GroupModeName(filter) + "），页签 " +
                            u.Groups.Count + " 个");
            }
        }

        private static string GroupModeName(ERecipeType filter)
        {
            return (int)filter != 0 ? "按配方大类" : "按 recipe type 合并";
        }

        private static void BuildCategoryGroups(PickerUi u, ERecipeType filter)
        {
            int[] counts = new int[Macros.Length];
            RecipeProto[] dataArray = LDB.recipes != null ? LDB.recipes.dataArray : null;
            if (dataArray != null)
            {
                for (int i = 0; i < dataArray.Length; i++)
                {
                    RecipeProto proto = dataArray[i];
                    if (proto == null || proto.GridIndex < 1101 || (int)proto.Type != (int)filter)
                    {
                        continue;
                    }
                    counts[MacroOf(RecipeCategories.Get(proto.ID))]++;
                }
            }
            Group all = new Group { Zh = "全部", En = "All", Macro = -1, TypeFilter = (int)filter };
            u.Groups.Add(all);
            for (int m = 0; m < Macros.Length; m++)
            {
                if (counts[m] > 0)
                {
                    u.Groups.Add(new Group { Zh = Macros[m].Zh, En = Macros[m].En, Macro = m, TypeFilter = (int)filter });
                }
            }
        }

        private static void BuildTypeGroups(PickerUi u)
        {
            bool[] present = new bool[TypeGroupTypes.Length];
            bool any = false;
            HashSet<int> leftovers = new HashSet<int>();
            RecipeProto[] dataArray = LDB.recipes != null ? LDB.recipes.dataArray : null;
            if (dataArray != null)
            {
                for (int i = 0; i < dataArray.Length; i++)
                {
                    RecipeProto proto = dataArray[i];
                    if (proto == null || proto.GridIndex < 1101)
                    {
                        continue;
                    }
                    any = true;
                    int type = (int)proto.Type;
                    bool found = false;
                    for (int g = 0; g < TypeGroupTypes.Length; g++)
                    {
                        if (Array.IndexOf(TypeGroupTypes[g], type) >= 0)
                        {
                            present[g] = true;
                            found = true;
                            break;
                        }
                    }
                    if (!found)
                    {
                        leftovers.Add(type);
                    }
                }
            }
            if (!any)
            {
                return;
            }
            u.Groups.Add(new Group { Zh = "全部", En = "All" });
            for (int g = 0; g < TypeGroupTypes.Length; g++)
            {
                if (present[g])
                {
                    u.Groups.Add(new Group
                    {
                        Zh = TypeGroupZh[g][0],
                        En = TypeGroupZh[g][1],
                        Types = TypeGroupTypes[g]
                    });
                }
            }
            if (leftovers.Count > 0)
            {
                List<int> otherTypes = new List<int>(leftovers);
                otherTypes.Sort();
                u.Groups.Add(new Group { Zh = "其它", En = "Misc", Types = otherTypes.ToArray() });
            }
        }

        // ------------------------------------------------------------------ 渲染
        private static List<RecipeProto> CollectMatches(PickerUi u, Group group)
        {
            List<RecipeProto> matches = new List<RecipeProto>();
            RecipeProto[] dataArray = LDB.recipes != null ? LDB.recipes.dataArray : null;
            GameHistoryData history = GameMain.history;
            if (dataArray == null)
            {
                return matches;
            }
            for (int i = 0; i < dataArray.Length; i++)
            {
                RecipeProto proto = dataArray[i];
                if (proto == null || proto.GridIndex < 1101)
                {
                    continue;
                }
                // 菜单/无存档场景 history 可能为 null（自测用），此时视为全部已解锁；正式游戏不受影响
                if (history != null && !history.RecipeUnlocked(proto.ID))
                {
                    continue;
                }
                if (group != null && !group.Matches(proto))
                {
                    continue;
                }
                matches.Add(proto);
            }
            matches.Sort(delegate(RecipeProto a, RecipeProto b)
            {
                int c = a.GridIndex.CompareTo(b.GridIndex);
                return c != 0 ? c : a.ID.CompareTo(b.ID);
            });
            return matches;
        }

        private static void RenderPage(UIRecipePicker picker)
        {
            PickerUi u = ui;
            if (u == null || !u.Built || picker == null)
            {
                return;
            }
            RebuildGroupsIfNeeded(u);

            uint[] indexArray = indexArrayField != null ? (uint[])indexArrayField.GetValue(picker) : null;
            RecipeProto[] protoArray = protoArrayField != null ? (RecipeProto[])protoArrayField.GetValue(picker) : null;
            if (indexArray != null)
            {
                Array.Clear(indexArray, 0, indexArray.Length);
            }
            if (protoArray != null)
            {
                Array.Clear(protoArray, 0, protoArray.Length);
            }

            Group group = (u.GroupIndex >= 0 && u.GroupIndex < u.Groups.Count) ? u.Groups[u.GroupIndex] : null;
            List<RecipeProto> matches = CollectMatches(u, group);
            u.TotalRecipes = matches.Count;
            u.TotalPages = Math.Max(1, (matches.Count + CellCount - 1) / CellCount);
            if (u.Page < 1)
            {
                u.Page = 1;
            }
            if (u.Page > u.TotalPages)
            {
                u.Page = u.TotalPages;
            }
            if (currentTypeField != null)
            {
                try
                {
                    currentTypeField.SetValue(picker, u.Page);
                }
                catch
                {
                }
            }

            int offset = (u.Page - 1) * CellCount;
            IconSet iconSet = GameMain.iconSet;
            for (int cell = 0; cell < CellCount; cell++)
            {
                int index = offset + cell;
                RecipeProto proto = index < matches.Count ? matches[index] : null;
                if (protoArray != null && cell < protoArray.Length)
                {
                    protoArray[cell] = proto;
                }
                if (indexArray != null && cell < indexArray.Length)
                {
                    uint iconIndex = 0;
                    if (proto != null && iconSet != null && proto.ID >= 0 && proto.ID < iconSet.recipeIconIndex.Length)
                    {
                        iconIndex = iconSet.recipeIconIndex[proto.ID];
                    }
                    indexArray[cell] = iconIndex;
                }
                Image image = u.CellImages[cell];
                if (image != null)
                {
                    Sprite sprite = null;
                    if (proto != null)
                    {
                        try
                        {
                            sprite = proto.iconSprite;
                        }
                        catch (Exception ex)
                        {
                            LogOnce("[选择器UI] 读取配方图标失败（recipe " + proto.ID + "）：" + ex.Message);
                        }
                    }
                    image.sprite = sprite;
                    image.enabled = sprite != null;
                }
            }
            UpdateFooter(u, group);
        }

        private static void UpdateFooter(PickerUi u, Group group)
        {
            if (u.PageLabel != null)
            {
                string groupName = group != null ? group.Zh : "全部";
                u.PageLabel.text = "第 " + u.Page + "/" + u.TotalPages + " 页 · " + u.TotalRecipes + " 条 · " + groupName;
            }
            for (int i = 0; i < u.TabButtons.Count; i++)
            {
                UIButton button = u.TabButtons[i];
                if (button != null)
                {
                    bool selected = i == u.GroupIndex;
                    button.highlighted = selected;
                    if (button.button != null)
                    {
                        button.button.interactable = !selected;
                    }
                }
                if (i < u.TabLabels.Count && u.TabLabels[i] != null)
                {
                    u.TabLabels[i].color = i == u.GroupIndex
                        ? new Color(1f, 0.96f, 0.72f, 1f)
                        : new Color(0.82f, 0.82f, 0.82f, 1f);
                }
            }
            if (u.TrackObject != null)
            {
                u.TrackObject.SetActive(u.TotalPages > 1);
            }
            if (u.ThumbRect != null && u.TrackRect != null && u.TotalPages > 1)
            {
                float trackH = u.TrackRect.rect.height;
                if (trackH <= 1f)
                {
                    trackH = 368f;
                }
                float thumbH = Mathf.Clamp(trackH * CellCount / Mathf.Max(CellCount, u.TotalRecipes), 18f, trackH);
                u.ThumbRect.sizeDelta = new Vector2(u.ThumbRect.sizeDelta.x, thumbH);
                float offset = u.TotalPages <= 1 ? 0f : ((u.Page - 1f) / (u.TotalPages - 1f)) * (trackH - thumbH);
                u.ThumbRect.anchoredPosition = new Vector2(0f, -offset);
            }
        }

        // ------------------------------------------------------------------ 页签 UI
        private static void RebuildTabButtons(PickerUi u)
        {
            for (int i = 0; i < u.TabObjects.Count; i++)
            {
                if (u.TabObjects[i] != null)
                {
                    UnityEngine.Object.Destroy(u.TabObjects[i]);
                }
            }
            u.TabObjects.Clear();
            u.TabButtons.Clear();
            u.TabLabels.Clear();
            u.TabScrollX = 0f;

            if (u.TabViewport == null || u.TabContent == null || u.Picker == null)
            {
                return;
            }
            UIButton template = u.Picker.typeButton1;
            if (template == null)
            {
                LogOnce("[选择器UI] 缺少 typeButton1 模板，无法创建页签");
                return;
            }
            bool show = u.Groups.Count > 1;
            u.TabViewport.SetActive(show);
            if (!show)
            {
                return;
            }
            for (int i = 0; i < u.Groups.Count; i++)
            {
                Group group = u.Groups[i];
                GameObject tabObject = UnityEngine.Object.Instantiate(template.gameObject, u.TabContent, false);
                tabObject.name = "PY_Tab_" + i;
                tabObject.SetActive(true);
                RectTransform rect = (RectTransform)tabObject.transform;
                rect.anchorMin = new Vector2(0f, 0.5f);
                rect.anchorMax = new Vector2(0f, 0.5f);
                rect.pivot = new Vector2(0f, 0.5f);
                float height = rect.sizeDelta.y > 1f ? rect.sizeDelta.y : 36f;
                rect.sizeDelta = new Vector2(TabWidth - 2f, height);
                rect.anchoredPosition = new Vector2(i * TabWidth, 0f);

                Transform icon = tabObject.transform.Find("icon");
                if (icon != null)
                {
                    icon.gameObject.SetActive(false);
                }
                Text label = CreateTabLabel(tabObject, u, group);
                UIButton button = tabObject.GetComponent<UIButton>();
                if (button != null)
                {
                    int captured = i;
                    button.onClick += delegate(int _)
                    {
                        OnTabClicked(captured);
                    };
                }
                u.TabObjects.Add(tabObject);
                u.TabButtons.Add(button);
                u.TabLabels.Add(label);
            }
            if (u.TabContent != null)
            {
                u.TabContent.sizeDelta = new Vector2(u.Groups.Count * TabWidth, u.TabContent.sizeDelta.y);
                u.TabContent.anchoredPosition = new Vector2(0f, u.TabContent.anchoredPosition.y);
            }
        }

        private static Text CreateTabLabel(GameObject tabObject, PickerUi u, Group group)
        {
            GameObject labelObject = new GameObject("label", typeof(RectTransform));
            labelObject.transform.SetParent(tabObject.transform, false);
            RectTransform rect = (RectTransform)labelObject.transform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            Text text = labelObject.AddComponent<Text>();
            text.font = u.Font;
            text.fontSize = u.FontSize > 0 ? Mathf.Max(9, u.FontSize - 2) : 10;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = new Color(0.82f, 0.82f, 0.82f, 1f);
            text.raycastTarget = false;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.text = group.Zh + " " + group.En;
            return text;
        }

        private static void OnTabClicked(int index)
        {
            PickerUi u = ui;
            if (u == null || !u.Built || u.Picker == null)
            {
                return;
            }
            if (index < 0 || index >= u.Groups.Count)
            {
                return;
            }
            u.GroupIndex = index;
            u.Page = 1;
            RenderPage(u.Picker);
            if (log != null)
            {
                log.LogInfo("[选择器UI] 页签切换：" + u.Groups[index].Zh + "（filter=" + (int)u.ActiveFilter + "，配方 " +
                            u.TotalRecipes + " 条 / " + u.TotalPages + " 页）");
            }
        }

        private static void ScrollTabs(PickerUi u, float wheel)
        {
            if (u.TabViewportRect == null || u.TabContent == null)
            {
                return;
            }
            float viewportWidth = u.TabViewportRect.rect.width;
            float contentWidth = u.TabContent.sizeDelta.x;
            if (contentWidth <= viewportWidth + 1f)
            {
                return;
            }
            float min = viewportWidth - contentWidth;
            u.TabScrollX = Mathf.Clamp(u.TabScrollX - wheel * TabWidth * 0.6f, min, 0f);
            u.TabContent.anchoredPosition = new Vector2(u.TabScrollX, u.TabContent.anchoredPosition.y);
        }

        // ------------------------------------------------------------------ 滚动条
        private static Camera GetCamera()
        {
            try
            {
                if (UIRoot.instance != null && UIRoot.instance.overlayCanvas != null)
                {
                    return UIRoot.instance.overlayCanvas.worldCamera;
                }
            }
            catch
            {
            }
            return null;
        }

        private static void AddScrollbarPointerHandlers(GameObject target, PickerUi u, RectTransform trackRect)
        {
            EventTrigger trigger = target.GetComponent<EventTrigger>();
            if (trigger == null)
            {
                trigger = target.AddComponent<EventTrigger>();
            }
            UnityAction<BaseEventData> action = delegate(BaseEventData data)
            {
                UpdatePageFromPointer(u, data as PointerEventData, trackRect);
            };
            EventTriggerType[] types = { EventTriggerType.PointerDown, EventTriggerType.Drag, EventTriggerType.PointerUp };
            for (int i = 0; i < types.Length; i++)
            {
                EventTrigger.Entry entry = new EventTrigger.Entry();
                entry.eventID = types[i];
                entry.callback.AddListener(action);
                trigger.triggers.Add(entry);
            }
        }

        private static void UpdatePageFromPointer(PickerUi u, PointerEventData data, RectTransform trackRect)
        {
            if (u == null || u.Picker == null || data == null || trackRect == null || u.TotalPages <= 1)
            {
                return;
            }
            Vector2 local;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(trackRect, data.position, GetCamera(), out local))
            {
                return;
            }
            float height = trackRect.rect.height;
            if (height <= 1f)
            {
                return;
            }
            float normalized = Mathf.Clamp01(-local.y / height);
            int page = 1 + Mathf.RoundToInt(normalized * (u.TotalPages - 1));
            if (page != u.Page)
            {
                u.Page = page;
                RenderPage(u.Picker);
            }
        }

        // ------------------------------------------------------------------ harmony callbacks
        private static void CreatePostfix(UIRecipePicker __instance)
        {
            try
            {
                if (__instance == null)
                {
                    return;
                }
                if (ui != null && ui.Built && ui.Picker != __instance)
                {
                    // 旧 picker 的 UI 子物体已随旧对象销毁，只清引用
                    ui = null;
                }
                if (ui != null && ui.Built)
                {
                    return;
                }
                PickerUi created = BuildUi(__instance);
                if (created != null)
                {
                    ui = created;
                    log.LogInfo("[选择器UI] 已创建：112 个 Sprite 直出格位 + 页签带 + 滚动条 + 页码（iconImage " +
                                created.IconRect.rect.width.ToString("0") + "x" + created.IconRect.rect.height.ToString("0") + "）");
                }
            }
            catch (Exception ex)
            {
                log.LogError("[选择器UI] 创建 UI 失败（回退原版渲染）：" + ex);
            }
        }

        private static bool RefreshIconsPrefix(UIRecipePicker __instance)
        {
            try
            {
                if (ui == null || !ui.Built)
                {
                    return true;
                }
                RenderPage(__instance);
                return false;
            }
            catch (Exception ex)
            {
                LogOnce("[选择器UI] RefreshIcons 接管失败，回退原实现：" + ex.Message);
                return true;
            }
        }

        private static bool OnTypeButtonClickPrefix(UIRecipePicker __instance, int type)
        {
            try
            {
                if (ui == null || !ui.Built)
                {
                    return true;
                }
                PickerUi u = ui;
                RebuildGroupsIfNeeded(u);
                if (type == 1)
                {
                    u.Page = Mathf.Max(1, u.Page - 1);
                }
                else if (type == 2)
                {
                    u.Page = Mathf.Min(Mathf.Max(1, u.TotalPages), u.Page + 1);
                }
                else if (type > 0)
                {
                    u.Page = Mathf.Clamp(type, 1, Mathf.Max(1, u.TotalPages));
                }
                RenderPage(__instance);
                return false;
            }
            catch (Exception ex)
            {
                LogOnce("[选择器UI] OnTypeButtonClick 接管失败，回退原实现：" + ex.Message);
                return true;
            }
        }

        private static void OpenPostfix(UIRecipePicker __instance)
        {
            try
            {
                PickerUi u = ui;
                if (u == null || !u.Built)
                {
                    return;
                }
                RebuildGroupsIfNeeded(u);
                u.GroupIndex = 0;
                u.Page = 1;
                u.TabScrollX = 0f;
                if (u.TabContent != null)
                {
                    u.TabContent.anchoredPosition = new Vector2(0f, u.TabContent.anchoredPosition.y);
                }
                RenderPage(__instance);
                if (u.LastOpenLogFrame != Time.frameCount)
                {
                    u.LastOpenLogFrame = Time.frameCount;
                    Group group = u.Groups.Count > 0 ? u.Groups[0] : null;
                    log.LogInfo("[选择器UI] 打开：filter=" + (int)u.ActiveFilter + "（" + GroupModeName(u.ActiveFilter) +
                                "），页签 " + u.Groups.Count + " 个，配方 " + u.TotalRecipes + " 条 / " + u.TotalPages +
                                " 页" + (group != null ? "，当前页签=" + group.Zh : string.Empty));
                }
            }
            catch (Exception ex)
            {
                LogOnce("[选择器UI] 打开初始化失败：" + ex.Message);
            }
        }

        private static void UpdatePostfix(UIRecipePicker __instance)
        {
            try
            {
                PickerUi u = ui;
                if (u == null || !u.Built || __instance == null || !__instance.active)
                {
                    return;
                }
                float wheel = Input.mouseScrollDelta.y;
                if (Mathf.Abs(wheel) < 0.01f)
                {
                    return;
                }
                Camera camera = GetCamera();
                if (u.TabViewportRect != null && u.TabViewport != null && u.TabViewport.activeSelf &&
                    RectTransformUtility.RectangleContainsScreenPoint(u.TabViewportRect, Input.mousePosition, camera))
                {
                    ScrollTabs(u, wheel);
                    return;
                }
                if (u.IconRect != null &&
                    RectTransformUtility.RectangleContainsScreenPoint(u.IconRect, Input.mousePosition, camera))
                {
                    int target = Mathf.Clamp(u.Page + (wheel > 0f ? -1 : 1), 1, Mathf.Max(1, u.TotalPages));
                    if (target != u.Page)
                    {
                        u.Page = target;
                        RenderPage(__instance);
                    }
                }
            }
            catch
            {
                // 滚轮翻页只是便利功能，失败不打断
            }
        }

        // ------------------------------------------------------------------ UI 构建
        private static PickerUi BuildUi(UIRecipePicker picker)
        {
            if (picker == null || picker.iconImage == null)
            {
                LogOnce("[选择器UI] iconImage 为空，跳过 UI 构建");
                return null;
            }
            RectTransform iconRect = picker.iconImage.rectTransform;
            PickerUi u = new PickerUi { Picker = picker, IconRect = iconRect };
            u.Font = FindFont(picker, out u.FontSize);

            // 1) 隐藏图集渲染（保留 RawImage 的 raycast：EventTrigger 的悬停/点击路径不变）
            Color original = picker.iconImage.color;
            picker.iconImage.color = new Color(original.r, original.g, original.b, 0f);

            // 2) 112 个 Sprite 直出格位（挂在 iconImage 上，用其 rect 计算 14×8 格）
            float cellWidth = iconRect.rect.width > 1f ? iconRect.rect.width / Cols : 46f;
            float cellHeight = iconRect.rect.height > 1f ? iconRect.rect.height / Rows : 46f;
            for (int row = 0; row < Rows; row++)
            {
                for (int col = 0; col < Cols; col++)
                {
                    GameObject cellObject = new GameObject("PY_Cell_" + row + "_" + col, typeof(RectTransform));
                    RectTransform cellRect = (RectTransform)cellObject.transform;
                    cellRect.SetParent(iconRect, false);
                    cellRect.anchorMin = new Vector2(0f, 1f);
                    cellRect.anchorMax = new Vector2(0f, 1f);
                    cellRect.pivot = new Vector2(0.5f, 0.5f);
                    cellRect.sizeDelta = new Vector2(Mathf.Max(1f, cellWidth - 1f), Mathf.Max(1f, cellHeight - 1f));
                    cellRect.anchoredPosition = new Vector2(
                        col * cellWidth + cellWidth * 0.5f,
                        -(row * cellHeight + cellHeight * 0.5f));
                    Image image = cellObject.AddComponent<Image>();
                    image.raycastTarget = false;
                    image.enabled = false;
                    u.CellImages[row * Cols + col] = image;
                }
            }

            // 3) 页签带（替换原版 1/2 翻页按钮所在行；按钮隐藏，空间给页签）
            BuildTabStrip(u, picker);

            // 4) 页码文本 + 右侧纵向滚动条
            BuildFooter(u, picker, iconRect);

            // 5) 隐藏原版翻页按钮（功能由页签/滚动条/滚轮接管；隐藏避免与页签重叠）
            if (picker.typeButton1 != null)
            {
                picker.typeButton1.gameObject.SetActive(false);
            }
            if (picker.typeButton2 != null)
            {
                picker.typeButton2.gameObject.SetActive(false);
            }

            u.Built = true;
            return u;
        }

        private static void BuildTabStrip(PickerUi u, UIRecipePicker picker)
        {
            UIButton template = picker.typeButton1;
            if (template == null)
            {
                LogOnce("[选择器UI] typeButton1 为空，页签带不可用（仍可用滚动条/滚轮翻页）");
                return;
            }
            RectTransform templateRect = (RectTransform)template.transform;
            Transform parent = templateRect.parent;
            if (parent == null)
            {
                parent = picker.pickerTrans;
            }
            GameObject viewport = new GameObject("PY_TabViewport", typeof(RectTransform));
            RectTransform viewportRect = (RectTransform)viewport.transform;
            viewportRect.SetParent(parent, false);
            viewportRect.anchorMin = templateRect.anchorMin;
            viewportRect.anchorMax = templateRect.anchorMax;
            viewportRect.pivot = templateRect.pivot;
            viewportRect.anchoredPosition = templateRect.anchoredPosition;
            float viewportWidth = picker.iconImage != null && picker.iconImage.rectTransform.rect.width > 1f
                ? picker.iconImage.rectTransform.rect.width
                : 560f;
            float viewHeight = templateRect.rect.height > 1f ? templateRect.rect.height : 36f;
            viewportRect.sizeDelta = new Vector2(viewportWidth, viewHeight);
            RectMask2D mask = viewport.AddComponent<RectMask2D>();
            mask.enabled = true;

            GameObject content = new GameObject("PY_TabContent", typeof(RectTransform));
            RectTransform contentRect = (RectTransform)content.transform;
            contentRect.SetParent(viewportRect, false);
            contentRect.anchorMin = new Vector2(0f, 0.5f);
            contentRect.anchorMax = new Vector2(0f, 0.5f);
            contentRect.pivot = new Vector2(0f, 0.5f);
            contentRect.sizeDelta = new Vector2(0f, viewHeight - 4f);
            contentRect.anchoredPosition = Vector2.zero;

            u.TabViewport = viewport;
            u.TabViewportRect = viewportRect;
            u.TabContent = contentRect;
            viewport.SetActive(false);
        }

        private static void BuildFooter(PickerUi u, UIRecipePicker picker, RectTransform iconRect)
        {
            // 页码文本：iconImage 正下方
            GameObject labelObject = new GameObject("PY_PageLabel", typeof(RectTransform));
            RectTransform labelRect = (RectTransform)labelObject.transform;
            labelRect.SetParent(iconRect, false);
            labelRect.anchorMin = new Vector2(0f, 1f);
            labelRect.anchorMax = new Vector2(0f, 1f);
            labelRect.pivot = new Vector2(0f, 1f);
            labelRect.sizeDelta = new Vector2(420f, 20f);
            labelRect.anchoredPosition = new Vector2(0f, -(iconRect.rect.height + 8f));
            Text label = labelObject.AddComponent<Text>();
            label.font = u.Font;
            label.fontSize = u.FontSize > 0 ? u.FontSize : 12;
            label.alignment = TextAnchor.MiddleLeft;
            label.color = new Color(1f, 0.92f, 0.72f, 1f);
            label.raycastTarget = false;
            label.horizontalOverflow = HorizontalWrapMode.Overflow;
            label.verticalOverflow = VerticalWrapMode.Overflow;
            label.text = string.Empty;
            u.PageLabelObject = labelObject;
            u.PageLabel = label;

            // 右侧纵向滚动条
            GameObject track = new GameObject("PY_PageScroll", typeof(RectTransform));
            RectTransform trackRect = (RectTransform)track.transform;
            trackRect.SetParent(iconRect, false);
            trackRect.anchorMin = new Vector2(1f, 1f);
            trackRect.anchorMax = new Vector2(1f, 1f);
            trackRect.pivot = new Vector2(0f, 1f);
            trackRect.sizeDelta = new Vector2(10f, iconRect.rect.height);
            trackRect.anchoredPosition = new Vector2(8f, 0f);
            Image trackImage = track.AddComponent<Image>();
            trackImage.color = new Color(0.08f, 0.08f, 0.08f, 0.55f);
            trackImage.raycastTarget = true;

            GameObject thumb = new GameObject("thumb", typeof(RectTransform));
            RectTransform thumbRect = (RectTransform)thumb.transform;
            thumbRect.SetParent(trackRect, false);
            thumbRect.anchorMin = new Vector2(0f, 1f);
            thumbRect.anchorMax = new Vector2(1f, 1f);
            thumbRect.pivot = new Vector2(0.5f, 1f);
            thumbRect.sizeDelta = new Vector2(8f, 40f);
            thumbRect.anchoredPosition = Vector2.zero;
            Image thumbImage = thumb.AddComponent<Image>();
            thumbImage.color = new Color(0.85f, 0.8f, 0.6f, 0.9f);
            thumbImage.raycastTarget = true;

            AddScrollbarPointerHandlers(track, u, trackRect);
            AddScrollbarPointerHandlers(thumb, u, trackRect);

            u.TrackObject = track;
            u.TrackRect = trackRect;
            u.TrackImage = trackImage;
            u.ThumbObject = thumb;
            u.ThumbRect = thumbRect;
            u.ThumbImage = thumbImage;
            track.SetActive(false);
        }

        private static Font FindFont(UIRecipePicker picker, out int fontSize)
        {
            fontSize = 12;
            try
            {
                Text text = picker.GetComponentInChildren<Text>(true);
                if (text != null && text.font != null)
                {
                    fontSize = Mathf.Max(10, text.fontSize);
                    return text.font;
                }
                if (UIRoot.instance != null)
                {
                    text = UIRoot.instance.GetComponentInChildren<Text>(true);
                    if (text != null && text.font != null)
                    {
                        fontSize = Mathf.Max(10, text.fontSize);
                        return text.font;
                    }
                }
            }
            catch
            {
            }
            LogOnce("[选择器UI] 未找到可用字体，页签/页码文本可能不显示（功能不受影响）");
            return null;
        }

        // ------------------------------------------------------------------ 自测（仅环境变量触发，默认不生效）
        private static int selfTestPhase;
        private static float selfTestDeadline;

        internal static bool SelfTestRequested
        {
            get
            {
                try
                {
                    return Environment.GetEnvironmentVariable("DSPPYPORT_PICKER_SELFTEST") == "1";
                }
                catch
                {
                    return false;
                }
            }
        }

        internal static void SelfTestTick(ManualLogSource logger)
        {
            if (!SelfTestRequested || selfTestPhase >= 4)
            {
                return;
            }
            try
            {
                if (selfTestPhase == 0)
                {
                    if (!contentReady)
                    {
                        return;
                    }
                    if (selfTestDeadline <= 0f)
                    {
                        selfTestDeadline = Time.realtimeSinceStartup + 60f;
                    }
                    if (Time.realtimeSinceStartup > selfTestDeadline)
                    {
                        selfTestPhase = 4;
                        logger.LogWarning("[选择器UI][自测] 超时：未找到已初始化的 UIRecipePicker，跳过（不影响正式功能）");
                        return;
                    }
                    UIRecipePicker picker = UIRoot.instance != null && UIRoot.instance.uiGame != null
                        ? UIRoot.instance.uiGame.recipePicker
                        : null;
                    if (picker == null || !picker.inited || picker.active)
                    {
                        return;
                    }
                    int type = FindSelfTestType();
                    if (type <= 0)
                    {
                        selfTestPhase = 4;
                        logger.LogWarning("[选择器UI][自测] 找不到带 GridIndex>=1101 的 PY 配方类型，跳过");
                        return;
                    }
                    logger.LogInfo("[选择器UI][自测] 打开选择器：filter=" + type + "，GameMain.data=" +
                                   (GameMain.data != null) + "，GameMain.history=" + (GameMain.history != null) +
                                   "，iconSet=" + (GameMain.iconSet != null));
                    UIRecipePicker.Popup(new Vector2(0f, 0f), null, (ERecipeType)type);
                    selfTestPhase = 1;
                    selfTestDeadline = Time.realtimeSinceStartup + 10f;
                    return;
                }
                if (selfTestPhase == 1)
                {
                    if (Time.realtimeSinceStartup < selfTestDeadline)
                    {
                        return;
                    }
                    logger.LogInfo("[选择器UI][自测] 打开 10s 后快照：" + BuildSnapshot());
                    selfTestPhase = 2;
                    selfTestDeadline = Time.realtimeSinceStartup + 2f;
                    return;
                }
                if (selfTestPhase == 2)
                {
                    if (Time.realtimeSinceStartup < selfTestDeadline)
                    {
                        return;
                    }
                    logger.LogInfo("[选择器UI][自测] 关闭前最终快照：" + BuildSnapshot());
                    UIRecipePicker.Close();
                    selfTestPhase = 3;
                    selfTestDeadline = Time.realtimeSinceStartup + 2f;
                    return;
                }
                if (selfTestPhase == 3 && Time.realtimeSinceStartup >= selfTestDeadline)
                {
                    selfTestPhase = 4;
                    logger.LogInfo("[选择器UI][自测] 完成（已关闭选择器）");
                }
            }
            catch (Exception ex)
            {
                selfTestPhase = 4;
                logger.LogWarning("[选择器UI][自测] 异常（不影响正式功能）：" + ex);
            }
        }

        private static int FindSelfTestType()
        {
            RecipeProto[] dataArray = LDB.recipes != null ? LDB.recipes.dataArray : null;
            if (dataArray == null)
            {
                return 0;
            }
            for (int i = 0; i < dataArray.Length; i++)
            {
                RecipeProto proto = dataArray[i];
                if (proto != null && proto.GridIndex >= 1101 && (int)proto.Type >= 60 && (int)proto.Type <= 77)
                {
                    return (int)proto.Type;
                }
            }
            return 0;
        }

        internal static string BuildSnapshot()
        {
            PickerUi u = ui;
            if (u == null || !u.Built)
            {
                return "ui=null";
            }
            int sprites = 0;
            HashSet<int> distinctSprites = new HashSet<int>();
            string firstCells = string.Empty;
            RecipeProto[] protoArray = protoArrayField != null ? (RecipeProto[])protoArrayField.GetValue(u.Picker) : null;
            for (int i = 0; i < u.CellImages.Length; i++)
            {
                Sprite sprite = u.CellImages[i] != null ? u.CellImages[i].sprite : null;
                if (sprite != null)
                {
                    sprites++;
                    distinctSprites.Add(sprite.GetInstanceID());
                }
                if (i < 10)
                {
                    RecipeProto proto = protoArray != null && i < protoArray.Length ? protoArray[i] : null;
                    if (firstCells.Length > 0)
                    {
                        firstCells += " | ";
                    }
                    firstCells += "cell" + i + ":recipe=" + (proto != null ? proto.ID.ToString() : "null") +
                                  ",sprite=" + (sprite != null ? sprite.name + "#" + sprite.GetInstanceID() : "null");
                }
            }
            string label = u.PageLabel != null ? u.PageLabel.text : "(null)";
            string tabViewport = u.TabViewportRect != null
                ? "pos=" + u.TabViewportRect.anchoredPosition + ",size=" + u.TabViewportRect.sizeDelta
                : "null";
            string track = u.TrackRect != null
                ? "pos=" + u.TrackRect.anchoredPosition + ",size=" + u.TrackRect.sizeDelta
                : "null";
            string iconRect = u.IconRect != null
                ? "pos=" + u.IconRect.anchoredPosition + ",size=" + u.IconRect.sizeDelta
                : "null";
            return "built=true filter=" + (int)u.ActiveFilter + " groups=" + u.Groups.Count + " groupIdx=" +
                   u.GroupIndex + " page=" + u.Page + "/" + u.TotalPages + " recipes=" + u.TotalRecipes +
                   " tabButtons=" + u.TabButtons.Count + " sprites=" + sprites + "/112 distinctSpriteIds=" +
                   distinctSprites.Count + " label=\"" + label + "\" tabViewport(" + tabViewport + ") track(" + track +
                   ") iconRect(" + iconRect + ") firstCells[" + firstCells + "]";
        }

        // ------------------------------------------------------------------ 日志
        private static readonly HashSet<string> loggedMessages = new HashSet<string>(StringComparer.Ordinal);

        private static void LogOnce(string message)
        {
            if (!loggedMessages.Add(message))
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
