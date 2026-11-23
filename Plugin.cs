using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
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
    ///
    /// v0.7.0（2026-11-18）：
    ///   - 修复建新档/读档崩溃：SkillSystem..ctor 的 10 个 `new T[models.dataArray.Length+64]` 按模型 ID 写数组越界
    ///     → SkillSystemCtorFix（Harmony Prefix 按 maxModelID+1 分配）+ 启动容量自检；
    ///   - 启动性能：分阶段 Stopwatch 计时 + 图标懒加载（LazyIcons 默认 true，首次访问 iconSprite 才解码 PNG）
    ///   - 分包开关：[Modules] Enable_<moduleId>，只加载需要的包以缩短启动时间。
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
        public const string VersionString = "0.9.3";

        internal static Plugin Instance { get; private set; }
        internal static ManualLogSource Log { get; private set; }
        internal static string PluginDir { get; private set; }
        internal static bool LazyIcons { get; private set; }

        private Registrar registrar;
        private bool sandboxUnlockPyContent = true;
        private bool autoBuildIndexMode = true;
        private readonly Harmony harmony = new Harmony(Guid);

        private void Awake()
        {
            Perf.Start();
            Instance = this;
            Log = Logger;
            PluginDir = ResolvePluginDirectory();

            Log.LogInfo(PluginName + " " + VersionString + " loading... | 插件目录=" + PluginDir);
            Log.LogInfo("[Perf] 进程启动→本插件 Awake（含 Unity/BepInEx/先加载插件）：" + Perf.SinceProcessStartMs + " ms");
            Log.LogInfo("[PyPort] 启动优化：图标懒加载（[Perf] LazyIcons，默认 true）；分包开关见 BepInEx\\config\\org.pyport.dsp.cfg 的 [Modules] 节");

            DependencyProbe.Report(Log);
            BuildBarIntegration.Detect(Log);
            Perf.Mark(Log, "前置探测");

            // 引擎兼容修复：必须在注册/Preload 之前挂上。
            //  1) SkillSystem..ctor：建新档/读档崩溃的根因（10 个数组按模型 ID 写下标越界）；
            //  2) InitPrefabDescArray：CommonAPI 重建 PrefabDesc 数组越界（会让 onLoadingFinished 中断）。
            try
            {
                bool fixPrefabDesc = Config.Bind(
                    "Compat",
                    "FixPrefabDescArray",
                    true,
                    "修复 CommonAPI 重建 PrefabDesc 数组时按 dataArray.Length+64 分配导致的越界（模型 ID 超余量时必现；" +
                    "越界会让 onLoadingFinished 不触发、所有 MOD 的收尾回填失效）。默认开启，排查冲突时可临时关闭。").Value;
                if (fixPrefabDesc)
                {
                    PrefabDescArrayFix.Install(harmony, Log);
                }
                else
                {
                    Log.LogWarning("[PyPort] 已按配置关闭 PrefabDesc 数组扩容补丁（Compat.FixPrefabDescArray=false）");
                }

                bool fixSkillSystem = Config.Bind(
                    "Compat",
                    "FixSkillSystemCtor",
                    false,
                    "【默认关闭】SkillSystem..ctor 接管补丁：仅在建筑模型 ID 超过 models.dataArray.Length+64 时才需要。" +
                    "旧版默认开启会用 Prefix 跳过原构造函数，导致 fire_lock/combat_stat_rw_lock 字段初始化器不执行，" +
                    "敌人开火 lock(null) 崩溃（P0-1）。当前内容模型 ID 720-737 < 容量 748，原构造函数安全，故默认关闭；" +
                    "即使旧 cfg 仍为 true，补丁也会先检查容量，未越界时自动走原构造函数。").Value;
                if (fixSkillSystem)
                {
                    SkillSystemCtorFix.Install(harmony, Log);
                }
                else
                {
                    Log.LogInfo("[兼容] SkillSystem..ctor 补丁未启用（默认关闭）：当前模型 ID 未越界，原构造函数安全并会初始化 fire_lock 等锁对象");
                }

                bool skipLdbToolBind = Config.Bind(
                    "Compat",
                    "SkipLdbToolBind",
                    true,
                    "跳过 LDBTool 对本 MOD proto 的 ID/GridIndex 配置绑定（性能关键）：LDBTool 每遇到一个新 key 就整文件保存，" +
                    "八包合集约 9663 个 proto 会写约 7GB 磁盘 I/O（启动 300s+）。本 MOD 的 ID/格位由 content JSON 固定并自检，" +
                    "不依赖 LDBTool 缓存。默认开启；仅在需要让 LDBTool 接管 ID 时关闭。").Value;
                if (skipLdbToolBind)
                {
                    LdbToolBindSkipFix.Install(harmony, Log);
                }
                else
                {
                    Log.LogWarning("[PyPort] 已按配置关闭 LDBTool 绑定跳过（Compat.SkipLdbToolBind=false），启动会变慢");
                }

                autoBuildIndexMode = Config.Bind(
                    "Compat",
                    "AutoBuildIndex",
                    true,
                    "建筑 BuildIndex 自动分配（原版 UIBuildMenu 只收录 slot<=12 且可见分类页 page1-9；" +
                    "内容包若用 900+ 顺序号会被建造菜单丢弃/与原版建筑撞槽）。默认开启；关闭则沿用 buildings.json/900+ 顺序值。").Value;
                sandboxUnlockPyContent = Config.Bind(
                    "Compat",
                    "SandboxUnlockPyContent",
                    true,
                    "沙盒模式自动解锁本 MOD 全部配方与科技（原版 SetForNewGame 只解锁原版初始配方，沙盒不解除锁定，导致新沙盒看不到 PY 配方）。" +
                    "仅 isSandboxMode 生效，普通模式不动，保持科技解锁玩法。默认开启。").Value;
                Registrar.TechReplaceMode = Config.Bind(
                    "Compat",
                    "TechReplaceMode",
                    true,
                    "科技替换模式：PY 科技占用原版主页科技 ID（1-1999）并原位覆盖原版 TechProto。" +
                    "原版 TechProto.page 硬编码为 ID>=2000→升级页，故只有 ID<2000 的科技能显示在科技树主页；" +
                    "本 MOD 借用原版主页槽位（1508/1509/1901-1904 保留），未覆盖的原版主页科技会被隐藏。" +
                    "关闭则科技 ID 与原版冲突，将拒绝注册全部内容模块。默认开启。").Value;
                if (!Registrar.TechReplaceMode)
                {
                    Log.LogWarning("[PyPort] [Compat] TechReplaceMode=false：科技 ID 与原版冲突会拒绝注册内容模块");
                }

                bool fixIconAtlas = Config.Bind(
                    "Compat",
                    "FixIconAtlas",
                    true,
                    "修复配方/物品选择器图标全显示同一张（原版 IconSet 图集只有 25×25=625 格且按 Sprite 索引；" +
                    "本 MOD 的 9664 个运行时图标远超容量，索引全为 0）。开启后在 IconSet.Create 后把 PY PNG 注入图集空闲格并重写索引；" +
                    "默认开启。关闭则选择器图标会回退为同一张（原版行为）。").Value;
                if (fixIconAtlas)
                {
                    IconSetAtlasFix.Install(harmony, Log);
                }
                else
                {
                    Log.LogWarning("[PyPort] 已关闭图标图集修复（Compat.FixIconAtlas=false），配方/物品选择器图标可能全为同一张");
                }

                BuildBarIntegration.Enabled = Config.Bind(
                    "Compat",
                    "UseBuildBarToolTier2",
                    true,
                    "把 18 座 PY 建筑绑到 BuildBarTool 的「第二排」快速建造栏（分类 5：type60-67 slot1-8；" +
                    "分类 8：type68-77 slot1-10），并把它们的 BuildIndex 置 0 移出原版建造菜单（不动原版建筑）。" +
                    "未安装 BuildBarTool 时自动回退为建造菜单自动分配（AutoBuildIndex）。默认开启。").Value;
                if (!BuildBarIntegration.Enabled)
                {
                    Log.LogWarning("[PyPort] [Compat] UseBuildBarToolTier2=false：不绑定 BBT 第二排，保留建造菜单自动分配");
                }
            }
            catch (Exception ex)
            {
                Log.LogError("[PyPort] 兼容补丁安装异常（已隔离）：" + ex);
            }
            Perf.Mark(Log, "兼容补丁安装（PrefabDesc/SkillSystem）");

            // 启动耗时时间线探针（仅环境变量 DSPPYPORT_TIMELINE=1 时生效，正式用户无影响）
            try
            {
                TimelineProbe.Install(harmony, Log);
            }
            catch (Exception ex)
            {
                Log.LogWarning("[Timeline] 探针安装失败（不影响功能）：" + ex.Message);
            }

            // 配方选择器 UI（队列 3 / task-18）：Sprite 直出图标 + 分类页签 + 纵向滚动条 + 页码。
            // 原版 UIRecipePicker 只渲染 GridIndex 千位=1/2、图标走共享图集（只有 64 空闲格 → 近似图标）。
            try
            {
                RecipePickerUiFix.Enabled = Config.Bind(
                    "UI",
                    "EnableRecipePickerUi",
                    true,
                    "配方选择器 UI 增强：112 个格位改为 Sprite 直出（PY 图标 1:1 精确显示），" +
                    "按配方大类提供分类页签（横向滚动）、右侧纵向滚动条和页码（第 X/Y 页），替代原版 1/2 翻页按钮。" +
                    "关闭则回退原版行为（GridIndex 千位 1/2 的配方才显示）。默认开启。").Value;
                if (RecipePickerUiFix.Enabled)
                {
                    RecipePickerUiFix.Install(harmony, Log);
                }
                else
                {
                    Log.LogWarning("[PyPort] [UI] EnableRecipePickerUi=false：配方选择器保持原版行为（PY 配方可能不显示）");
                }
            }
            catch (Exception ex)
            {
                Log.LogError("[选择器UI] 补丁安装异常（已隔离，配方选择器将回退原版 1/2 页行为）：" + ex);
            }

            // ---- 图标加载模式（必须在内容注册之前确定：注册时的 IconPath 策略依赖它）----
            string contentRoot = Path.Combine(PluginDir, "content");
            bool lazyRequested = Config.Bind(
                "Perf",
                "LazyIcons",
                true,
                "图标懒加载（推荐）：启动时不批量解码 PNG，首次访问 ItemProto/TechProto/RecipeProto.iconSprite 时才加载对应图标，" +
                "可显著缩短启动时间。若某些 UI 出现空白图标，可改为 false 回退启动时批量加载。").Value;
            IconLoader.Configure(contentRoot, lazyRequested, Log);
            if (lazyRequested)
            {
                if (!IconLoader.InstallLazyPatches(harmony, Log))
                {
                    IconLoader.DisableLazy(Log);
                }
            }
            LazyIcons = IconLoader.LazyMode && IconLoader.LazyPatchesInstalled;
            Perf.Mark(Log, "图标模式准备（LazyIcons=" + LazyIcons + "）");

            // ---- 内容加载（manifest + 用户分包开关）----
            Func<string, bool, bool> moduleGate = delegate(string moduleId, bool manifestEnabled)
            {
                ConfigEntry<bool> entry = Config.Bind(
                    "Modules",
                    "Enable_" + moduleId,
                    manifestEnabled,
                    "是否加载内容模块 " + moduleId + "（默认跟随 manifest.enabled）。" +
                    "关闭不想要的包可以显著缩短启动时间；关闭后该包物品/配方/科技/建筑都不注册。");
                return entry.Value;
            };

            LoadedContent loaded = ContentLoader.LoadAll(contentRoot, Log, moduleGate);
            if (loaded.IsEmpty)
            {
                Log.LogError("[PyPort] 没有启用任何内容模块：请检查 " + Path.Combine(contentRoot, "manifest.json") +
                             " 的 modules[].enabled 或本插件 [Modules] 配置");
            }
            else
            {
                Log.LogInfo("[PyPort] 内容包加载完成：启用模块 " + loaded.Modules.Count + " 个（" +
                            DescribeModules(loaded.Modules) + "）" +
                            (loaded.DisabledModules.Count > 0
                                ? "；禁用 " + loaded.DisabledModules.Count + " 个（" + string.Join("、", loaded.DisabledModules.ToArray()) + "）"
                                : string.Empty));
            }
            Perf.Mark(Log, "JSON 解析/内容加载");

            try
            {
                // 所有注册必须在 Awake 内、StartModLoad 块内完成（手册 §1.1/§1.2）
                using (ProtoRegistry.StartModLoad(Guid))
                {
                    registrar = new Registrar(loaded, Log, contentRoot, autoBuildIndexMode);
                    registrar.RegisterAll();
                }
            }
            catch (Exception ex)
            {
                Log.LogError("[PyPort] 注册阶段异常（插件继续加载，内容可能不完整）：" + ex);
            }
            Perf.Mark(Log, "原型注册（StartModLoad 块）");

            ProtoRegistry.onLoadingFinished += OnLoadingFinished;
            Perf.Mark(Log, "Awake 总计");
            Log.LogInfo(PluginName + " " + VersionString + " loaded（注册请求已提交，等待 Preload 收尾）");
        }

        private static string DescribeModules(List<ModuleContent> modules)
        {
            if (modules == null || modules.Count == 0)
            {
                return "无";
            }
            string text = string.Empty;
            for (int i = 0; i < modules.Count; i++)
            {
                if (i > 0)
                {
                    text += "、";
                }
                text += modules[i].Id;
            }
            return text;
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
                // 这一段是 Awake 之后到 onLoadingFinished 之间的全部耗时：LDBTool AddProtos、
                // CommonAPI OnPostAdd 的 Preload（物品/配方/科技）、其它 MOD 的 Preload 都在其中。
                Perf.Mark(Log, "Awake→onLoadingFinished（LDBTool AddProtos + CommonAPI Preload + 其它 MOD）");
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
            // 沙盒模式自动解锁（等 GameMain.data/gameDesc/history 就绪后一次性执行）
            SandboxUnlockFix.Tick(registrar, Log, sandboxUnlockPyContent);
            // P0-1 运行时兜底：SkillSystem 锁对象若为 null 立即补上（防敌人开火 lock(null) 崩溃）
            SkillSystemCtorFix.CheckRuntimeLocks(Log);
            // P0-2：IconSet 可能早于 LDB 合并创建 → LDB 就绪后再注入图集索引
            IconSetAtlasFix.TryInjectPending(Log);
            // 队列 3 自测：仅当环境变量 DSPPYPORT_PICKER_SELFTEST=1 时生效（正式用户无影响）
            try
            {
                RecipePickerUiFix.SelfTestTick(Log);
            }
            catch (Exception ex)
            {
                Log.LogWarning("[选择器UI][自测] 调度异常（不影响正式功能）：" + ex.Message);
            }
        }

        private void OnDestroy()
        {
            ProtoRegistry.onLoadingFinished -= OnLoadingFinished;
        }
    }
}
