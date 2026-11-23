using System;
using System.Collections.Generic;
using UnityEngine;

namespace DSPPyPort.Core
{
    /// <summary>
    /// 科技替换模式（task-17 引擎侧）。
    ///
    /// 背景：DSP 把 <c>TechProto.page</c> 硬编码为 <c>ID &gt;= 2000 ? 1 : 0</c>，主页（page 0）只渲染
    /// ID &lt; 2000 的科技。PY 内容包把 733 条科技重排到 1..1999，其中 103 条「占用」原版主页科技 ID
    /// （1508/1509/1901-1904 除外），因此注册时必须允许与原版 TechProto 冲突，并在原位覆盖字段。
    ///
    /// 流程：
    ///  1) Awake：冲突扫描放行 1..1999 的占位 ID；RegisterTechs 对占位 ID 走 <see cref="ReplaceVanillaTech"/>
    ///     （不新建 proto，直接改写 LDB.techs 里已有的 TechProto）；
    ///  2) Awake 末尾：<see cref="HideUnreplacedVanillaMainTechs"/> 把仍未替换的原版主页科技标记为 obsolete（隐藏）；
    ///  3) onLoadingFinished：<see cref="RebuildReplacedTechs"/> 对替换过的科技补跑 Preload + Preload2
    ///     （CommonAPI 的 OnPostAdd 只处理「新增」科技，不会重建被覆盖的旧科技）；
    ///  4) 同一步里 <see cref="RefreshRecipePreTechs"/> 重算配方 preTech（原版配方已按槽位继承到 PY 科技）。
    /// </summary>
    internal sealed partial class Registrar
    {
        internal const int VanillaMainTechIdMax = 1999;

        /// <summary>切换开关，Plugin 在 Awake 里按 [Compat] TechReplaceMode 赋值。</summary>
        internal static bool TechReplaceMode = true;

        /// <summary>不参与替换的原版主页科技：1508 是硬编码开局/目标科技，其余为原版隐藏科技。</summary>
        internal static readonly int[] KeepVanillaMainTechIds = { 1508, 1509, 1901, 1902, 1903, 1904 };

        private static readonly int[] EmptyIntArray = new int[0];
        private static readonly double[] EmptyDoubleArray = new double[0];

        private readonly List<RegisteredTech> replacedTechs = new List<RegisteredTech>();
        private readonly List<int> hiddenVanillaTechIds = new List<int>();
        private int newTechCount;
        private bool replacedTechsRebuilt;

        /// <summary>该科技 ID 是否是「应被 PY 科技替换的原版主页槽位」。</summary>
        private bool IsTechSlotToReplace(int id)
        {
            return TechReplaceMode && id > 0 && id <= VanillaMainTechIdMax && preOccupiedTechs.Contains(id);
        }

        /// <summary>冲突扫描用：剔除「由本 MOD 主动占用的原版主页科技槽位」，其余 ID 仍按冲突拒绝。</summary>
        private List<int> FilterTechSlotsToReplace(List<int> planned)
        {
            List<int> result = new List<int>(planned.Count);
            for (int i = 0; i < planned.Count; i++)
            {
                if (!IsTechSlotToReplace(planned[i]))
                {
                    result.Add(planned[i]);
                }
            }
            if (TechReplaceMode)
            {
                int replaceCount = planned.Count - result.Count;
                if (replaceCount > 0)
                {
                    log.LogInfo("[冲突检查] 科技替换模式：计划占用原版主页槽位 " + replaceCount + " 条（冲突检查已放行）");
                }
            }
            return result;
        }

