using System;
using System.Collections.Generic;

namespace DSPPyPort.Core
{
    /// <summary>物品定义（items.json 一行）。</summary>
    internal sealed class ItemDef
    {
        internal int Id;
        internal string Key;
        internal string NameZh;
        internal string NameEn;
        internal string DescZh;
        internal string IconFile;
        internal string IconSource;
        internal int Stack;
        internal bool IsFluid;
        internal int ItemType;
        internal bool IsBuilding;
        internal string Source;
        internal int GridIndex;      // 0 = 自动分配
        internal string ModuleId;

        internal bool Skip;
        internal string SkipReason;

        /// <summary>本地化 key（缺省时由 id 派生）。</summary>
        internal string EffectiveKey
        {
            get { return string.IsNullOrEmpty(Key) ? "PY_Item_" + Id : Key; }
        }
    }

    /// <summary>自建建筑定义（buildings.json 一行）。</summary>
    internal sealed class BuildingDef
    {
        internal int ItemId;
        internal int ModelId;          // 0 = 自动分配
        internal int RecipeType;       // 自定义 ERecipeType byte（60-79）
        internal string CloneFrom;     // assembler / chemical / smelter / refinery
        internal int ClonePrefabModelId; // 可选：直接指定克隆源 model id
        internal double AssemblerSpeed;   // 负数 = 沿用克隆源
        internal long WorkEnergyPerTick;  // <=0 = 沿用克隆源
        internal long IdleEnergyPerTick;  // <=0 = 沿用克隆源
        internal int GridIndex;        // 物品栏格位，0 = 自动
        internal int BuildIndex;       // 建造栏位置 XYY，0 = 自动
        internal int PreTech;          // 解锁科技，0 = 无

        // 展示字段：缺省时从 items.json 同 item_id 的条目继承
        internal string Key;
        internal string NameZh;
        internal string NameEn;
        internal string DescZh;
        internal string IconFile;
        internal string IconSource;

        internal string ModuleId;
        internal bool Skip;
        internal string SkipReason;

        internal string EffectiveKey
        {
            get { return string.IsNullOrEmpty(Key) ? "PY_Building_" + ItemId : Key; }
        }
    }

    /// <summary>配方定义（recipes.json 一行）。</summary>
    internal sealed class RecipeDef
    {
        internal int Id;
        internal string Key;
        internal int Type;
        internal int TimeTicks;
        internal int[] Inputs;
        internal int[] InCounts;
        internal int[] Outputs;
        internal int[] OutCounts;
        internal string NameZh;
        internal string NameEn;
        internal string DescZh;
        internal int TechId;
        internal int GridIndex;        // 0/非法 = 自动补齐到 >=1101
        internal string IconFile;
        internal string IconSource;
        internal bool Handcraft;
        internal string ModuleId;
        internal bool Skip;
        internal string SkipReason;

        internal bool IsWide
        {
            get
            {
                return (Inputs != null && Inputs.Length > 6) ||
                       (Outputs != null && Outputs.Length > 2);
            }
        }

        internal string EffectiveKey
        {
            get { return string.IsNullOrEmpty(Key) ? "PY_Recipe_" + Id : Key; }
        }
    }

    /// <summary>科技定义（techs.json 一行）。</summary>
    internal sealed class TechDef
    {
        internal int Id;
        internal string Key;
        internal string NameZh;
        internal string NameEn;
        internal string DescZh;
        internal string ConclusionZh;
        internal string IconFile;
        internal string IconSource;
        internal int[] PreTechs;
        internal int[] Matrices;
        internal int[] MatrixPoints;
        internal long HashNeeded;
        internal int[] UnlockRecipes;
        internal float PosX;
        internal float PosY;
        internal bool HasPosition;
        internal bool Hidden;
        internal string ModuleId;
        internal bool Skip;
        internal string SkipReason;

        internal string EffectiveKey
        {
            get { return string.IsNullOrEmpty(Key) ? "PY_Tech_" + Id : Key; }
        }
    }

    /// <summary>一个已启用内容模块（manifest.modules[] 一项 + 其四个数据文件）。</summary>
    internal sealed class ModuleContent
    {
        internal string Id;
        internal string Name;
        internal string Dir;
        internal string Root;

        internal readonly List<ItemDef> Items = new List<ItemDef>();
        internal readonly List<BuildingDef> Buildings = new List<BuildingDef>();
        internal readonly List<RecipeDef> Recipes = new List<RecipeDef>();
        internal readonly List<TechDef> Techs = new List<TechDef>();

        /// <summary>content/&lt;module&gt;/_skipped.json 的条数（生成器主动跳过的条目）。</summary>
        internal int GeneratorSkipped;

        /// <summary>ID 冲突时整模块拒绝注册（不注册半组内容，避免存档污染）。</summary>
        internal bool Refused;
        internal string RefuseReason = string.Empty;

        /// <summary>加载期告警（汇总后统一打印，避免逐条刷屏）。</summary>
        internal readonly List<string> Warnings = new List<string>();

        internal void Warn(string message)
        {
            Warnings.Add(message);
        }
    }

    /// <summary>content/ 目录整体加载结果。</summary>
    internal sealed class LoadedContent
    {
        internal string Root;
        internal int ManifestVersion;
        internal readonly List<ModuleContent> Modules = new List<ModuleContent>();
        internal readonly List<string> DisabledModules = new List<string>();
        internal readonly Dictionary<string, string> LocaleZh = new Dictionary<string, string>(StringComparer.Ordinal);
        internal readonly Dictionary<string, string> LocaleEn = new Dictionary<string, string>(StringComparer.Ordinal);
        internal readonly List<string> Warnings = new List<string>();

        internal bool IsEmpty
        {
            get { return Modules.Count == 0; }
        }
    }
}
