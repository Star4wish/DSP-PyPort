using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;

namespace DSPPyPort.Core
{
    /// <summary>
    /// 跳过 LDBTool 对我们 proto 的 ID/GridIndex 配置绑定（启动性能修复，task-13，2026-11-18）。
    ///
    /// 根因：LDBTool 3.0.3 的 `IdBind` → `CustomID.Bind&lt;int&gt;(...)`、`GridIndexBind` →
    /// `CustomGridIndex.Bind&lt;int&gt;(...)`，两个 ConfigFile 都是 `saveOnConfigSet: true`，
    /// **每 Bind 一个新 key 就整文件 Save 一次**。八包合集有约 9663 个 proto（3175 物品 + 5738 配方 +
    /// 733 科技 + 18 模型），启动时约 9663 次整文件写（两份 cfg 共 ~1.8MB）≈ 7GB I/O → 启动 300+ 秒。
    ///
    /// 处理：Prefix 里识别属于本 MOD 的 proto（名称以 `PY_` 开头，或注册前登记的自有 ModelProto ID），
    /// 直接 return false 跳过绑定——我们的 ID/GridIndex 完全由 content JSON 固定并自检，不依赖 LDBTool 缓存。
    /// 其它 MOD 的 proto 完全不受影响（仍走原逻辑）。
    ///
    /// 收益：清空我们的 LDBTool 缓存后，启动时不再产生这 9663 次写；冷/热启动耗时接近。
    /// </summary>
    internal static class LdbToolBindSkipFix
    {
        internal static bool Installed { get; private set; }
        internal static long SkippedIdBinds;
        internal static long SkippedGridBinds;
        internal static long AllowedIdBinds;
        internal static long AllowedGridBinds;

        private static readonly HashSet<int> ownedModelIds = new HashSet<int>();

        /// <summary>模型在注册前登记 ID（RegisterModel 内部会立刻调 LDBTool.PreAddProto→IdBind）。</summary>
        internal static void MarkOwnedModelId(int modelId)
        {
            ownedModelIds.Add(modelId);
        }

        internal static void Install(Harmony harmony, ManualLogSource log)
        {
            try
            {
                MethodInfo idBind = AccessTools.Method(typeof(xiaoye97.LDBTool), "IdBind", new Type[] { typeof(Proto) });
                MethodInfo gridBind = AccessTools.Method(typeof(xiaoye97.LDBTool), "GridIndexBind", new Type[] { typeof(Proto) });
                if (idBind == null || gridBind == null)
                {
                    log.LogError("[兼容] 找不到 LDBTool.IdBind/GridIndexBind（版本不匹配？），启动性能优化未生效");
                    return;
                }
                harmony.Patch(idBind, prefix: new HarmonyMethod(typeof(LdbToolBindSkipFix)
                    .GetMethod("IdBindPrefix", BindingFlags.Static | BindingFlags.NonPublic)));
                harmony.Patch(gridBind, prefix: new HarmonyMethod(typeof(LdbToolBindSkipFix)
                    .GetMethod("GridIndexBindPrefix", BindingFlags.Static | BindingFlags.NonPublic)));
                Installed = true;
                log.LogInfo("[兼容] 已跳过 LDBTool 对本 MOD proto 的 ID/GridIndex 配置绑定" +
                            "（避免每个新 proto 整文件 Save；我们的 ID 由 content JSON 固定）");
            }
            catch (Exception ex)
            {
                log.LogError("[兼容] LDBTool 绑定跳过补丁安装失败（启动会很慢）：" + ex);
            }
        }

        private static bool IdBindPrefix(Proto proto)
        {
            if (IsOwned(proto))
            {
                SkippedIdBinds++;
                return false;
            }
            AllowedIdBinds++;
            return true;
        }

        private static bool GridIndexBindPrefix(Proto proto)
        {
            if (IsOwned(proto))
            {
                SkippedGridBinds++;
                return false;
            }
            AllowedGridBinds++;
            return true;
        }

        private static bool IsOwned(Proto proto)
        {
            if (proto == null)
            {
                return false;
            }
            try
            {
                string name = proto.Name;
                if (!string.IsNullOrEmpty(name) && name.StartsWith("PY_", StringComparison.Ordinal))
                {
                    return true;
                }
                ModelProto model = proto as ModelProto;
                if (model != null && ownedModelIds.Contains(model.ID))
                {
                    return true;
                }
            }
            catch
            {
            }
            return false;
        }

        internal static void LogSummary(ManualLogSource log)
        {
            if (!Installed)
            {
                return;
            }
            log.LogInfo(string.Format(
                "[Perf] LDBTool 绑定：跳过 本MOD IdBind {0} / GridIndexBind {1}；放行 其它MOD IdBind {2} / GridIndexBind {3}" +
                "（只跳过 Name 以 PY_ 开头或注册前登记的自有 Model ID，其它 MOD 一律原样绑定）",
                SkippedIdBinds, SkippedGridBinds, AllowedIdBinds, AllowedGridBinds));
        }
    }
}