        // ------------------------------------------------------------------ 注册期：就地替换
        private bool ReplaceVanillaTech(TechDef def)
        {
            TechProto proto = null;
            try
            {
                if (LDB.techs != null)
                {
                    proto = LDB.techs.Select(def.Id);
                }
            }
            catch (Exception ex)
            {
                log.LogWarning("[科技替换] 读取原版科技 " + def.Id + " 失败：" + ex.Message);
            }
            if (proto == null)
            {
                log.LogError("[科技替换] 计划替换科技 " + def.Id + " 但 LDB.techs 中不存在该 proto，已跳过");
                skippedInvalid++;
                return false;
            }

            string key = def.EffectiveKey;
            string oldName = string.IsNullOrEmpty(proto.Name) ? proto.name : proto.Name;

            // ---- 基础文本（Preload 稍后翻译）
            proto.Name = key + "_Name";
            proto.Desc = key + "_Desc";
            proto.Conclusion = key + "_Conclusion";
            proto.IconPath = ResolveIconPath(def.IconFile, def.IconSource, false, "科技 " + def.Id);
            proto.IconTag = string.Empty;

            // ---- 解锁前置 / 矩阵消耗
            proto.PreTechs = def.PreTechs ?? EmptyIntArray;
            proto.PreTechsImplicit = EmptyIntArray;
            proto.PreTechsMax = false;
            proto.Items = def.Matrices ?? EmptyIntArray;
            proto.ItemPoints = NormalizePoints(proto.Items, def.MatrixPoints);
            proto.HashNeeded = def.HashNeeded > 0 ? def.HashNeeded : 1;
            proto.UnlockRecipes = def.UnlockRecipes ?? EmptyIntArray;

            // ---- 位置（主页布局由生成器算好的像素坐标）
            if (def.HasPosition)
            {
                proto.Position = new Vector2(def.PosX, def.PosY);
            }

            // ---- 非升级科技：清掉原版升级科技残留（Level/MaxLevel、功能解锁、覆盖属性、附加物品）
            proto.Level = 0;
            proto.MaxLevel = 1;
            proto.IsLabTech = true;
            proto.Published = true;
            proto.IsObsolete = false;
            proto.IsHiddenTech = def.Hidden;
            proto.PreItem = EmptyIntArray;
            proto.UnlockFunctions = EmptyIntArray;
            proto.UnlockValues = EmptyDoubleArray;
            proto.PropertyOverrideItems = EmptyIntArray;
            proto.PropertyItemCounts = EmptyIntArray;
            proto.AddItems = EmptyIntArray;
            proto.AddItemCounts = EmptyIntArray;

            // ---- 图标 / 本地化
            IconLoader.RegisterTech(def.Id, def.IconFile, def.IconSource);
            AddTechTranslations(def);

            replacedTechs.Add(new RegisteredTech { Def = def, Proto = proto, ExpectedId = def.Id });
            log.LogInfo("[科技替换] 原版主页槽位 " + def.Id + "（" + oldName + "）→ PY 科技「" + def.NameZh + "」");
            return true;
        }

        private static int[] NormalizePoints(int[] items, int[] points)
        {
            int len = items != null ? items.Length : 0;
            if (points != null && points.Length == len)
            {
                return points;
            }
            int[] result = new int[len];
            if (points != null)
            {
                Array.Copy(points, result, Math.Min(len, points.Length));
            }
            for (int i = 0; i < result.Length; i++)
            {
                if (result[i] <= 0)
                {
                    result[i] = 1;
                }
            }
            return result;
        }

        /// <summary>注册期结束时调用：未被 PY 科技覆盖的原版主页科技标记 obsoolete（科技树主页不显示）。</summary>
        private void HideUnreplacedVanillaMainTechs()
        {
            if (!TechReplaceMode || LDB.techs == null)
            {
                return;
            }
            hiddenVanillaTechIds.Clear();
            HashSet<int> ours = usedTechIds;
            for (int i = 0; i < LDB.techs.dataArray.Length; i++)
            {
                TechProto proto = LDB.techs.dataArray[i];
                if (proto == null || proto.ID <= 0 || proto.ID > VanillaMainTechIdMax)
                {
                    continue;
                }
                if (ours.Contains(proto.ID) || IsKeptVanillaMainTech(proto.ID))
                {
                    continue;
                }
                // 只处理注册前就存在的 proto（本 MOD 新注册的科技不可能走到这里）
                if (!preOccupiedTechs.Contains(proto.ID))
                {
                    continue;
                }
                proto.IsObsolete = true;
                proto.Published = false;
                proto.UnlockRecipes = EmptyIntArray;
                proto.PreTechs = EmptyIntArray;
                proto.PreTechsImplicit = EmptyIntArray;
                hiddenVanillaTechIds.Add(proto.ID);
            }
            if (hiddenVanillaTechIds.Count == 0)
            {
                log.LogInfo("[科技替换] 原版主页科技已全部被 PY 科技覆盖或保留，无需隐藏残留");
            }
            else
            {
                log.LogWarning("[科技替换] 隐藏未覆盖的原版主页科技 " + hiddenVanillaTechIds.Count + " 条：" +
                               DescribeSmallList(hiddenVanillaTechIds));
            }
        }

        private static bool IsKeptVanillaMainTech(int id)
        {
            for (int i = 0; i < KeepVanillaMainTechIds.Length; i++)
            {
                if (KeepVanillaMainTechIds[i] == id)
                {
                    return true;
                }
            }
            return false;
        }

        private static string DescribeSmallList(List<int> list)
        {
            string text = string.Empty;
            int max = Math.Min(list.Count, 40);
            for (int i = 0; i < max; i++)
            {
                if (i > 0)
                {
                    text += ",";
                }
                text += list[i];
            }
            if (list.Count > max)
            {
                text += ",...";
            }
            return text;
        }

