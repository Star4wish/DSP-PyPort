using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Logging;

namespace DSPPyPort.Core
{
    /// <summary>
    /// BuildBarTool 第二排快速建造栏集成（队列 2，2026-11-19）。
    ///
    /// 用户口径：18 座 PY 建筑**不再占原版建造菜单槽位**，全部放 BuildBarTool 的「第二排」快速栏：
    ///   - recipe type 60-67（8 座）→ category 5，slot 1-8；
    ///   - recipe type 68-77（10 座）→ category 8，slot 1-10；
    ///   - 同时 `proto.BuildIndex = 0` 移出原版建造菜单（**不改原版建筑任何 BuildIndex**）。
    ///
    /// 时序依据（BBT 反编译 `BBT-BuildBarToolPlugin.cs`）：
    ///   - `BuildBarTool.SetBuildBar(category,index,itemId,isTopRow:true)` 只是写静态 `protoIds[category,index]`；
    ///   - BBT 在 `VFPreload_Patch.VFPreloadPostPatch` 的 **HarmonyPostfix** 里才把 protoIds 解析成 ItemProto；
    ///   - 我们的 `onLoadingFinished`（CommonAPI PostAddDataAction）早于该 postfix → 在这里调用即可被 BBT 读到。
    /// 未安装 BuildBarTool 时回退：保留 `[Compat] AutoBuildIndex` 自动分配的建造菜单槽位（日志 WARN）。
    /// </summary>
    internal static class BuildBarIntegration
    {
        internal const string BuildBarToolGuid = "Gnimaerd.DSP.plugin.BuildBarTool";

        internal static bool Enabled = true;
        internal static bool Available { get; private set; }
        internal static bool Tier2Applied { get; private set; }
        internal static string DetectedVersion = string.Empty;

        private static Type buildBarType;
        private static MethodInfo setBuildBarMethod;

