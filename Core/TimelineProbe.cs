using System;
using System.Diagnostics;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;

namespace DSPPyPort.Core
{
    /// <summary>
    /// 启动耗时时间线探针（仅在环境变量 DSPPYPORT_TIMELINE=1 时安装；正式用户无影响）。
    ///
    /// 现象：修好跨 MOD 配方 ID 冲突后，Awake→onLoadingFinished 从约 46s 涨到 300-500s，
    /// 但单看一个总耗时无法定位到底卡在哪。本探针在关键静态方法上打 Prefix/Postfix，
    /// 记录「进入/退出 + 距上一条记录的间隔」，一次启动即可还原耗时结构：
    ///   VFPreload.InvokeOnLoadWorkEnded（LDBTool 合并 + CommonAPI Preload 的总入口）、
    ///   LDBTool.AddProtos、CommonAPI.Systems.ProtoRegistry.OnPostAdd、
    ///   IconSet.Create、ItemProto.Init* / RecipeProto.InitRecipeItems、UXAssist 生产目录重建。
    /// </summary>
    internal static class TimelineProbe
    {
        internal static bool Installed { get; private set; }
        internal static bool Requested { get; private set; }

        private static readonly Stopwatch Clock = new Stopwatch();
        private static ManualLogSource log;
        private static long lastMs;

        internal static void Install(Harmony harmony, ManualLogSource logger)
        {
            log = logger;
            Requested = Environment.GetEnvironmentVariable("DSPPYPORT_TIMELINE") == "1";
            if (!Requested)
            {
                return;
            }
            Clock.Start();
            MethodInfo prefix = typeof(TimelineProbe).GetMethod("Prefix",
                BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo postfix = typeof(TimelineProbe).GetMethod("Postfix",
                BindingFlags.Static | BindingFlags.NonPublic);
            int ok = 0;
            ok += TryPatch(harmony, prefix, postfix, AccessTools.TypeByName("VFPreload"), "InvokeOnLoadWorkEnded");
            ok += TryPatch(harmony, prefix, postfix, typeof(xiaoye97.LDBTool), "AddProtos");
            ok += TryPatch(harmony, prefix, postfix, AccessTools.TypeByName("CommonAPI.Systems.ProtoRegistry"), "OnPostAdd");
            ok += TryPatch(harmony, prefix, postfix, typeof(IconSet), "Create");
            ok += TryPatch(harmony, prefix, postfix, typeof(ItemProto), "InitFuelNeeds");
            ok += TryPatch(harmony, prefix, postfix, typeof(ItemProto), "InitConstructableItems");
            ok += TryPatch(harmony, prefix, postfix, typeof(ItemProto), "InitItemIds");
            ok += TryPatch(harmony, prefix, postfix, typeof(ItemProto), "InitItemIndices");
            ok += TryPatch(harmony, prefix, postfix, typeof(RecipeProto), "InitRecipeItems");
            ok += TryPatch(harmony, prefix, postfix,
                AccessTools.TypeByName("UXAssist.Production.ProductionCatalogService"), "Rebuild");
            ok += TryPatch(harmony, prefix, postfix,
                AccessTools.TypeByName("UXAssist.Production.ProductionCatalogReader"), "Capture");
            Installed = ok > 0;
            log.LogInfo("[Timeline] 启动耗时探针已安装（" + ok + " 个打点，环境变量 DSPPYPORT_TIMELINE=1）");
        }

        private static int TryPatch(Harmony harmony, MethodInfo prefix, MethodInfo postfix, Type type, string methodName)
        {
            if (type == null)
            {
                log.LogWarning("[Timeline] 类型不存在，跳过打点：" + methodName);
                return 0;
            }
            try
            {
                MethodInfo target = AccessTools.Method(type, methodName);
                if (target == null)
                {
                    log.LogWarning("[Timeline] 方法不存在，跳过打点：" + type.Name + "." + methodName);
                    return 0;
                }
                harmony.Patch(target, prefix: new HarmonyMethod(prefix), postfix: new HarmonyMethod(postfix));
                return 1;
            }
            catch (Exception ex)
            {
                log.LogWarning("[Timeline] 打点失败 " + type.Name + "." + methodName + "：" + ex.Message);
                return 0;
            }
        }

        private static void Prefix(MethodBase __originalMethod)
        {
            Mark("→ " + Describe(__originalMethod));
        }

        private static void Postfix(MethodBase __originalMethod)
        {
            Mark("← " + Describe(__originalMethod));
        }

        private static void Mark(string label)
        {
            if (log == null)
            {
                return;
            }
            long now = Clock.ElapsedMilliseconds;
            long gap = now - lastMs;
            lastMs = now;
            if (gap >= 200 || label.StartsWith("→", StringComparison.Ordinal) == false)
            {
                log.LogInfo(string.Format("[Timeline] +{0,7} ms (+{1,6} ms) {2}", now, gap, label));
                return;
            }
            log.LogDebug(string.Format("[Timeline] +{0,7} ms (+{1,6} ms) {2}", now, gap, label));
        }

        private static string Describe(MethodBase method)
        {
            if (method == null)
            {
                return "?";
            }
            return method.DeclaringType != null
                ? method.DeclaringType.Name + "." + method.Name
                : method.Name;
        }
    }
}
