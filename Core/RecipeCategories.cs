using System;
using System.Collections.Generic;

namespace DSPPyPort.Core
{
    /// <summary>
    /// 配方大类注册表（队列 3 / task-18，2026-11-19）。
    ///
    /// content/recipes.json 每条配方带 PY 原始 `category` 字段（crafting / compost / chemistry / ...，
    /// 171 种）。该字段不注册进 DSP（RecipeProto 没有对应成员），只由引擎读入内存，
    /// 供配方选择器 UI 做「按大类分组」的页签；不修改任何 content JSON。
    /// </summary>
    internal static class RecipeCategories
    {
        private static readonly Dictionary<int, string> byRecipeId = new Dictionary<int, string>();

        internal static int Count
        {
            get { return byRecipeId.Count; }
        }

        internal static void Set(int recipeId, string category)
        {
            if (recipeId <= 0)
            {
                return;
            }
            if (string.IsNullOrEmpty(category))
            {
                byRecipeId.Remove(recipeId);
                return;
            }
            byRecipeId[recipeId] = category;
        }

        internal static string Get(int recipeId)
        {
            string category;
            return byRecipeId.TryGetValue(recipeId, out category) ? category : null;
        }

        internal static void Clear()
        {
            byRecipeId.Clear();
        }
    }
}
