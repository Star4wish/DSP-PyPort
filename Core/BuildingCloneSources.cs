using System;

namespace DSPPyPort.Core
{
    /// <summary>
    /// 建筑克隆源表（clone_from → 原版机器物品 ID）。
    /// 依据本机实测的原版建筑 ID（_analysis/audit-20261110/_vanilla_items_idmap.json）：
    ///   2303 制造台 Mk.I（model 65）/ 2309 化工厂（model 64）/
    ///   2315 位面熔炉（model 194）/ 2308 原油精炼厂（model 63）。
    /// 位面熔炉是工作区 DSP-MechDawn 实机验证过的克隆源（模型 194），接口/尺寸适合多输入机器。
    /// </summary>
    internal static class BuildingCloneSources
    {
        internal const int AssemblerSourceItemId = 2303;
        internal const int ChemicalSourceItemId = 2309;
        internal const int SmelterSourceItemId = 2315;
        internal const int RefinerySourceItemId = 2308;

        internal static bool TryResolve(string cloneFrom, out int sourceItemId, out string description)
        {
            sourceItemId = 0;
            description = string.Empty;
            if (string.IsNullOrEmpty(cloneFrom))
            {
                return false;
            }
            switch (cloneFrom.Trim().ToLowerInvariant())
            {
                case "assembler":
                    sourceItemId = AssemblerSourceItemId;
                    description = "制造台 Mk.I(2303/model 65)";
                    return true;
                case "chemical":
                    sourceItemId = ChemicalSourceItemId;
                    description = "化工厂(2309/model 64)";
                    return true;
                case "smelter":
                    sourceItemId = SmelterSourceItemId;
                    description = "位面熔炉(2315/model 194)";
                    return true;
                case "refinery":
                    sourceItemId = RefinerySourceItemId;
                    description = "原油精炼厂(2308/model 63)";
                    return true;
                default:
                    description = cloneFrom;
                    return false;
            }
        }
    }
}
