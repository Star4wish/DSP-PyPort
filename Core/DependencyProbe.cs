using System;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Logging;

namespace DSPPyPort.Core
{
    /// <summary>
    /// 前置 MOD 探测。CommonAPI / LDBTool 是硬依赖（BepInEx 会拦截缺失），
    /// DSP-RecipeSlots 是软依赖：缺失时 MOD 仍可加载，但 >6 原料 / >2 产物的配方会被
    /// 引擎静默截断，因此这里必须打明确的 Error + 修复指引（手册 §3.3 / 附录 B2-1）。
    /// </summary>
    internal static class DependencyProbe
    {
        internal const string CommonApiGuid = "dsp.common-api.CommonAPI";
        internal const string LdbToolGuid = "me.xiaoye97.plugin.Dyson.LDBTool";
        internal const string RecipeSlotsGuid = "star4wish.dsp.recipeslots";
        internal const string RecipeSlotsMinVersion = "1.2.0";

        internal static bool RecipeSlotsAvailable { get; private set; }
        internal static string RecipeSlotsVersion { get; private set; } = string.Empty;
        internal static string CommonApiVersion { get; private set; } = string.Empty;
        internal static string LdbToolVersion { get; private set; } = string.Empty;

        internal static void Report(ManualLogSource log)
        {
            RecipeSlotsAvailable = false;
            RecipeSlotsVersion = string.Empty;

            CommonApiVersion = ProbeVersion(CommonApiGuid);
            if (string.IsNullOrEmpty(CommonApiVersion))
            {
                log.LogError("[前置] 未找到 CommonAPI（GUID=" + CommonApiGuid +
                             "）。本 MOD 依赖其 ProtoRegistry 注册数据，请安装 CommonAPI 1.6.5+。");
            }
            else
            {
                log.LogInfo("[前置] CommonAPI 已就绪：v" + CommonApiVersion);
            }

            LdbToolVersion = ProbeVersion(LdbToolGuid);
            if (string.IsNullOrEmpty(LdbToolVersion))
            {
                log.LogError("[前置] 未找到 LDBTool（GUID=" + LdbToolGuid +
                             "）。本 MOD 依赖其把新增 Proto 并入 LDB，请安装 LDBTool 3.0.3+。");
            }
            else
            {
                log.LogInfo("[前置] LDBTool 已就绪：v" + LdbToolVersion);
            }

            string slotsVersion = ProbeVersion(RecipeSlotsGuid);
            if (string.IsNullOrEmpty(slotsVersion))
            {
                log.LogError("[前置] 未找到 DSP-RecipeSlots（GUID=" + RecipeSlotsGuid + "）。" +
                             "内容里的 >6 原料 / >2 产物配方会被原生引擎静默截断（机器只吃前 6 项、多余产物丢失）。" +
                             "请安装 DSP_RecipeSlots " + RecipeSlotsMinVersion + "+ 到 BepInEx\\plugins\\DSP-RecipeSlots\\，然后重启游戏。");
                return;
            }

            RecipeSlotsAvailable = true;
            RecipeSlotsVersion = slotsVersion;
            if (CompareVersion(slotsVersion, RecipeSlotsMinVersion) >= 0)
            {
                log.LogInfo("[前置] DSP-RecipeSlots 已就绪：v" + slotsVersion + "（宽配方 >6 原料 / >2 产物可用）");
            }
            else
            {
                log.LogWarning("[前置] DSP-RecipeSlots 版本 v" + slotsVersion + " 低于建议的 v" + RecipeSlotsMinVersion +
                               "：1.1.x 用 6 格轮转，宽配方有占空比损失。功能仍可用，建议升级。");
            }
        }

        private static string ProbeVersion(string guid)
        {
            try
            {
                if (Chainloader.PluginInfos == null)
                {
                    return string.Empty;
                }
                PluginInfo info;
                if (!Chainloader.PluginInfos.TryGetValue(guid, out info) || info == null)
                {
                    return string.Empty;
                }
                if (info.Metadata != null && info.Metadata.Version != null)
                {
                    return info.Metadata.Version.ToString();
                }
                return "unknown";
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>按「主.次.修订」逐段比数字，忽略后缀（如 1.2.0-beta）。</summary>
        internal static int CompareVersion(string a, string b)
        {
            int[] va = ParseVersion(a);
            int[] vb = ParseVersion(b);
            for (int i = 0; i < 3; i++)
            {
                if (va[i] != vb[i])
                {
                    return va[i] < vb[i] ? -1 : 1;
                }
            }
            return 0;
        }

        private static int[] ParseVersion(string version)
        {
            int[] parts = new int[3];
            if (string.IsNullOrEmpty(version))
            {
                return parts;
            }
            string cleaned = version.Trim();
            int cut = cleaned.IndexOfAny(new char[] { '-', '+', ' ' });
            if (cut >= 0)
            {
                cleaned = cleaned.Substring(0, cut);
            }
            string[] fields = cleaned.Split('.');
            for (int i = 0; i < 3 && i < fields.Length; i++)
            {
                int value;
                if (int.TryParse(fields[i], out value) && value >= 0)
                {
                    parts[i] = value;
                }
            }
            return parts;
        }
    }
}
