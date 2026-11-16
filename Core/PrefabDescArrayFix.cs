using System;
using HarmonyLib;
using BepInEx.Logging;

namespace DSPPyPort.Core
{
    /// <summary>
    /// 引擎兼容修复（2026-11-16 实机验证）：CommonAPI 在 onLoadingFinished 前重建
    /// PrefabDesc 数组时调用 `SpaceSector.InitPrefabDescArray()` / `PlanetFactory.InitPrefabDescArray()`，
    /// 这两个引擎方法按 `LDB.models.dataArray.Length + 64` 分配数组，却按 `dataArray[i].ID` 写入。
    ///
    /// 但 LDBTool.AddProtosToSet 是把 MOD 的 ModelProto **紧凑追加**到 dataArray 末尾（索引≠ID），
    /// 因此当 MOD 注册的模型 ID 超过「原版模型数 + MOD 模型数 + 64」时（本机：原版模型 ID 最大 664，
    /// 加 MolecularHub/MechDawn 的 666/667/700-708 后余量只到 ~740），写入 ID 800 就抛
    /// IndexOutOfRangeException。后果是 CommonAPI 的 OnPostAdd 整体中断、`onLoadingFinished` 不触发，
    /// 所有 MOD 的收尾回填（建筑 assemblerRecipeType、配方标志、自定义图标、解锁闭环）全部静默失效。
    ///
    /// 本补丁只是把数组扩到「最大模型 ID + 1」，不改变任何语义，属于纯防御性修复：
    ///   - 内容包契约（content-schema §7）要求建筑模型用 800–999 段，若不修则该段不可用；
    ///   - 全量 PY 9 个包约 72 座自建建筑，模型 ID 会到 800-871，必须修。
    /// </summary>
    internal static class PrefabDescArrayFix
    {
        internal static bool Installed { get; private set; }

        internal static void Install(Harmony harmony, ManualLogSource log)
        {
            try
            {
                harmony.PatchAll(typeof(PrefabDescArrayFix).Assembly);
                Installed = true;
                log.LogInfo("[兼容] 已安装 PrefabDesc 数组扩容补丁（SpaceSector / PlanetFactory InitPrefabDescArray）");
            }
            catch (Exception ex)
            {
                log.LogError("[兼容] PrefabDesc 数组扩容补丁安装失败（模型 ID >= ~740 时 CommonAPI 初始化会中断）：" + ex);
            }
        }

        private static int callCount;

        /// <summary>按最大 proto ID 分配 PrefabDesc 数组（原实现只按 dataArray.Length+64）。</summary>
        internal static PrefabDesc[] BuildArray()
        {
            ModelProto[] dataArray = LDB.models.dataArray;
            int size = dataArray.Length + 64;
            int maxId = 0;
            for (int i = 0; i < dataArray.Length; i++)
            {
                ModelProto proto = dataArray[i];
                if (proto != null && proto.ID + 1 > size)
                {
                    size = proto.ID + 1;
                }
                if (proto != null && proto.ID > maxId)
                {
                    maxId = proto.ID;
                }
            }
            int call = ++callCount;
            int oldCapacity = dataArray.Length + 64;
            Plugin.Log.LogInfo(string.Format(
                "[兼容] PrefabDesc 数组第 {0} 次重建：models.dataArray.Length={1}，最大模型 ID={2}，原引擎容量={3}{4} → 扩容到 {5}",
                call, dataArray.Length, maxId, oldCapacity,
                maxId + 1 > oldCapacity ? "（原实现会越界 " + (maxId + 1 - oldCapacity) + " 项）" : "（未越界）",
                size));
            PrefabDesc[] array = new PrefabDesc[size];
            for (int i = 0; i < dataArray.Length; i++)
            {
                ModelProto proto = dataArray[i];
                if (proto != null && proto.ID >= 0 && proto.ID < array.Length)
                {
                    array[proto.ID] = proto.prefabDesc;
                }
            }
            return array;
        }
    }

    [HarmonyPatch(typeof(SpaceSector), "InitPrefabDescArray")]
    internal static class SpaceSectorPrefabDescArrayPatch
    {
        private static bool Prefix()
        {
            if (SpaceSector.PrefabDescByModelIndex != null)
            {
                return true;
            }
            try
            {
                SpaceSector.PrefabDescByModelIndex = PrefabDescArrayFix.BuildArray();
                return false;
            }
            catch
            {
                return true; // 构建失败时退回原实现（保持原行为）
            }
        }
    }

    [HarmonyPatch(typeof(PlanetFactory), "InitPrefabDescArray")]
    internal static class PlanetFactoryPrefabDescArrayPatch
    {
        private static bool Prefix()
        {
            if (PlanetFactory.PrefabDescByModelIndex != null)
            {
                return true;
            }
            try
            {
                PlanetFactory.PrefabDescByModelIndex = PrefabDescArrayFix.BuildArray();
                return false;
            }
            catch
            {
                return true;
            }
        }
    }
}
