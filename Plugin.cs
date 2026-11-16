using System;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using CommonAPI;
using CommonAPI.Systems;
using DSPPyPort.Core;
using HarmonyLib;

namespace DSPPyPort
{
    /// <summary>
    /// DSP-PyPort：通用内容注册引擎。
    /// 插件本身不含任何 PY 内容——全部从 BepInEx\plugins\DSP-PyPort\content\ 的 JSON 读取
    /// （契约见 _analysis/py-port/docs/content-schema.md），改内容不需要重编译。
    /// </summary>
    [BepInPlugin(Guid, PluginName, VersionString)]
    [BepInDependency(DependencyProbe.CommonApiGuid, BepInDependency.DependencyFlags.HardDependency)]
    [BepInDependency(DependencyProbe.LdbToolGuid, BepInDependency.DependencyFlags.HardDependency)]
    // 软依赖：缺失时仍加载（优雅降级），但启动会打 Error 说明宽配方会被引擎静默截断（手册 §3.3）。
    [BepInDependency(DependencyProbe.RecipeSlotsGuid, BepInDependency.DependencyFlags.SoftDependency)]
    [CommonAPISubmoduleDependency(new string[] { "ProtoRegistry", "LocalizationModule" })]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "org.pyport.dsp";
        public const string PluginName = "DSP-PyPort";
        public const string VersionString = "0.1.0";

        internal static Plugin Instance { get; private set; }
        internal static ManualLogSource Log { get; private set; }
        internal static string PluginDir { get; private set; }

        private Registrar registrar;

        private void Awake()
        {
            Instance = this;
            Log = Logger;
            PluginDir = ResolvePluginDirectory();

            Log.LogInfo(PluginName + " " + VersionString + " loading... | 插件目录=" + PluginDir);
            Log.LogInfo("[PyPort] ID 规划：物品 6300-11999 / 配方 2000-7999+ / 科技 2000-3999 / 模型 800-999 / " +
                        "建造索引 900-999 / 自定义配方类型 60-79");

            DependencyProbe.Report(Log);

            // 引擎兼容修复：必须在注册/Preload 之前挂上（CommonAPI 在 onLoadingFinished 之前重建 PrefabDesc 数组）。
            try
            {
                bool fixPrefabDesc = Config.Bind(
                    "Compat",
                    "FixPrefabDescArray",
                    true,
                    "修复 CommonAPI 重建 PrefabDesc 数组时按 dataArray.Length+64 分配导致的越界（模型 ID >= 约 740 时必现；" +
                    "越界会让 onLoadingFinished 不触发、所有 MOD 的收尾回填失效）。默认开启，排查冲突时可临时关闭。").Value;
                if (fixPrefabDesc)
                {
                    PrefabDescArrayFix.Install(new Harmony(Guid), Log);
                }
                else
                {
                    Log.LogWarning("[PyPort] 已按配置关闭 PrefabDesc 数组扩容补丁（Compat.FixPrefabDescArray=false）");
                }
            }
            catch (Exception ex)
            {
                Log.LogError("[PyPort] 兼容补丁安装异常（已隔离）：" + ex);
            }

            string contentRoot = Path.Combine(PluginDir, "content");
            LoadedContent loaded = ContentLoader.LoadAll(contentRoot, Log);
            if (loaded.IsEmpty)
            {
                Log.LogError("[PyPort] 没有启用任何内容模块：请检查 " + Path.Combine(contentRoot, "manifest.json") +
                             " 的 modules[].enabled");
            }
            else
            {
                Log.LogInfo(string.Format("[PyPort] 内容包加载完成：启用模块 {0} 个{1}",
                    loaded.Modules.Count,
                    loaded.DisabledModules.Count > 0 ? "，禁用 " + loaded.DisabledModules.Count + " 个" : string.Empty));
            }

            try
            {
                // 所有注册必须在 Awake 内、StartModLoad 块内完成（手册 §1.1/§1.2）
                using (ProtoRegistry.StartModLoad(Guid))
                {
                    registrar = new Registrar(loaded, Log, contentRoot);
                    registrar.RegisterAll();
                }
            }
            catch (Exception ex)
            {
                Log.LogError("[PyPort] 注册阶段异常（插件继续加载，内容可能不完整）：" + ex);
            }

            ProtoRegistry.onLoadingFinished += OnLoadingFinished;
            Log.LogInfo(PluginName + " " + VersionString + " loaded（注册请求已提交，等待 Preload 收尾）");
        }

        private static string ResolvePluginDirectory()
        {
            try
            {
                string location = Assembly.GetExecutingAssembly().Location;
                if (!string.IsNullOrEmpty(location))
                {
                    return Path.GetDirectoryName(location);
                }
            }
            catch
            {
            }
            return Paths.PluginPath;
        }

        private static void OnLoadingFinished()
        {
            if (Instance == null || Instance.registrar == null)
            {
                return;
            }
            try
            {
                Instance.registrar.ApplyPostLoad();
            }
            catch (Exception ex)
            {
                Log.LogError("[PyPort] onLoadingFinished 收尾失败：" + ex);
            }
        }

        private void Update()
        {
            if (registrar == null)
            {
                return;
            }
            registrar.Tick();
        }

        private void OnDestroy()
        {
            ProtoRegistry.onLoadingFinished -= OnLoadingFinished;
        }
    }
}
