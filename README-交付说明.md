# DSP-PyPort 交付说明（BepInEx 内容注册引擎）

> 版本 0.2.0（2026-11-17）：已接入 2 个内容包 —— **pyrawores（原矿）+ pycoalprocessing（煤处理）**。
> 版本 0.1.0（2026-11-17）：引擎 + pyrawores 首包。
> 契约：`_analysis/py-port/docs/content-schema.md`；实现手册：`_analysis/py-port/docs/dsp-impl-patterns.md`。

## 1. 产物与路径

| 内容 | 路径 |
|---|---|
| 工程源码 | `C:\WorkWork\DSP MOD\DSP-PyPort\`（`DSP-PyPort.csproj` / `Plugin.cs` / `Core\*.cs` / `build.ps1`） |
| 冒烟/开发测试内容（手写最小包，随工程保留） | `DSP-PyPort\content\`（1 物品 + 1 液体 + 1 建筑 + 1 科技 + 2 配方，含 1 张 PNG 图标） |
| 部署产物 | `C:\Games\Steam\steamapps\common\Dyson Sphere Program\BepInEx\plugins\DSP-PyPort\DSP-PyPort.dll` + `content\` |
| 实机日志快照 | `DSP-PyPort\logs\final-20261117-001307.log`（最终真实内容验证）、`logs\diag-*.log`（占用诊断）、`logs\smoke2-*.log`（最小 smoke） |
| 主菜单截图 | `DSP-PyPort\logs\menu-20261117-001612-1.png`（游戏正常到主菜单、无报错弹窗） |

插件 GUID：`org.pyport.dsp`；显示名 `DSP-PyPort`；目标框架 net472。
依赖：CommonAPI 1.6.7（硬）、LDBTool 3.0.3（硬）、DSP-RecipeSlots 1.2.0（软，缺失时明确报错并降级）。

**工程本身不含任何 PY 内容**：改内容只改 `content/` JSON，不需要重编译。

## 2. 构建与部署（幂等）

```powershell
# 构建 + 部署（默认内容：工程内冒烟 content）
powershell -NoProfile -ExecutionPolicy Bypass -File "C:\WorkWork\DSP MOD\DSP-PyPort\build.ps1"

# 接入生成器真实内容（推荐用法）
powershell -NoProfile -ExecutionPolicy Bypass -File "C:\WorkWork\DSP MOD\DSP-PyPort\build.ps1" `
    -ContentDir "C:\WorkWork\DSP MOD\_analysis\py-port\content"

# 只构建不部署
... build.ps1 -NoDeploy

# 非默认 Steam 库/游戏目录
... build.ps1 -GameDir "D:\Steam\steamapps\common\Dyson Sphere Program"
```

