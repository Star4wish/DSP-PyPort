using System;
using System.Diagnostics;
using BepInEx.Logging;

namespace DSPPyPort.Core
{
    /// <summary>
    /// 启动分阶段计时（v0.7.0）：用于定位「启动极慢」的瓶颈。
    /// Plugin.Awake 第一行 Start()，之后每个阶段 Mark() 一次；日志格式 `[Perf] 阶段名：X ms（累计 Y ms）`。
    /// 只是观测，不改变任何逻辑；Debug 级日志关闭时也不影响（LogInfo）。
    /// </summary>
    internal static class Perf
    {
        private static readonly Stopwatch Total = new Stopwatch();
        private static readonly Stopwatch Stage = new Stopwatch();
        private static bool started;
        private static long processStartUnixMs;

        internal static void Start()
        {
            Total.Reset();
            Stage.Reset();
            Total.Start();
            Stage.Start();
            started = true;
            try
            {
                // 进程启动时刻（含 Unity 引擎 + BepInEx 预加载 + 先加载的插件），用于把启动耗时拆成
                // 「进程启动→本插件 Awake」「Awake→onLoadingFinished」「后面到主菜单」三段。
                processStartUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                System.Diagnostics.Process process = System.Diagnostics.Process.GetCurrentProcess();
                long startMs = (long)(process.StartTime.ToUniversalTime() - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
                if (startMs > 0 && startMs <= processStartUnixMs)
                {
                    processStartUnixMs = startMs;
                }
            }
            catch
            {
            }
        }

        /// <summary>从进程启动到现在的毫秒数（用于拆分引擎/其它 MOD 占用的启动时间）。</summary>
        internal static long SinceProcessStartMs
        {
            get
            {
                if (processStartUnixMs <= 0)
                {
                    return 0;
                }
                return Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - processStartUnixMs);
            }
        }

        internal static long ElapsedMs
        {
            get { return started ? Total.ElapsedMilliseconds : 0; }
        }

        /// <summary>结束当前阶段并打印「阶段耗时 + 累计耗时」。</summary>
        internal static long Mark(ManualLogSource log, string stage)
        {
            if (!started)
            {
                return 0;
            }
            Stage.Stop();
            long ms = Stage.ElapsedMilliseconds;
            log.LogInfo("[Perf] " + stage + "：" + ms + " ms（累计 " + Total.ElapsedMilliseconds + " ms）");
            Stage.Reset();
            Stage.Start();
            return ms;
        }

        /// <summary>结束当前阶段但不打印（用于把多段合并成一段再打印）。</summary>
        internal static long Take()
        {
            if (!started)
            {
                return 0;
            }
            Stage.Stop();
            long ms = Stage.ElapsedMilliseconds;
            Stage.Reset();
            Stage.Start();
            return ms;
        }

        /// <summary>追加一行不带阶段切换的累计信息。</summary>
        internal static void Note(ManualLogSource log, string message)
        {
            log.LogInfo("[Perf] " + message + "（累计 " + ElapsedMs + " ms）");
        }
    }
}