        /// <summary>启动时探测 BuildBarTool（只在日志里说明，不阻断）。</summary>
        internal static void Detect(ManualLogSource log)
        {
            try
            {
                if (Chainloader.PluginInfos != null)
                {
                    PluginInfo info;
                    if (Chainloader.PluginInfos.TryGetValue(BuildBarToolGuid, out info) && info != null)
                    {
                        try
                        {
                            DetectedVersion = info.Metadata != null && info.Metadata.Version != null
                                ? info.Metadata.Version.ToString()
                                : string.Empty;
                        }
                        catch
                        {
                            DetectedVersion = string.Empty;
                        }
                    }
                }
                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] types;
                    try
                    {
                        types = assembly.GetTypes();
                    }
                    catch (ReflectionTypeLoadException ex)
                    {
                        types = ex.Types;
                    }
                    catch
                    {
                        continue;
                    }
                    if (types == null)
                    {
                        continue;
                    }
                    for (int i = 0; i < types.Length; i++)
                    {
                        Type type = types[i];
                        if (type != null && type.FullName == "BuildBarTool.BuildBarTool")
                        {
                            buildBarType = type;
                            break;
                        }
                    }
                    if (buildBarType != null)
                    {
                        break;
                    }
                }
                if (buildBarType != null)
                {
                    setBuildBarMethod = buildBarType.GetMethod("SetBuildBar",
                        BindingFlags.Public | BindingFlags.Static,
                        null,
                        new Type[] { typeof(int), typeof(int), typeof(int), typeof(bool) },
                        null);
                }
                Available = buildBarType != null && setBuildBarMethod != null;
                if (Available)
                {
                    log.LogInfo("[BBT] 已找到 BuildBarTool" +
                                (string.IsNullOrEmpty(DetectedVersion) ? string.Empty : " v" + DetectedVersion) +
                                " → 18 座 PY 建筑将绑定到第二排快速建造栏（配置 [Compat] UseBuildBarToolTier2）");
                }
                else
                {
                    log.LogWarning("[BBT] 未找到 BuildBarTool.SetBuildBar（未安装或版本不匹配）：" +
                                   "回退为建造菜单自动分配槽位（[Compat] AutoBuildIndex）");
                }
            }
            catch (Exception ex)
            {
                Available = false;
                log.LogWarning("[BBT] 探测失败（回退建造菜单自动分配）：" + ex.Message);
            }
        }

        /// <summary>
        /// 在 onLoadingFinished 里调用：把 18 座建筑绑到 BBT 第二排，并把它们的 BuildIndex 置 0。
        /// 返回 true 表示至少绑定了 1 座（后续跳过建造菜单槽位修复逻辑）。
        /// </summary>
        internal static bool TryApply(List<RegisteredBuilding> buildings, ManualLogSource log)
        {
            if (!Enabled)
            {
                log.LogInfo("[BBT] [Compat] UseBuildBarToolTier2=false：保留建造菜单自动分配槽位（不使用第二排）");
                return false;
            }
            if (!Available || setBuildBarMethod == null)
            {
                log.LogWarning("[BBT] BuildBarTool 不可用：18 座 PY 建筑保留建造菜单自动分配槽位（回退路径）");
                return false;
            }
            if (buildings == null || buildings.Count == 0)
            {
                return false;
            }

            // 按 recipe type 排序，保证确定性 & 日志可读
            List<RegisteredBuilding> sorted = new List<RegisteredBuilding>(buildings);
            sorted.Sort(delegate(RegisteredBuilding a, RegisteredBuilding b)
            {
                return a.Def.RecipeType.CompareTo(b.Def.RecipeType);
            });

            int ok = 0;
            int failed = 0;
            string table = string.Empty;
            for (int i = 0; i < sorted.Count; i++)
            {
                RegisteredBuilding building = sorted[i];
                if (building.Item == null)
                {
                    continue;
                }
                int type = building.Def.RecipeType;
                int category;
                int slot;
                if (type >= 60 && type <= 67)
                {
                    category = 5;
                    slot = type - 60 + 1; // type60→slot1 ... type67→slot8
                }
                else if (type >= 68 && type <= 77)
                {
                    category = 8;
                    slot = type - 68 + 1; // type68→slot1 ... type77→slot10
                }
                else
                {
                    log.LogWarning("[BBT] 建筑 item " + building.ExpectedItemId + " 的 recipe_type=" + type +
                                   " 不在 BBT 映射范围（60-77），保留建造菜单槽位");
                    failed++;
                    continue;
                }
                bool bound = false;
                try
                {
                    object result = setBuildBarMethod.Invoke(null, new object[] { category, slot, building.Item.ID, true });
                    bound = result is bool && (bool)result;
                }
                catch (Exception ex)
                {
                    log.LogWarning("[BBT] 绑定 type=" + type + " item=" + building.ExpectedItemId + " 失败：" + ex.Message);
                }
                if (bound)
                {
                    // 移出原版建造菜单（BBT 的 SetBuildBar 扩展方法也是这么做的；这里手动设，避免依赖扩展方法可见性）
                    building.Item.BuildIndex = 0;
                    building.Def.BuildIndex = 0;
                    ok++;
                    if (table.Length > 0)
                    {
                        table += "、";
                    }
                    table += "t" + type + "→cat" + category + "/s" + slot + "(item " + building.Item.ID + ", BuildIndex=" + building.Item.BuildIndex + ")";
                }
                else
                {
                    failed++;
                    log.LogWarning("[BBT] 绑定失败（槽位被占用？）：type=" + type + " item=" + building.ExpectedItemId +
                                   " → category " + category + " slot " + slot + "；该建筑保留建造菜单槽位");
                }
            }

            Tier2Applied = ok > 0;
            log.LogInfo(string.Format(
                "[BBT] 第二排快速建造栏绑定完成：{0}/{1} 座（失败 {2}）；分配表：{3}",
                ok, sorted.Count, failed, table));
            if (failed > 0)
            {
                log.LogWarning("[BBT] 有 " + failed + " 座绑定失败，已回退保留其建造菜单槽位（见上方逐条 WARN）");
            }
            return Tier2Applied;
        }
    }
}
