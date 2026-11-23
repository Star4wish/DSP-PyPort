using System;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;

namespace DSPPyPort.Core
{
    /// <summary>
    /// SkillSystem 构造函数防御补丁（2026-11-18，崩溃修复 task-13）。
    ///
    /// 引擎 `SkillSystem..ctor(SpaceSector)`（full.cs L107961）里有 **10 个数组**：
    ///   HpMaxByModelIndex / HpUpgradeByModelIndex / HpRecoverByModelIndex /
    ///   RoughRadiusByModelIndex / RoughHeightByModelIndex / RoughWidthByModelIndex /
    ///   BarHeightByModelIndex / BarWidthByModelIndex / ColliderComplexityByModelIndex /
    ///   EnemySandCountByModelIndex
    /// 全部 `new T[LDB.models.dataArray.Length + 64]`，随后 `arr[dataArray[j].ID] = ...` 按 **模型 ID** 写入。
    /// 本机 dataArray.Length=684（原版 664 + 内容包 18 座建筑）→ 容量 748；
    /// 若建筑模型 ID 为 800-817 就会 IndexOutOfRange，崩溃点在建新档/读档时的
    /// `SpaceSector.Init → new SkillSystem(...) → GameData.Import`（用户实测）。
    ///
    /// 这与已修的 `InitPrefabDescArray` 是同一类原版 bug。修复分两层：
    ///   1. 内容侧把建筑模型 ID 降到安全段（666-683，task-14）；
    ///   2. 本补丁按「最大模型 ID + 1」分配，彻底消除该越界（也兼容未来更多建筑）。
    ///
    /// Prefix 成功则 return false 跳过原构造；任何异常 return true 回退原实现（保持原行为，不制造二次故障）。
    /// </summary>
    internal static class SkillSystemCtorFix
    {
        internal static bool Installed { get; private set; }
        internal static int LastCapacity { get; private set; }
        internal static int LastMaxModelId { get; private set; }
        private static int rebuildCount;
        private static bool safePathLogged;
        private static FieldInfo fireLockField;
        private static FieldInfo combatLockField;
        private static object lastRuntimeChecked;