        // ------------------------------------------------------------------ 收尾期：重建被覆盖的 proto
        /// <summary>
        /// 对替换过的科技补跑 Preload（翻译/图标）与 Preload2（preTechArray / postTechArray /
        /// unlockRecipeArray / itemArray / unlockNeedItemArray）。必须在配方解锁清单补全之后调用。
        /// </summary>
        internal void RebuildReplacedTechs()
        {
            if (!TechReplaceMode)
            {
                return;
            }
            if (replacedTechs.Count == 0)
            {
                log.LogWarning("[科技替换] 替换模式开启，但没有任何科技走替换分支（内容未启用？）");
                return;
            }
            if (replacedTechsRebuilt)
            {
                return;
            }
            replacedTechsRebuilt = true;
            int ok = 0;
            int failed = 0;
            for (int i = 0; i < replacedTechs.Count; i++)
            {
                RegisteredTech entry = replacedTechs[i];
                try
                {
                    // LDB 合并（LDBTool/CommonAPI 重新分配数组）后重新取一次，避免旧引用失效。
                    TechProto proto = LDB.techs != null ? LDB.techs.Select(entry.ExpectedId) : null;
                    if (proto == null)
                    {
                        proto = entry.Proto;
                    }
                    if (proto == null)
                    {
                        failed++;
                        continue;
                    }
                    entry.Proto = proto;
                    if (proto.Items.Length != proto.ItemPoints.Length)
                    {
                        proto.ItemPoints = NormalizePoints(proto.Items, proto.ItemPoints);
                    }
                    proto.Preload();
                    proto.Preload2();
                    ok++;
                }
                catch (Exception ex)
                {
                    failed++;
                    log.LogError("[科技替换] 重建科技 " + entry.ExpectedId + " 失败：" + ex.Message);
                }
            }
            int recipes = RefreshRecipePreTechs();
            log.LogInfo(string.Format(
                "[科技替换] 重建完成：替换科技 {0}/{1}（失败 {2}）/ 新增科技 {3} / 隐藏原版主页科技 {4} / 配方 preTech 重算 {5} 条",
                ok, replacedTechs.Count, failed, newTechCount, hiddenVanillaTechIds.Count, recipes));
            try
            {
                VerifyTechTree();
            }
            catch (Exception ex)
            {
                log.LogWarning("[科技树自检] 运行失败（不影响功能）：" + ex.Message);
            }
        }

        /// <summary>
        /// 运行时科技树自检：不依赖肉眼即可确认主页节点数量、前置/解锁数组、名称翻译与坐标是否就绪。
        /// TechProto.page 是硬编码属性（ID &gt;= 2000 → 1），所以「主页节点数」直接等于 page==0 的科技数。
        /// </summary>
        private void VerifyTechTree()
        {
            if (LDB.techs == null)
            {
                return;
            }
            HashSet<int> ours = new HashSet<int>();
            for (int i = 0; i < techs.Count; i++)
            {
                ours.Add(techs[i].ExpectedId);
            }
            for (int i = 0; i < replacedTechs.Count; i++)
            {
                ours.Add(replacedTechs[i].ExpectedId);
            }

            int page0 = 0;
            int page0Ours = 0;
            int obsolete = 0;
            int nameUntranslated = 0;
            int preArrayMismatch = 0;
            int unlockMismatch = 0;
            int itemArrayMismatch = 0;
            int emptyUnlockNeed = 0;
            int noPreTech = 0;
            int badPosition = 0;
            Dictionary<string, int> nameCounts = new Dictionary<string, int>();
            TechProto[] array = LDB.techs.dataArray;
            for (int i = 0; i < array.Length; i++)
            {
                TechProto proto = array[i];
                if (proto == null || proto.ID <= 0)
                {
                    continue;
                }
                if (proto.IsObsolete)
                {
                    obsolete++;
                    continue;
                }
                bool isOurs = ours.Contains(proto.ID);
                if (proto.page == 0)
                {
                    page0++;
                    if (isOurs)
                    {
                        page0Ours++;
                    }
                    string name = proto.name;
                    if (!string.IsNullOrEmpty(name))
                    {
                        int count;
                        nameCounts.TryGetValue(name, out count);
                        nameCounts[name] = count + 1;
                    }
                }
                if (!isOurs)
                {
                    continue;
                }
                // ---- 名称翻译（Preload 后 name 应为译文，而不是 PY_..._Name 键）
                if (string.IsNullOrEmpty(proto.name) || proto.name == proto.Name)
                {
                    nameUntranslated++;
                }
                // ---- Preload2 生成的数组
                if (proto.preTechArray == null || proto.preTechArray.Length != CountResolvedTechs(proto.PreTechs))
                {
                    preArrayMismatch++;
                }
                if (proto.unlockRecipeArray == null
                    || proto.unlockRecipeArray.Length != CountResolvedRecipes(proto.UnlockRecipes))
                {
                    unlockMismatch++;
                }
                if (proto.itemArray == null || proto.itemArray.Length != proto.Items.Length)
                {
                    itemArrayMismatch++;
                }
                if (proto.unlockNeedItemArray == null || proto.unlockNeedItemArray.Length == 0)
                {
                    emptyUnlockNeed++;
                }
                if (proto.PreTechs.Length == 0 && proto.ID != 1 && !IsRootTech(proto))
                {
                    noPreTech++;
                }
                if (float.IsNaN(proto.Position.x) || float.IsNaN(proto.Position.y)
                    || Math.Abs(proto.Position.x) > 20000f || Math.Abs(proto.Position.y) > 20000f)
                {
                    badPosition++;
                }
            }

            int duplicatedNames = 0;
            foreach (KeyValuePair<string, int> pair in nameCounts)
            {
                if (pair.Value > 1)
                {
                    duplicatedNames += pair.Value - 1;
                }
            }

            log.LogInfo(string.Format(
                "[科技树自检] page0（主页）科技 {0} 条 = PY {1} + 其它 MOD/保留原版 {2}；失效/隐藏 {3}；" +
                "名称未翻译 {4}；preTechArray 不一致 {5}；unlockRecipeArray 不一致 {6}；itemArray 不一致 {7}；" +
                "解锁需求为空 {8}；无前置 {9}；坐标异常 {10}；主页重名 {11}",
                page0, page0Ours, Math.Max(0, page0 - page0Ours), obsolete,
                nameUntranslated, preArrayMismatch, unlockMismatch, itemArrayMismatch,
                emptyUnlockNeed, noPreTech, badPosition, duplicatedNames));
            if (page0Ours != techs.Count + replacedTechs.Count)
            {
                log.LogError(string.Format(
                    "[科技树自检] PY 主页科技 {0} 条与已注册总数 {1} 不一致（应有科技未落在 page0）",
                    page0Ours, techs.Count + replacedTechs.Count));
            }
            if (nameUntranslated + preArrayMismatch + unlockMismatch + itemArrayMismatch + badPosition > 0)
            {
                log.LogWarning("[科技树自检] 存在未就绪字段，详见上一条统计（懒加载图标不计入）");
            }
        }

