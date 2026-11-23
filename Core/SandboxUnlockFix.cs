using System;
using System.Collections.Generic;
using BepInEx.Logging;

namespace DSPPyPort.Core
{
    /// <summary>
    /// 沙盒模式自动解锁 PY 内容（task-15，2026-11-18）。
    ///
    /// 问题：`GameHistoryData.SetForNewGame()` 只解锁 `Configs.freeMode.recipes`（原版初始配方），
    /// 沙盒模式仅设置 `GameDesc.isSandboxMode` 标志、**不会**解除科技/配方锁定；
    /// 所以新沙盒里我们的 5738 条配方全部 `RecipeUnlocked=false`，配方选择器里看不到。
    ///
    /// 修复：检测 `GameMain.data.gameDesc.isSandboxMode`，游戏载入完成后（GameMain.data/history 可用）
    /// 对我们的 recipes 调 `GameMain.history.UnlockRecipe(id)`、对我们的 techs 调 `UnlockTech(id)`；
    /// 只解锁本 MOD 注册的 ID（注册时收集），普通模式完全不动（保持科技解锁玩法）。
    /// 配置开关：`[Compat] SandboxUnlockPyContent`（默认 true）。
    /// </summary>
    internal static class SandboxUnlockFix
    {
        private static bool done;
        private static bool reportedNonSandbox;

        internal static void Tick(Registrar registrar, ManualLogSource log, bool enabled)
        {
            if (!enabled || done || registrar == null)
            {
                return;
            }
            try
            {
                if (GameMain.data == null || GameMain.data.gameDesc == null || GameMain.history == null)
                {
                    return; // 还在主菜单/加载中
                }
                if (!GameMain.data.gameDesc.isSandboxMode)
                {
                    done = true;
                    if (!reportedNonSandbox)
                    {
                        reportedNonSandbox = true;
                        log.LogInfo("[Compat] 非沙盒模式：不自动解锁 PY 配方/科技（保持正常科技解锁玩法；" +
                                    "需要自动解锁可开 [Compat] SandboxUnlockPyContent 但仅沙盒生效）");
                    }
                    return;
                }

                List<int> recipeIds = registrar.GetRegisteredRecipeIds();
                List<int> techIds = registrar.GetRegisteredTechIds();
                int recipes = 0;
                int techs = 0;
                for (int i = 0; i < recipeIds.Count; i++)
                {
                    try
                    {
                        GameMain.history.UnlockRecipe(recipeIds[i]);
                        recipes++;
                    }
                    catch (Exception ex)
                    {
                        if (i < 3)
                        {
                            log.LogWarning("[Compat] 沙盒解锁配方 " + recipeIds[i] + " 失败：" + ex.Message);
                        }
                    }
                }
                for (int i = 0; i < techIds.Count; i++)
                {
                    try
                    {
                        GameMain.history.UnlockTech(techIds[i]);
                        techs++;
                    }
                    catch (Exception ex)
                    {
                        if (i < 3)
                        {
                            log.LogWarning("[Compat] 沙盒解锁科技 " + techIds[i] + " 失败：" + ex.Message);
                        }
                    }
                }
                done = true;
                log.LogInfo(string.Format(
                    "[Compat] 沙盒模式：已自动解锁 PY 内容 —— 配方 {0}/{1} 条、科技 {2}/{3} 个" +
                    "（生效后可在建筑配方选择器里用按钮 1/2 或鼠标滚轮翻页查看）",
                    recipes, recipeIds.Count, techs, techIds.Count));
            }
            catch (Exception ex)
            {
                done = true;
                log.LogError("[Compat] 沙盒自动解锁失败（已跳过）：" + ex);
            }
        }
    }
}