        /// <summary>
        /// 运行时兜底（P0-1）：任何路径导致 SkillSystem 的 readonly 锁对象仍为 null（例如其它 MOD
        /// 也写了跳过构造的补丁），这里在拿到实例后立即补上并 WARN，避免敌人开火 lock(null) 崩溃。
        /// </summary>
        internal static void CheckRuntimeLocks(ManualLogSource log)
        {
            try
            {
                if (GameMain.data == null || GameMain.data.spaceSector == null)
                {
                    return;
                }
                object sector = GameMain.data.spaceSector;
                FieldInfo skillField = sector.GetType().GetField("skillSystem",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (skillField == null)
                {
                    return;
                }
                object skillSystem = skillField.GetValue(sector);
                if (skillSystem == null || ReferenceEquals(skillSystem, lastRuntimeChecked))
                {
                    return;
                }
                lastRuntimeChecked = skillSystem;
                EnsureLockObjects(skillSystem, log, "运行时检查");
            }
            catch
            {
            }
        }

        internal static void Install(Harmony harmony, ManualLogSource log)
        {
            try
            {
                MethodBase ctor = AccessTools.Constructor(typeof(SkillSystem), new Type[] { typeof(SpaceSector) });
                if (ctor == null)
                {
                    log.LogError("[兼容] 找不到 SkillSystem..ctor(SpaceSector)，模型 ID 越界防御补丁未安装");
                    return;
                }
                MethodInfo prefix = typeof(SkillSystemCtorFix).GetMethod(
                    "Prefix", BindingFlags.Static | BindingFlags.NonPublic);
                if (prefix == null)
                {
                    log.LogError("[兼容] SkillSystemCtorFix.Prefix 反射失败，补丁未安装");
                    return;
                }
                harmony.Patch(ctor, prefix: new HarmonyMethod(prefix));
                Installed = true;
                log.LogInfo("[兼容] 已安装 SkillSystem..ctor 数组扩容补丁（防御模型 ID 越界导致建新档/读档崩溃）");
            }
            catch (Exception ex)
            {
                log.LogError("[兼容] SkillSystem..ctor 补丁安装失败（模型 ID 超出 dataArray.Length+64 时建新档/读档会崩溃）：" + ex);
            }
        }

        // Harmony 注入：__instance = 正在构造的实例，_sector = 原构造参数（按名字匹配）。
        private static bool Prefix(SkillSystem __instance, SpaceSector _sector)
        {
            try
            {
                ManualLogSource log = Plugin.Log;
                ModelProto[] dataArray = LDB.models.dataArray;

                int size = dataArray.Length + 64;
                int maxId = 0;
                for (int i = 0; i < dataArray.Length; i++)
                {
                    ModelProto proto = dataArray[i];
                    if (proto != null && proto.ID > maxId)
                    {
                        maxId = proto.ID;
                    }
                }
                int originalCapacity = size;
                LastCapacity = originalCapacity;
                LastMaxModelId = maxId;

                // v0.9.0 关键修复（P0-1）：原构造函数的**字段初始化器**会创建
                // `public readonly object fire_lock = new object();` 与 `combat_stat_rw_lock = new object();`。
                // 旧版 Prefix 一律 return false 跳过原构造 → 这两个锁为 null → 敌人开火
                // `lock (skillSystem.fire_lock)` 抛 ArgumentNullException 崩溃。
                // 现在：模型 ID 未越界时**直接执行原构造函数**（保留所有字段初始化器）；只有真的会越界才接管。
                if (maxId + 1 <= originalCapacity)
                {
                    if (!safePathLogged)
                    {
                        safePathLogged = true;
                        log.LogInfo("[兼容] SkillSystem..ctor 检测：最大模型 ID=" + maxId + " <= 原版容量=" +
                                    originalCapacity + "，使用原构造函数（保留 fire_lock / combat_stat_rw_lock 等字段初始化器）");
                    }
                    return true;
                }

                rebuildCount++;
                int expandedSize = size;
                if (maxId + 1 > expandedSize)
                {
                    expandedSize = maxId + 1;
                }
                size = expandedSize;
                log.LogInfo("[兼容] SkillSystem..ctor 第 " + rebuildCount + " 次构造：最大模型 ID=" + maxId +
                            " > 原版容量=" + originalCapacity + "（会越界 " + (maxId + 1 - originalCapacity) +
                            " 项）→ 接管并按 maxID+1 扩容到 " + expandedSize);
                // 接管路径必须自己初始化 readonly 锁对象（字段初始化器不会执行）。
                EnsureLockObjects(__instance, log, "构造接管");

                // 字段全为 public：直接赋值（10 个数组是 public static）。
                __instance.sector = _sector;
                __instance.gameData = _sector.gameData;
                __instance.history = __instance.gameData.history;
                __instance.astroFactories = __instance.gameData.galaxy.astrosFactory;

                int[] hpMax = new int[size];
                int[] hpUpgrade = new int[size];
                int[] hpRecover = new int[size];
                float[] roughRadius = new float[size];
                float[] roughHeight = new float[size];
                float[] roughWidth = new float[size];
                float[] barHeight = new float[size];
                float[] barWidth = new float[size];
                int[] colliderComplexity = new int[size];
                int[] enemySandCount = new int[size];
                for (int i = 0; i < size; i++)
                {
                    hpMax[i] = 1;
                    hpUpgrade[i] = 1;
                    hpRecover[i] = 0;
                    roughRadius[i] = 1f;
                    roughHeight[i] = 1f;
                    roughWidth[i] = 1f;
                    barHeight[i] = 1f;
                    barWidth[i] = 1f;
                    colliderComplexity[i] = 0;
                    enemySandCount[i] = 0;
                }
                for (int j = 0; j < dataArray.Length; j++)
                {
                    ModelProto proto = dataArray[j];
                    if (proto == null || proto.ID < 0 || proto.ID >= size)
                    {
                        continue;
                    }
                    int id = proto.ID;
                    hpMax[id] = proto.HpMax;
                    hpUpgrade[id] = proto.HpUpgrade;
                    hpRecover[id] = proto.HpRecover;
                    PrefabDesc desc = proto.prefabDesc;
                    if (desc != null)
                    {
                        roughRadius[id] = desc.roughRadius;
                        roughHeight[id] = desc.roughHeight;
                        roughWidth[id] = desc.roughWidth;
                        barHeight[id] = desc.barHeight;
                        barWidth[id] = desc.barWidth;
                        colliderComplexity[id] = desc.colliderComplexity;
                        enemySandCount[id] = desc.enemySandCount;
                    }
                }
                SkillSystem.HpMaxByModelIndex = hpMax;
                SkillSystem.HpUpgradeByModelIndex = hpUpgrade;
                SkillSystem.HpRecoverByModelIndex = hpRecover;
                SkillSystem.RoughRadiusByModelIndex = roughRadius;
                SkillSystem.RoughHeightByModelIndex = roughHeight;
                SkillSystem.RoughWidthByModelIndex = roughWidth;
                SkillSystem.BarHeightByModelIndex = barHeight;
                SkillSystem.BarWidthByModelIndex = barWidth;
                SkillSystem.ColliderComplexityByModelIndex = colliderComplexity;
                SkillSystem.EnemySandCountByModelIndex = enemySandCount;
                return false;
            }
            catch (Exception ex)
            {
                try
                {
                    Plugin.Log.LogError("[兼容] SkillSystem..ctor 防御实现失败，回退原实现：" + ex);
                }
                catch
                {
                }
                return true;
            }
        }

        /// <summary>
        /// 确保实例上的 readonly 锁对象已初始化（字段：fire_lock / combat_stat_rw_lock）。
        /// 用途：① 接管构造路径时字段初始化器不会执行；② 运行时兜底检查（其它 MOD 若也有跳过构造的补丁）。
        /// </summary>
        internal static void EnsureLockObjects(object skillSystem, ManualLogSource log, string reason)
        {
            if (skillSystem == null)
            {
                return;
            }
            try
            {
                if (fireLockField == null)
                {
                    fireLockField = typeof(SkillSystem).GetField("fire_lock", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                }
                if (combatLockField == null)
                {
                    combatLockField = typeof(SkillSystem).GetField("combat_stat_rw_lock", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                }
                int fixedCount = 0;
                if (fireLockField != null && fireLockField.GetValue(skillSystem) == null)
                {
                    fireLockField.SetValue(skillSystem, new object());
                    fixedCount++;
                }
                if (combatLockField != null && combatLockField.GetValue(skillSystem) == null)
                {
                    combatLockField.SetValue(skillSystem, new object());
                    fixedCount++;
                }
                if (fixedCount > 0)
                {
                    log.LogWarning("[兼容] SkillSystem 锁对象为 null（" + reason + "），已补初始化 " + fixedCount +
                                   " 个（fire_lock/combat_stat_rw_lock）——否则敌人开火 lock(null) 会崩");
                }
            }
            catch (Exception ex)
            {
                try
                {
                    log.LogWarning("[兼容] SkillSystem 锁对象初始化失败（" + reason + "）：" + ex.Message);
                }
                catch
                {
                }
            }
        }
    }
}