        private static int CountResolvedTechs(int[] ids)
        {
            if (ids == null)
            {
                return 0;
            }
            int count = 0;
            for (int i = 0; i < ids.Length; i++)
            {
                if (ids[i] > 0 && LDB.techs.Select(ids[i]) != null)
                {
                    count++;
                }
            }
            return count;
        }

        private static int CountResolvedRecipes(int[] ids)
        {
            if (ids == null)
            {
                return 0;
            }
            int count = 0;
            for (int i = 0; i < ids.Length; i++)
            {
                if (ids[i] > 0 && LDB.recipes.Select(ids[i]) != null)
                {
                    count++;
                }
            }
            return count;
        }

        private static bool IsRootTech(TechProto proto)
        {
            // 开局科技（不要求前置）：PY 的 era0 根节点；原版 1 号为戴森球计划（已被替换时按 PY 内容）
            return proto.Position.y > -50f && proto.Position.x < 200f;
        }

        /// <summary>科技解锁清单变化后，重算配方 preTech（RecipeProto.FindPreTech 按 UnlockRecipes 反查）。</summary>
        private int RefreshRecipePreTechs()
        {
            if (LDB.recipes == null)
            {
                return 0;
            }
            int ok = 0;
            RecipeProto[] array = LDB.recipes.dataArray;
            for (int i = 0; i < array.Length; i++)
            {
                RecipeProto recipe = array[i];
                if (recipe == null || recipe.ID <= 0)
                {
                    continue;
                }
                try
                {
                    recipe.FindPreTech();
                    ok++;
                }
                catch (Exception ex)
                {
                    log.LogWarning("[科技替换] 配方 " + recipe.ID + " preTech 重算失败：" + ex.Message);
                }
            }
            return ok;
        }

        internal void LogTechReplacementSummary()
        {
            if (!TechReplaceMode)
            {
                log.LogInfo("[科技替换] 模式关闭：科技按普通新 proto 注册（ID 冲突会被拒绝）");
                return;
            }
            log.LogInfo(string.Format(
                "[科技替换] 模式开启：原版主页槽位替换 {0} 条 / 新增科技 {1} 条 / 保留原版主页科技 {2} 条 / 隐藏残留 {3} 条",
                replacedTechs.Count, newTechCount, KeepVanillaMainTechIds.Length, hiddenVanillaTechIds.Count));
        }
    }
}