脚本行为：`dotnet build -c Release -p:DSPGameDir=...` → 校验 DLL → 拷贝 DLL/内容到
`<GameDir>\BepInEx\plugins\DSP-PyPort\`（先删旧 `content\` 再整体拷贝，天然幂等）。
部署前必须先退出游戏（DLL 被进程映射时 Windows 会拒绝覆盖，脚本会给出明确报错）。

直接构建：`dotnet build DSP-PyPort\DSP-PyPort.csproj -c Release --nologo`。

## 3. 引擎实现（契约 → 行为）

启动时（`Plugin.Awake`，全部在 `ProtoRegistry.StartModLoad` 块内，顺序 = 手册 §1）：

1. **前置探测**：CommonAPI / LDBTool / DSP-RecipeSlots 版本，缺失打明确 Error（RecipeSlots 缺失时仍加载，但提示宽配方会被静默截断）。
2. **载入 content**：`content/manifest.json` 的 `modules[]`（`enabled=false` 整模块跳过）→ 各模块
   `items/buildings/recipes/techs.json` + `content/locale/{zh-CN,en-US}.json`；JSON 用自带的零依赖解析器（带行列号报错）。
3. **注册前 ID 冲突扫描**（见 §4）：items / recipes / techs / models 四类占用段 + 计划段 + 空闲段全部打印；有冲突 → **拒绝整个模块**（不注册半组，避免悬空引用与存档污染）。
4. **注册**：物品（`is_fluid=true` → `IsFluid=true`；`source=mapped` 不注册；`is_building=true` 由 buildings.json 注册）→ 自建建筑（clone PrefabPath → `RegisterModel` → `RegisterItem(Production)` → `AddModelToItemProto`）→ 配方（任意长度 inputs/outputs、`GridIndex<1101` 自动补到 5000+、自定义 `ERecipeType` 用强转重载）→ 科技（`IsLabTech=true`、位置取 pos_x/pos_y）→ 本地化。
5. **收尾**（`onLoadingFinished`）：写 `prefabDesc.assemblerRecipeType`(60-79)/速度/耗电、回填配方标志、配方↔科技解锁闭环、PNG 图标反射写 `_iconSprite`、CommonAPI `itemIconDescs` 清理（防 IconSet 越界）、自检与联调验证输出。
6. **Update 探针**：等 LDBTool `EditDataAction` 之后再回填一次（防覆盖）；若 `onLoadingFinished` 因其它 MOD 异常未触发，用 LDB 就绪探针兜底执行收尾。

细节约定：

- 本地化 key：物品/建筑 `{key}_Name/_Desc`、配方 `{key}_Name/_Desc`、科技 `{key}_Name/_Desc/_Conclusion`；
  `key` 缺省时按 `PY_Item_{id}` / `PY_Building_{item_id}` / `PY_Recipe_{id}` / `PY_Tech_{id}` 派生。
  内联 `name_zh/name_en` 先登记，`content/locale/*.json` 覆盖；英文缺失回退中文。
- 物品栏格位：items.json 无 `grid_index`，MOD 自动分配（2026-11-17 重做，详见 §14）：
  `GridIndex = tab*1000 + (row+1)*100 + (col+1)`；在 tab1-5 的 8×14 合法格中确定性分配（优先 tab4/5，再 tab1/2/3），
  注册前扫描 LDB 物品 + `LDBTool.CustomGridIndex.cfg`（跳过本 MOD 历史的 `PY_` 记录）并预留建筑格位（5011-5025 等）；
  tab1-5 容量用尽后确定性降级到 tab6+ 顺序格（筛选器网格不渲染，但不再制造重复格），并汇总统计。
- `clone_from` 映射（本机原版实测 ID）：`assembler`→2303 制造台 Mk.I（model 65）、`chemical`→2309 化工厂（model 64）、`smelter`→2315 位面熔炉（model 194，工作区实机验证过的克隆源）、`refinery`→2308 原油精炼厂（model 63）。也可用 `clone_prefab_model_id` 直接指定。
- 建造索引：`build_index` 缺省自动 900+（XYY 格式，页号 9）。
- 图标：`icon_file` → `content/icons/<file>` PNG（80×80 RGBA）→ `Texture2D.LoadImage` + `Sprite.Create` → 反射写
  `ItemProto/TechProto/RecipeProto._iconSprite`；配方无图标时跟随产物。PNG 失败/缺图只降级为原版近似图标，不阻断。

## 4. 【重要】原版升级页科技 ID 的真实占用范围

手册 §6.2 写「原版科技 ID 全部 < 2000」——**在本机游戏 0.10.35 上不成立**。
启动期扫描（`[冲突检查] 注册前 LDB 已有 proto`）实测 vanilla 共 **315 条科技**，其中升级页（ID ≥ 2000）占用到 **6106**：

```
1901-1904, 2101-2106, 2201-2208, 2301-2307, 2401-2407, 2501-2506, 2601-2606,
2701-2706, 2801-2807, 2901-2906, 2951-2956, 3101-3106, 3201-3208, 3301-3306,
3311-3316, 3401-3407, 3501-3510, 3601-3606, 3701-3706, 3801-3803, 3901-3904,
4001-4005, 4101-4104, 4201-4206, 5001-5006, 5101-5106, 5201-5206, 5301-5305,
5401-5405, 5601-5605, 5701-5705, 5801-5807, 5901-5907, 6001-6006, 6101-6106
```

（`2101-2106` 的「T机甲核心」等就是原版升级科技，不是其它 MOD；曾导致 LDB 科技重复 ID 与 UXAssist 重复键崩溃。）

2000-11999 内的空闲科技段（按长度降序，运行期实测）：
`6107-11999（长 5893）`、`4207-5000（长 794）`、`5406-5600（长 195）`、`2957-3100（长 144）`、`2000-2100（长 101）`…

**当前 pyrawores 实际使用 `4207-4318`（112 条）**；后续包建议从 4320 起、每包 +120，且每包接入前先看启动日志的
`[冲突检查] 已占用科技段 / 空闲段` 输出重新确认（不要照抄旧结论）。

## 5. 【重要】PrefabDesc 数组扩容补丁（`Core\PrefabDescArrayFix.cs`）

- **问题**：CommonAPI 在 `onLoadingFinished` 之前调用引擎的 `SpaceSector.InitPrefabDescArray()` /
  `PlanetFactory.InitPrefabDescArray()`，这两个方法按 `LDB.models.dataArray.Length + 64` 分配数组，
  却按 `dataArray[i].ID` 写入。LDBTool 把 MOD 模型**紧凑追加**到 `dataArray` 末尾（索引≠ID），
  所以模型 ID 超过「原版模型数 + MOD 模型数 + 64」时必现 `IndexOutOfRangeException`。
  本机实测：`dataArray.Length=683`（含 MOD 模型）、最大模型 ID=807、原容量 747 → **原实现会越界 61 项**。
  后果不是崩溃退出，而是 CommonAPI `OnPostAdd` 整体中断 → `onLoadingFinished` 不触发 →
  **所有 MOD 的建筑 recipeType / 配方标志 / 自定义图标 / 解锁闭环全部静默失效**（疑似随机故障，极难排查）。
- **补丁**：Harmony 前缀替换两个引擎方法，改为按「最大模型 ID + 1」分配、跳过原方法；只扩大数组，不改变任何语义。
  每次重建都会打印 `[兼容] PrefabDesc 数组第 N 次重建：… 原引擎容量=… → 扩容到 …`，便于升级后复核。
- **风险**：属于对引擎静态方法的防御性修改；游戏或 CommonAPI 版本升级若改变方法签名/调用时机，补丁可能安装失败或失效
  （失败会打 Error 并回调原实现）。已提供配置开关 `BepInEx\config\org.pyport.dsp.cfg` 的
  `[Compat] FixPrefabDescArray = true`，排查冲突时可临时关闭。**升级游戏后请确认日志里仍有该补丁与「扩容」行，否则需要重做适配。**

## 6. 实机验证结果（最终真实内容，2026-11-17）

### 6.1 最新一次复验（2026-11-17 00:35，ID 缓存清理后）

环境说明：本次启动时已装 MOD 与上一轮不同（`DSP-MechDawn` 已移除，`ProjectOrbitalRing 1.0.22` 已装入），
因此 LDB 总量与前次不同属正常；关键是**本 MOD 的注册与 ID 完全正确**：

```
[冲突检查] 计划 ID 与已有 proto 无冲突，继续注册
[PyPort] 注册汇总：物品 613（液体 211）/ 建筑 8 / 配方 559（宽配方 88）/ 科技 112 / 本地化 2696
[PyPort] ID 使用：物品 6300-6920（621 个）/ 模型 800-807（8 个）/ 配方 4000-4558（559 个）/ 科技 4207-4318（112 个）
[PyPort] 加载完成自检：LDB 物品 799 / 配方 723 / 科技 428 / 模型 674；缺失 物品 0 / 配方 0 / 科技 0；ID 被 LDBTool 改写 0
[自检] LDB.物品 / 配方 / 科技 / 模型 均无重复 ID
```

### 6.2 上一轮完整验证（2026-11-17 00:13，MechDawn 环境）

日志 `logs\final-20261117-001307.log`。关键行：

```
[冲突检查] 注册前 LDB 已有 proto：物品 175 / 配方 162 / 科技 315 / 模型 664
[冲突检查] 内容计划段：物品 6300-6912（613 个）/ 配方 4000-4558（559 个）/ 科技 4207-4318（112 个）/ 模型 800-807（8 个）
[冲突检查] 计划 ID 与已有 proto 无冲突，继续注册
[PyPort] 物品注册：613/613（液体 211 个，mapped 跳过 17，建筑物品条目 8 交给建筑注册）
[PyPort] 建筑注册：8/8（模型 800–999 段，自动分配 model_id 0 个；建造索引从 900 起）
[PyPort] 配方注册：559/559（宽配方 >6 原料或 >2 产物：88 条）
[PyPort] 科技注册：112/112
[PyPort] 本地化注册：2696 条
[PyPort] 注册汇总：物品 613（液体 211）/ 建筑 8 / 配方 559（宽配方 88）/ 科技 112 / 本地化 2696
        / 无效跳过 17 条 / 生成器标记跳过降级 3802 条
[PyPort] ID 使用：物品 6300-6920（621 个）/ 模型 800-807（8 个）/ 配方 4000-4558（559 个）/ 科技 4207-4318（112 个）
[图标] 自定义图标已应用：物品 621 / 科技 112 / 配方 0（跟随产物 559）；缺图 0 个
[验证] 建筑 8 座：assemblerRecipeType 60→67 各 1 座；CanBuild=True BuildIndex=900-907 GridIndex=5011-5018
[验证] 指定宽配方 4009：Items.Length=12（期望 12，未被截断）
[验证] 宽配方统计：>6 原料 51 条，>2 产物 37 条（注册口径 88 条）
[验证] 科技 IsLabTech=true：112/112；ID>=2000（升级页 page=1）：112/112
[自检] LDB.物品/配方/科技/模型 均无重复 ID
[PyPort] onLoadingFinished 收尾完成 … / LDBTool EditDataAction 之后回填完成（Update 探针，建筑 prefabDesc 8/8）
[兼容] PrefabDesc 数组第 3/4 次重建：dataArray.Length=683，最大模型 ID=807，原引擎容量=747（原实现会越界 61 项）→ 扩容到 808
```

- 无 `Error initializing proto data`、无 LDB 重复 ID、无缺失引用；与 DSP-PyPort 相关的 Error/WARN 为 0。
- 日志中仍有少量**其它 MOD 的**既有告警（BepInEx 版本提示、BuildBarTool 依赖缺失、Icarus II sidecar 端口占用等），与本次内容注册无关。
- 主菜单截图 `logs\menu-20261117-001612-1.png`：游戏正常启动到主菜单，无报错弹窗。
- 说明：真正的「科技页/建造菜单/配方选择器」属于进档后的 UI，本轮按任务约束未进入存档、未点击游戏内按钮；
  上述可见性用 LDB 反射查询验证（配方可见性判据 = `recipe.Type == prefabDesc.assemblerRecipeType`）。
  另：本机 Mechanical Dawn 的 DevAutoLoad 会在主菜单 8 秒后自动尝试读档 `_lastexit_`（外部 MOD 的 dev 配置），
  截图如看到读档失败弹窗属该 MOD 行为，与本插件无关。

## 6.5 【重要】LDBTool ID 缓存与「发布后不敢改 ID」问题

**问题本质（源码确认，`LDBTool-class.cs::IdBind`）**：LDBTool 3.0.3 **没有任何配置项可以关闭 ID 缓存**。
它对每个 proto 执行
`CustomID.Bind<int>(protoName, proto.Name, proto.ID)` 然后 `proto.ID = cachedValue`：

- 缓存文件：`BepInEx\config\LDBTool\LDBTool.CustomID.cfg`（ID）与 `LDBTool.CustomGridIndex.cfg`（格位）；
- 首次运行写入的是 MOD 请求的 ID；**之后任何人手改/程序改 content 里的 ID，都会被缓存值覆盖**；
- 每次启动它都会把当前全部 proto 重写进缓存（实测启动一次回写 1292 条我们的条目），所以「清缓存」必须发生在**启动前**。

**本项目的解决方式（已自动化，不需要手改 ID）**：

1. 新增 `tools\clean-ldbtool-cache.ps1`：只删除**我们自己的**条目
   （`PY_*` 键 + 落在我们号段内的 Model/Item/Recipe 数字键 + 对应 GridIndex 块），
   其它 MOD 的缓存原样保留；删除前自动备份 `*.bak-时间戳`。
2. `build.ps1` 每次部署后**自动调用**它（幂等；`-SkipIdCacheClean` 可跳过）。
3. 因此正确流程是：

```powershell
# 改了 content（ID 可能变化）→ 必须走 build.ps1 部署，它会先清我们的缓存
powershell -NoProfile -ExecutionPolicy Bypass -File "C:\WorkWork\DSP MOD\DSP-PyPort\build.ps1" `
    -ContentDir "C:\WorkWork\DSP MOD\_analysis\py-port\content"
# 然后启动游戏：日志应出现「计划 ID 与已有 proto 无冲突」+「ID 被 LDBTool 改写 0」
```

4. 万一 ID 又被旧缓存覆盖（日志出现 `ID 被 LDBTool 改写 N>0`）：先关游戏，单独跑一次清理器，再启动：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "C:\WorkWork\DSP MOD\DSP-PyPort\tools\clean-ldbtool-cache.ps1" `
    -ContentDir "C:\WorkWork\DSP MOD\_analysis\py-port\content"
```

**为什么不做成「全局关闭 LDBTool 缓存」**：LDBTool 没有开关；强行删整个缓存文件或把文件设只读会影响其它 MOD
（机械黎明/光子交易所等依赖缓存保证存档兼容），且 LDBTool 下次启动仍会重写。定向清理是唯一既安全又等效的做法。

**清理实测（2026-11-17 00:33）**：CustomID 删除 1417 条（含历史遗留的旧科技段 2xxx/5xxx 条目 224 条 + 物品 624 + 配方 561 + 模型 8），
GridIndex 删除 1180 块；备份 `LDBTool.CustomID.cfg.bak-20261117-003300`、`LDBTool.CustomGridIndex.cfg.bak-20261117-003301`；
清理后启动验证 = ID 与 content 完全一致、LDBTool 改写 0、无重复 ID。

## 7. 后续 8 个包接入步骤

1. 生成器按契约输出 `_analysis/py-port/content/<module>/{items,buildings,recipes,techs}.json` + `locale`/`icons`，并更新 `manifest.json`（新模块 `enabled=true`）。
2. 规划 ID 段（接入前必须用启动日志的 `[冲突检查]` 复核，勿照抄旧文档）：
   - 物品：pyrawores 6300-7299，后续包每包 +500（如 pycoalprocessing 7300-7799）；
   - 配方：pyrawores 4000-4599，后续包每包 +600（4600 起）；GridIndex 每包一段 5xxx；
   - 科技：pyrawores 4207-4318，后续包从 4320 起每包 +120（4320-4439、4440-4559…），全部 ≥2000 自动进升级页；
   - 模型：pyrawores 800-807，后续包每包 +10（808 起），上限 2047；
   - 自定义配方类型：pyrawores 60-67，后续包 68+（79 封顶，超过需与其它 MOD 协调）。
3. `build.ps1 -ContentDir "_analysis\py-port\content"` 部署；确认 `content\manifest.json` 里目标模块 enabled。
4. 启动游戏看日志：`[冲突检查]` 无冲突 → `模块 <id> 注册计数` → `加载完成自检` 缺失 0 → `LDB.* 无重复 ID`。
5. 宽配方（>6 原料 / >2 产物）：必须有 DSP-RecipeSlots 1.2.0+（缺失时本插件明确报错，配方会静默截断）。
6. 新矩阵（>6 种研究材料）：需玩家侧配置 `BepInEx\config\star4wish.dsp.recipeslots.cfg` 的 `Lab.ExtraMatrixIds`；本插件会在科技矩阵缺失时告警。
7. 建筑外观 = 克隆源外观（制造台/化工厂/位面熔炉/原油精炼厂）；要独占外观需 Unity AssetBundle（超出本轮）。

## 8. 已知限制 / 未验证

- 未进存档做 UI 点击级验证（任务约束），科技树节点位置是否与其它 MOD 重叠、配方选择器实际显示需进档人工确认一次。
- 未验证 >6 种研究材料（首期 112 条科技矩阵均为 6001-6006 子集）。
- 未验证真实生产链运行（投料/产出/宽配方 RecipeSlots 全链路），建议进档后跑通一条「原矿→破碎→选矿→熔炼」。
- 物品栏筛选器 UI 容量限制（v0.6 起显著）：tab1-5 每 tab 只有 8×14=112 格、合计 560 格；扣除原版/其它 MOD/建筑后
  六包合集只剩 **386 格可进筛选器**，而注册物品 1593 个 → **1207 个物品被确定性放到 tab6+**（筛选器网格不渲染，
  仍可通过物品选择器/配方产物/建造栏访问）。这是大规模 overhaul 的已知限制，不是重复格位。
- 配方/科技/物品 ID 发布后不要改（LDBTool 会按 proto.Name 持久化 ID 到 `LDBTool.CustomID.cfg`）；本插件检测到 ID 被改写会告警。

## 9. 故障排查

```powershell
$log='C:\Games\Steam\steamapps\common\Dyson Sphere Program\BepInEx\LogOutput.log'
Select-String -Path $log -Pattern 'PyPort|冲突检查|验证|自检|Error initializing proto data|不能大于|已被占用'
```


---

## 10. v0.2.0：两包接入（pyrawores + pycoalprocessing）

### 10.1 规模（content/ 1134 文件：16 JSON + 1115 PNG）

| 项 | pyrawores（原矿） | pycoalprocessing（煤处理） | 合计 |
|---|---:|---:|---:|
| 物品行（注册） | 567（550） | 485（485） | 1035 注册 + 17 mapped |
| 其中流体（液体物品） | 211 | 132 | 247 注册（另有 mapped 液体） |
| 其中占位物品 | 171 | 132 | 303 |
| 建筑 | 8 | 5 | **13** |
| 配方 | 559 | 538 | **1097** |
| 科技 | 112 | 41 | **153** |
| 模型段 | 800-807 | 808-812 | 800-812 |
| 配方类型 | 60-67 | 68-72 | 60-72 |

煤处理新增 5 座建筑：**68 焦化炉 / 69 生物农场 / 70 精炼塔 / 71 采掘机 / 72 研究枢纽**（grid 5019-5023）。
跨包占位转正 **71 条**（物品 ID 全部沿用），pyrawores 占位 242 → 171。

### 10.2 实机验证（2026-11-17 01:24，日志 logs/final-2pkg-20261117-012445.log）

`
[冲突检查] 内容计划段：物品 6300-7708（1022）/ 配方 4000-5137（1097）/ 科技 4207-4360（153）/ 模型 800-812（13）
[冲突检查] 计划 ID 与已有 proto 无冲突，继续注册
物品注册：1022/1022（液体 247，mapped 跳过 17，建筑物品 13 交给建筑注册）
建筑注册：13/13   配方注册：1097/1097（宽配方 236）   科技注册：153/153   本地化 4723 条
加载完成自检：LDB 物品 1213 / 配方 1261 / 科技 469 / 模型 679；缺失 物品 0 / 配方 0 / 科技 0；ID 被 LDBTool 改写 0
[自检] LDB.物品/配方/科技/模型 均无重复 ID
引用自检：配方引用缺失物品 0 处 / 科技矩阵缺失 0 处 / 自定义配方类型无对应建筑 0 条
`

13 座建筑可见性（
ecipe.Type == prefabDesc.assemblerRecipeType 实测配方数）：
60:67 61:74 62:138 63:84 64:27 65:27 66:34 67:238 68:31 69:60 70:24 71:12 72:9，全部 CanBuild=True、BuildIndex 900-912。
宽配方 236 条（>6 原料 162 / >2 产物 74）；最宽 20 进（recipe 4677），最多产物 6 出（recipe 5047）。

### 10.3 待实机确认的 3 点（本轮只验证到 LDB 层）

1. **配方选择器第 6 页**：煤处理配方 GridIndex 用 page 6（6001-6999），首次使用，需进档确认可见（引擎只保证 ≥1101 不被隐藏）。
2. **67 兜底建筑跨包依赖**：煤处理有 225 条配方落 67（通用工厂，属 pyrawores 包）→ 两包必须同时启用；只启用 p2 时这些配方无对应建筑。
3. **0 原料配方**：moondrop-co2（recipe 4927，type 69）无输入，需确认机器持续产出行为符合预期。

### 10.4 号段总表（v0.2）

| 类型 | 已占（原版+其它MOD实测） | 本 MOD 使用 |
|---|---|---|
| 物品 | 原版 175 项；其它 MOD 6007-6197 | 6300-7713 |
| 配方 | 原版 1-162 | 4000-5137 |
| 科技 | 原版 315 项、升级页最高 6106 | 4207-4360 |
| 模型 | 原版 1-665 | 800-812 |
| 自定义配方类型 | POR 9-19 / MechDawn 20-25 | 60-72（后续 73+，79 封顶） |

> 注意：本机 **ProjectOrbitalRing 主插件已不在加载列表**（仅残留 preloader）；若日后装回 POR，其物品号段 6201-9486 会与我们的 6300-8499 重叠 —— 启动时 [冲突检查] 会拒绝注册并报冲突 ID，届时应把我们的物品段整体上移到 9500-11999。


---

## 11. v0.3.0：第 3 包 pyindustry（工业）接入（2026-11-17）

### 11.1 三包规模

| 包 | 注册物品 | 建筑 | 配方 | 科技 | 号段（物品/配方/科技） |
|---|---:|---:|---:|---:|---|
| pyrawores 原矿 | 550（+17 mapped） | 8 | 559 | 112 | 6300-6920 / 4000-4558 / 4207-4318 |
| pycoalprocessing 煤处理 | 484 | 5 | 538 | 41 | 6670-7713 / 4600-5137 / 4320-4360 |
| pyindustry 工业 | 84 | 0 | 71 | 15 | 7670-8582 / 5200-5270 / 4440-4454 |
| **合计** | **1118** | **13** | **1168** | **168** | 物品 6300-9199 / 配方 4000-5799 / 科技 4207-4559 |

配方类型分布（全局）：Assemble 4=239、Smelt 1=19、PY 冶金炉 62=142、反应釜 63=85、蒸馏塔 64=27、洗涤塔 65=27、特殊 66=34、通用 67=239、焦化炉 68=31、生物农场 69=60、精炼塔 70=25、采掘机 71=12、研究枢纽 72=9。

### 11.2 跨包占位转正

- 第 2 包：pyrawores → pycoalprocessing 迁移 **71 条**（ID 全部沿用）。
- 第 3 包：pycoalprocessing → pyindustry 迁移 **1 条**（py-iron-oxide id=7670）。
- 迁移合计 **72 条，ID 变化 0**；占位物品：242+132 → 171+131+14（合计 316）。
- 基线文件：content/_global/id-baseline-{pyrawores,pycoalprocessing,pyindustry}.json，全局自检断言「既有包 ID 逐个不变」。

### 11.3 实机验证（2026-11-17 01:44）

`
物品注册 1105/1105（液体 248）+ 建筑物品 13    建筑 13/13
配方 1168/1168（宽配方 248）                  科技 168/168   本地化 5076
LDB：物品 1296 / 配方 1332 / 科技 484 / 模型 679
缺失 0 / 重复 ID 0 / ID 被改写 0 / 引用缺失 0 / 自定义类型无建筑 0
`
- pyindustry 配方页 page 7（7001-7400），与 p1/p2 的 page 5/6 分离。
- pyindustry 不新增建筑：配方沿用 62/63/70/67 → **三包需同时启用**（仅启用 p3 时 7 条配方无对应建筑）。
- 日志快照：DSP-PyPort/logs/final-3pkg-*.log。

### 11.4 后续包号段规划（实测复核后再用）

物品：p4 9200+（每包 900）；配方：p4 5800+（每包 600，溢出用 9000-11999）；科技：p4 4560+（每包 120）；模型：p4 813+。
注意：每包接入前必须看启动日志 [冲突检查] 的空闲段输出，不要照抄本文档。


---

## 12. v0.4.0：第 4 包 pypetroleumhandling（石油化工）接入（2026-11-17）

### 12.1 四包规模

| 包 | 注册物品 | 建筑 | 配方 | 科技 | 号段（物品/配方/科技） |
|---|---:|---:|---:|---:|---|
| pyrawores 原矿 | 528（+17 mapped） | 8 | 559 | 112 | 6300-6920 / 4000-4558 / 4207-4318 |
| pycoalprocessing 煤处理 | 468 | 5 | 538 | 41 | 6670-7713 / 4600-5137 / 4320-4360 |
| pyindustry 工业 | 84 | 0 | 71 | 15 | 7670-8582 / 5200-5270 / 4440-4454 |
| pypetroleumhandling 石油 | 160 | 0 | 200 | 34 | 9200-9321 / 5800-5999 / 4560-4593 |
| **合计** | **1240** | **13** | **1368** | **202** | 物品 6300-10199 / 配方 4000-6399 / 科技 4207-4679 |

石油包类别扩展（不新建建筑）：racking/coalbed/tholin-atm→71 采掘机、
he/kerogen→70 精炼塔、cridren→69 生物农场；**67 兜底降为 0**。

### 12.2 占位物品迁移（累计 106 条，ID 全部沿用）

- 本轮 p4 收到既有定义 38 条：占位迁入 35（p1 23 + p2 12）+ canonical 收回 4（carbolic-oil/naphtha/oil-sand/soot，从 p2 回归 p4）。
- 累计迁移 106 条；占位物品 374 → **295**；前三包既有 ID 逐条差异 **0**。

### 12.3 实机验证（2026-11-17 01:57）

`
物品注册 1227/1227（液体 274）+ 建筑物品 13    建筑 13/13
配方 1368/1368（宽配方 324）                  科技 202/202   本地化 5822
LDB：物品 1418 / 配方 1532 / 科技 518 / 模型 679
缺失 0 / 重复 ID 0 / ID 被改写 0 / 引用缺失 0 / 自定义类型无建筑 0
`
- p4 配方页 page 8（8001-8940）。
- 0 原料配方累计 3 条（moondrop-co2 + p4 的 5921/5999），进档时留意机器空输入行为。

### 12.4 剩余 4 包号段规划（实测复核后再用）

物品：p5 10200+；配方：p5 6400+（溢出用 9000-11999 前先查占用）；科技：p5 4680+；模型：p5 813+。


---

## 13. v0.5.0：第 5 包 pyhightech（高科技）+ 2 座新建筑（2026-11-17）

### 13.1 五包规模

| 包 | 注册物品 | 建筑 | 配方 | 科技 |
|---|---:|---:|---:|---:|
| pyrawores 原矿 | 475（+17 mapped） | 8 | 559 | 112 |
| pycoalprocessing 煤处理 | 440 | 5 | 538 | 41 |
| pyindustry 工业 | 81 | 0 | 71 | 15 |
| pypetroleumhandling 石油 | 158 | 0 | 200 | 34 |
| pyhightech 高科技 | 300 | 2 | 344 | 48 |
| **合计** | **1454** | **15** | **1712** | **250** |

### 13.2 建筑总表（15 座，自定义类型 60–74）

| 类型 | 建筑 | 模型 | 可见配方数 |
|---|---|---:|---:|
| 60 | PY 破碎机 | 800 | 67 |
| 61 | PY 选矿机 | 801 | 74 |
| 62 | PY 冶金炉 | 802 | 166 |
| 63 | PY 反应釜 | 803 | 162 |
| 64 | PY 蒸馏塔 | 804 | 72 |
| 65 | PY 洗涤塔 | 805 | 38 |
| 66 | PY 特殊工厂 | 806 | 69 |
| 67 | PY 通用工厂 | 807 | 239 |
| 68 | PY 焦化炉 | 808 | 37 |
| 69 | PY 生物农场 | 809 | 90 |
| 70 | PY 精炼塔 | 810 | 72 |
| 71 | PY 采掘机 | 811 | 32 |
| 72 | PY 研究枢纽 | 812 | 30 |
| **73** | **PY 粒子加速器** | **813** | **30** |
| **74** | **PY 电子厂** | **814** | **32** |

### 13.3 实机验证（2026-11-17 02:10）

`
物品注册 1439/1439 + 建筑物品 15    建筑 15/15（CanBuild=True，BuildIndex 900-914，GridIndex 5011-5025）
配方 1712/1712（宽配方 401）        科技 250/250    本地化 6628
LDB：物品 1632 / 配方 1876 / 科技 566 / 模型 681
缺失 0 / 重复 ID 0 / ID 被改写 0 / 引用缺失 0 / 自定义类型无建筑 0
`

### 13.4 占位迁移（累计 184 条，ID 零变化）

- p5 接管既有定义 86 条：占位迁入 82（p1 57 / p2 20 / p3 3 / p4 2）+ canonical 收回 8（p2→p5）。
- 占位总计 374 → **275**；前四包既有 ID 逐条差异 **0**。

### 13.5 剩余 3 包号段规划

物品：p6 11100+、p7 11600+、p8 需用空闲回填（当前已用 6300-10413，剩余 10414-11999 足够）；配方：p6 7000+（溢出 9000-11999 先查占用）；科技：p6 4800+；模型：p6 815+。


---

## 14. v0.6.0：第 6 包 pyfusionenergy 接入 + 【重要】物品 GridIndex 分配器重做（2026-11-17）

### 14.1 六包规模与验证（2026-11-17 02:28，日志 `logs/grid-20261117-022835.log`）

| 项 | 数值 |
|---|---:|
| 注册物品（不含建筑物品） | **1593**（液体 337，mapped 跳过 17，建筑物品 15 交给建筑注册） |
| 建筑 | **15/15**（CanBuild=True，BuildIndex 900-914，GridIndex 5011-5025） |
| 配方 | **1957/1957**（宽配方 478） |
| 科技 | **275/275**（IsLabTech 全 true，ID 全部 ≥2000 → 升级页） |
| 本地化 | 7955 条 |
| ID 段 | 物品 6300-11253 / 模型 800-814 / 配方 4000-7244 / 科技 4207-4824 |
| 自检 | 物品/配方/科技/模型 LDB 均**无重复 ID**；缺失 0；引用缺失 0；ID 被 LDBTool 改写 0 |

### 14.2 问题（task-10）

旧分配器从 4001 顺序 `+1`：六包 1593 个物品一路排到 ~5593，与建筑格位 5011-5025 **同 tab 同格位重复**，
且大量落到 tab4 之外（`[Warning] 物品栏自动分配已越过 tab4 区间`）。反编译 `full.cs` 确认的 GridIndex 语义：

- 编码 `grid = tab*1000 + (row+1)*100 + (col+1)`；`grid < 1101` 视为不参与这些 UI。
- 3 个消费点：L426447（物品筛选器 RefreshIcons）、L429210（物品选择器，tab=1/2）、L434479（配方选择器）。
- 物品筛选器网格 row 0-7、col 0-13（每 tab 112 格；选择器 type2 时 row 0-8），**只渲染 `tab == currentType`**；
  原版 175 个物品的格位分布 tab1=40 / tab2=52 / tab3=23 / tab4=18 / tab5=32（tab1-5 都有原版物品）。

### 14.3 新分配策略（`Core\Registrar.cs` 的 `InitItemGridAllocation / NextVisibleCell / NextOverflowCell`）

1. **注册前占用扫描**：`LDB.items.dataArray` 现有 GridIndex（实测 174 格）+ `LDBTool.CustomGridIndex.cfg`
   （本次经 build.ps1 缓存清理后为 0 条；解析时仍**跳过 `PY_` 前缀的本 MOD 历史记录**，防止旧分配污染）
   + 建筑保留格（buildings.json 的 15 个 grid_index，重复会报 Error）。
2. **tab1-5 合法格确定性分配**：优先级 `tab4 → tab5 → tab1 → tab2 → tab3`，每 tab 行 0-7、列 0-13，跳过已占格；
   同包内按注册顺序。实测 tab1-5 空闲 **386 格**。
3. **容量耗尽确定性降级**：继续按 tab6、7、…（同样 8×14 格式，跳过已占格，cap tab59）顺序分配，
   保证「全局唯一、不重复、不与建筑交叠」；实测降级 **1207** 个，兜底 0 个。
4. **交叉校验断言**（启动日志）：
   `[验证] GridIndex 交叉校验：PY 物品格重复 0 个；物品∩建筑 0 个；建筑格重复 0 个 → PASS`。
5. **LDBTool 覆盖回填**：LDBTool 在 `PreAddProto` 时用 CustomGridIndex.cfg 旧值覆盖 GridIndex，
   收尾阶段（onLoadingFinished / Update 探针）把分配值写回；本次因缓存已清，回填 0 个。

### 14.4 启动验证关键日志

```
[GridIndex] 占用扫描：LDB 物品 174 格 + LDBTool 登记 0 格（跳过本 MOD 历史 PY_ 记录 0 条）
            + 建筑预留 15 格（其中建筑自动补格 0 座）；tab1-5 合法格 560，当前空闲 386 格；超出后降级 tab6+
[GridIndex] 分配完成：PY 物品 1593 个（tab1-5 可见 386，tab6+ 降级 1207，兜底 0）；
            建筑 15 座（JSON 固定预留 15 / 自动补格 0）；tab1-5 空闲容量 386 格
[验证] GridIndex 交叉校验：PY 物品格重复 0 个；物品∩建筑 0 个；建筑格重复 0 个 → PASS
[Warning:DSP-PyPort] [GridIndex] 有 1207 个 PY 物品分配到 tab6+ 顺序格（筛选器网格不渲染）：…… 不是重复格位。
[自检] LDB.物品/配方/科技/模型 均无重复 ID
[PyPort] 注册汇总：物品 1593（液体 337）/ 建筑 15 / 配方 1957（宽配方 478）/ 科技 275 / 本地化 7955
```

旧 WARN「物品栏自动分配已越过 tab4 区间」已消失（`grep 计数 = 0`）；本插件无 ERROR。

### 14.5 需要进档人工确认的 UI 行为

1. **可见物品**（386 个）：确认其出现在对应 tab 的筛选器网格内、不重叠（分配已断言无重复格，但 UI 是否按
   `tab == currentType` 正确分页需实看）。
2. **降级物品**（1207 个）：确认仍可通过「物品选择器 / 配方产物 / 建造栏」拿到（它们只是不在筛选器网格里）。
3. 若后续包继续增加物品，降级数量会继续增长；这是 tab1-5 固定 560 格的结构性限制。


---

## 15. v0.7.0：第 7 包 pyalternativeenergy（替代能源）+ 2 座新建筑（2026-11-17）

### 15.1 七包规模

| 包 | 注册物品 | 建筑 | 配方 | 科技 |
|---|---:|---:|---:|---:|
| pyrawores 原矿 | 438（+17 mapped） | 8 | 559 | 112 |
| pycoalprocessing 煤处理 | 401 | 5 | 538 | 41 |
| pyindustry 工业 | 70 | 0 | 71 | 15 |
| pypetroleumhandling 石油 | 156 | 0 | 200 | 34 |
| pyhightech 高科技 | 249 | 2 | 344 | 48 |
| pyfusionenergy 聚变 | 199 | 0 | 245 | 25 |
| pyalternativeenergy 替代能源 | 578 | 2 | 645 | 102 |
| **合计** | **2091** | **17** | **2602** | **377** |

新增建筑：**75 PY 核设施**（model 815 / grid 5026 / type 75 / 10 条配方）、**76 PY 发电机组**（model 816 / grid 5027 / type 76 / 16 条配方）。

### 15.2 新能力：空闲池回填 + 配方/科技溢出段

- 物品：主段 10414-11099（本包 483 个），实现确定性空闲池回填顺序 6921-7299 → 7714-8499 → 8583-9199 → 9322-10199（本包回填 0，为 p8 准备）。
- 配方：主段 7600-8199（600）+ 溢出段 **8214-8259**（45；避开 MechDawn 8001-8213）。
- 科技：主段 4920-4999（80）+ 溢出段 **5040-5061**（22）。

### 15.3 实机踩坑记录：科技段撞原版升级页

首轮部署时 p7 科技段 4920–5039 **跨过原版占用的 5001–5006**（原版升级页「T动能武器伤害」等），引擎冲突检查直接**拒绝注册全部 7 个模块**（注册计数全 0，未污染 LDB）——安全机制按设计生效。修复为 4920-4999 + 5040-5061，并新增「所有科技 ID 必须避开原版已占段」的全局断言与生成器纪律：

`
原版已占科技段：5001-5006, 5101-5106, 5201-5206, 5301-5305, 5401-5405,
                5601-5605, 5701-5705, 5801-5807, 5901-5907, 6001-6006, 6101-6106
p8（pyalienlife 356 科技）规划：6107-6462
`

### 15.4 实机验证（2026-11-17 02:59，修复后）

`
物品注册 2074/2074（液体 426）+ 建筑物品 17    建筑 17/17（BuildIndex 900-916，GridIndex 5011-5027）
配方 2602/2602（宽配方 676）                  科技 377/377    本地化 10517
LDB：物品 2269 / 配方 2766 / 科技 693 / 模型 683
缺失 0 / 重复 ID 0 / ID 被改写 0 / 引用缺失 0
type 75 可见 10 条、type 76 可见 16 条，均 CanBuild=True
`

### 15.5 数据异常（已按契约处理）

pyalternativeenergy 有 1 条 canonical 配方 
eutron-absorbston **产物为空** → DSP 要求配方产物非空，生成器跳过并记入 _skipped.json → no_product_recipes（因此 646 条范围内转换 645 条）。

### 15.6 最后一包规划

pyalienlife：预计 1309 物品行（需新 ID ≈1101）、356 科技（6107-6462）、3140 条有效配方（配方段 8260+，溢出空间充足）、预计新增生物类建筑 1-2 座（总数 ≤18~19）。


---

## 16. v1.0.0：全部 9 个 PY 包接入完成（2026-11-17）

### 16.1 最终规模

| 包 | 注册物品 | 建筑 | 配方 | 科技 |
|---|---:|---:|---:|---:|
| pyrawores 原矿 | 399（+17 mapped） | 8 | 559 | 112 |
| pycoalprocessing 煤处理 | 334 | 5 | 538 | 41 |
| pyindustry 工业 | 70 | 0 | 71 | 15 |
| pypetroleumhandling 石油 | 145 | 0 | 200 | 34 |
| pyhightech 高科技 | 225 | 2 | 344 | 48 |
| pyfusionenergy 聚变 | 191 | 0 | 245 | 25 |
| pyalternativeenergy 替代能源 | 519 | 2 | 645 | 102 |
| pyalienlife 外星生命 | 1310 | 1 | 3136 | 356 |
| **合计** | **3193** | **18** | **5738** | **733** |

pypostprocessing 为纯工具包（无配方/物品/科技内容），已确认无需建内容包 → **9 个 PY 包全部处理完毕**。

### 16.2 18 座自建建筑（类型 60–77 / 模型 800–817 / 格位 5011–5028）

破碎机 60、选矿机 61、冶金炉 62、反应釜 63、蒸馏塔 64、洗涤塔 65、特殊工厂 66、通用工厂 67、
焦化炉 68、生物农场 69、精炼塔 70、采掘机 71、研究枢纽 72、粒子加速器 73、电子厂 74、
核设施 75、发电机组 76、**异星生物场 77（956 条配方）**。

### 16.3 实机最终验证（2026-11-17 03:18）

`
物品注册 3175/3175（液体 455）+ 建筑物品 18     建筑 18/18（BuildIndex 900-917，GridIndex 5011-5028）
配方 5738/5738（宽配方 1860）                  科技 733/733    本地化 20061
LDB：物品 3371 / 配方 5902 / 科技 1049 / 模型 684
缺失 0 / 重复 ID 0 / ID 被改写 0 / 引用缺失 0 / 自定义类型无建筑 0
type 77 可见 956 条，CanBuild=True
`

### 16.4 全库 ID 余量

| 类别 | 已用 | 最大 ID | 剩余 |
|---|---:|---:|---:|
| items | 3193 | 11253 | 2507（空闲池 1558） |
| recipes | 5738 | 11395 | 1662 |
| techs | 733 | 6462 | 1523 |
| models | 18 | 817 | 1230（818+） |

### 16.5 完整性指标（全局自检 PASS）

- 引用完整：配方/科技/物品交叉引用缺失 **0**；配方↔科技双向一致 **0** 差异；
- 科技段与原版已占段交集 **0**（全部 733 条）；
- 前七包既有 ID 逐个不变，差异 **0**；跨包占位迁移累计 **494 条**，ID 变化 **0**；
- 幂等：连跑两次 / 从零复跑，3710 个内容文件 sha256 完全一致；
- 图标：提取 3796 张，缺失 129 张全部用兜底图标，不影响注册。

### 16.6 已知限制（进档人工确认清单）

1. 配方选择器 page 6–11 为新增分页（p1 page5 … p8 部分 page7-11），需确认分页显示与翻页正常；
2. 物品筛选器 tab1-5 只有约 560 格，3175 个物品中约 2700 个只能通过配方产物/建造栏/物品选择器获取（不出现在筛选器网格）；
3. 0 原料配方累计 15 条、627 条概率产物已确定性化、4 条零产物配方被跳过 —— 均记录在 _skipped.json；
4. 建筑外观为克隆原版机器，PY 专属外观需 Unity AssetBundle（未做）；
5. 未做进存档级人工试玩验证（避免动存档），建议首次进档按「原矿→煤处理→冶炼」主线跑一条链。
