# DSP-PyPort

把 Factorio 的 **Pyanodons（PY）** 大型模组包移植到 **Dyson Sphere Program（戴森球计划）** 的 BepInEx 插件。

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
**当前版本：v0.9.3**（科技树主页已由 PY 科技完整接管）

> English quick start: see [English](#english) at the bottom. The full PY content bundle is tracked as a single
> `assets/DSP-PyPort-content-*.zip` (build.ps1 auto-extracts it); ready-to-install bundles are also attached to
> **GitHub Releases**.

---

## 1. 这是什么

- 把 PY 的配方 / 物品 / 科技 / 建筑搬进 DSP：目前 8 个内容包已接入（第 9 包 pypostprocessing 无内容，不需要移植）。
- 规模：**物品 3193（含 455 液体）/ 配方 5738 / 科技 733 / PY 建筑 18 座 / 本地化 20061 条**。
- 科技树：DSP 的 `TechProto.page` 是硬编码 `ID >= 2000 ? 升级页 : 主页`，所以 PY 科技**占用原版主页科技 ID（1–1999）**
  并原位替换原版科技 —— 主页现在显示的是完整的 PY 科技树（附加其它 MOD 的主页科技，共 781 节点），
  原版 206 条升级科技保留在「升级」页。
- 配方选择器：分类页签（全部/采掘/冶炼/化工/组装/生物/能源/科研/其它）+ 纵向滚动条 + 页码，图标为 PY 图标 1:1 直出。
- 18 座 PY 建筑绑定到 **BuildBarTool 第二排快速建造栏**（分类 5 / 8），不占原版建造菜单。

## 2. 运行环境 / 前置 MOD

| 依赖 | 版本 | 必需 |
|---|---|---|
| Dyson Sphere Program | 0.10.35.x（Steam） | ✅ |
| BepInEx | 5.4.17 x64 | ✅ |
| [CommonAPI](https://dsp.thunderstore.io/) | 1.6.7 | ✅ |
| [LDBTool](https://dsp.thunderstore.io/) | 3.0.3 | ✅ |
| BuildBarTool | 1.0.4 | 可选（PY 建筑的第二排快速建造栏） |
| DSP-RecipeSlots | 1.2.x | 可选（配方原料/产物数量上下限） |

已做兼容处理的 MOD：**DSP-MechDawn**（配方运行期 ID 段 8001–8213 已让开）、UXAssist、ModFixerOne、ErrorAnalyzer、
Nebula 多人 API 等。与其它内容 MOD 共存时若 ID 冲突，日志会打印 `[冲突检查]` 明细并拒绝注册（避免存档污染）。

## 3. 安装

1. 安装 BepInEx 5.4.17 x64 + CommonAPI + LDBTool（及其它可选 MOD）。
2. 从 [Releases](https://github.com/Star4wish/DSP-PyPort/releases) 下载最新内容包 zip（约 26 MB，含 DLL + 3876 个内容文件）。
3. 解压后把 `DSP-PyPort/` 整个目录放到：

   ```
   <游戏目录>\BepInEx\plugins\DSP-PyPort\
       DSP-PyPort.dll
       content\...
   ```

4. 启动游戏，检查 `BepInEx\LogOutput.log` 关键行（见 §6）。

> ⚠️ **建议新开存档**：PY 科技占用了原版主页科技 ID，旧档的科技进度会对应到不同科技
> （物品/配方/建筑 ID 未变，旧档可进但科技树进度会错乱）。

## 4. 从源码构建

**编译本身不需要 `content/`（图标/数据只在运行时读取）**，只需本机安装游戏 + BepInEx + CommonAPI + LDBTool
提供引用程序集（`DSP-PyPort.csproj` 通过 `HintPath` 引用，不复制、不随仓库分发）。

仓库以**一个压缩包** `assets/DSP-PyPort-content-<版本>.zip`（约 25 MB，内含 3876 个内容文件）附带完整 PY 内容；
`content/` 目录本身只跟踪 8 个 schema 样例文本，避免仓库里出现数千个小文件。
内容生成管线（Factorio 数据提取 + `convert_py2dsp.py`）不在本仓库范围内。

```powershell
# 1) 需要 .NET SDK 6+（目标框架 net472，通过 NuGet 引用参考程序集）
# 2) 指定游戏目录（三选一）：
#    - 命令行：-p:DSPGameDir=...
#    - 环境变量：$env:DSP_GAME_DIR=...
#    - 自动探测 Steam 常见安装位置
dotnet build .\DSP-PyPort.csproj -c Release -p:DSPGameDir="D:\Steam\steamapps\common\Dyson Sphere Program"

# 编译 + 部署：默认自动解压 assets 内容包到 build\content 并部署
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1

# 使用自己准备的完整内容目录 / 指定压缩包
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -ContentDir "D:\path\to\full\content"
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -ContentZip "D:\path\to\content.zip"

# 只编译不部署
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -NoDeploy

# 维护者：内容更新后重新打包 assets 归档（保证 assets 内只留一个内容 zip）
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\pack-content.ps1
```

构建要点：

- 游戏 DLL（`Assembly-CSharp` / `UnityEngine.*`）与 `BepInEx`、`CommonAPI`、`LDBTool` 均为**引用不复制**（`Private=false`），
  本仓库不附带任何第三方二进制。
- 内容解析顺序：`-ContentDir` > `-ContentZip` > `assets/*.zip`（解压到 `build\content`，已 gitignore）> `.\content` 样例。
- 编译前置检查：`build.ps1` 会先检查游戏程序集、BepInEx、CommonAPI、LDBTool 是否存在并给出明确报错；
  `DSP-PyPort.csproj` 对 CommonAPI/LDBTool 的安装目录名做了多种布局探测（`CommonAPI-CommonAPI/`、`CommonAPI/`、
  plugins 根目录等），安装器不同也不会找不到引用。
- `build.ps1` 部署时会清理 LDBTool 缓存中**只属于本 MOD** 的 ID/GridIndex 条目：
  LDBTool 3.0.3 的 `ConfigFile` 是 `saveOnConfigSet:true`，且会用缓存值覆盖 proto.ID；
  清掉后内容 JSON 是唯一 ID 来源（其它 MOD 的缓存不动）。
- 插件注册阶段（`ProtoRegistry.StartModLoad`）约 1.3 s；到主菜单总耗时约 60–70 s（其中 ~55 s 是游戏本体 + 其它 MOD）。

## 5. 配置（`BepInEx\config\org.pyport.dsp.cfg`）

| 配置 | 默认 | 说明 |
|---|---|---|
| `[Compat] TechReplaceMode` | true | PY 科技占用原版主页 ID 并原位替换（关闭 = 科技 ID 冲突会拒绝注册，内容不可用） |
| `[Compat] SandboxUnlockPyContent` | true | 沙盒模式自动解锁 5738 配方 + 733 科技 |
| `[Compat] SkipLdbToolBind` | true | 跳过 LDBTool 对本 MOD proto 的逐条配置写盘（启动 300 s+ → 数秒） |
| `[Compat] FixIconAtlas` | true | 把 PY 图标注入原版 IconSet 空闲格并重写索引（否则全是同一格） |
| `[Compat] UseBuildBarToolTier2` | true | 18 座 PY 建筑绑定 BuildBarTool 第二排（关闭则回退建造菜单自动分配） |
| `[Compat] AutoBuildIndex` | true | 建筑 BuildIndex 自动分配 |
| `[UI] EnableRecipePickerUi` | true | 配方选择器：Sprite 直出图标 + 分类页签 + 滚动条 + 页码 |
| `[Perf] LazyIcons` | true | 图标懒加载（首次访问 iconSprite 时解码 PNG） |
| `[Modules] Enable_<moduleId>` | true | 8 个内容包逐个开关（如 `Enable_pyalienlife=false`，排查问题可只留一个包） |

## 6. 启动自检（日志关键词）

启动后检查 `BepInEx\LogOutput.log`：

```
[PyPort] 注册汇总：物品 3175（液体 455）/ 建筑 18 / 配方 5738 / 科技 733 / 本地化 20061 条
[科技替换] 模式开启：原版主页槽位替换 103 条 / 新增科技 630 条 / 保留原版主页科技 6 条 / 隐藏残留 0 条
[科技替换] 重建完成：替换科技 103/103（失败 0）/ 新增科技 630 / 配方 preTech 重算 6048 条
[科技树自检] page0（主页）科技 781 条 = PY 733 + 其它 MOD/保留原版 48；坐标异常 0；主页重名 0
[Compat] 沙盒模式：已自动解锁 PY 内容 —— 配方 5738/5738 条、科技 733/733 个
[BBT] 第二排快速建造栏绑定完成：18/18 座（失败 0）
[图标图集] 索引复核：物品 3193/3193、科技 733/733、配方 5738/5738 的图集索引 > 0
```

若出现 `[冲突检查] … 拒绝注册全部内容模块`，说明与其它 MOD 的 ID 段冲突，日志会列出冲突 ID。
若出现 `ArgumentNullException`（战斗）或 `IndexOutOfRangeException`（进档），请附日志提 issue。

## 7. 已知限制

1. **建造菜单图标**：原版 IconSet 图集只有 625 格，扣除原版/其它 MOD 后只剩 ~161 个空闲格，
   因此建造菜单/PY 物品图标是 161 个代表图标复用；**配方选择器**是 Sprite 1:1 精确图标。
2. **物品筛选器**：DSP 物品筛选器只渲染 tab1–5（386 格），PY 物品中约 2789 个被分配到 tab6+ 顺序格，
   不在筛选器网格显示（仍可通过物品选择器、配方产物、建造栏访问）。
3. **科技 ID 语义**：PY 科技占用原版主页 ID，旧存档科技进度错乱（建议新档）。
4. **升级页**：原版 206 条升级科技保留，未替换。
5. 图标/UI 补丁依赖反射与 Harmony，DSP 升级后可能失效（日志会 WARN 并回退原版行为）。

## 8. 仓库结构

```
DSP-PyPort/
├─ Plugin.cs                     # BepInEx 插件入口（配置 / 注册 / 收尾 / Update 探针）
├─ Core/                         # 全部引擎逻辑（注册、科技替换、UI、兼容补丁）
│  ├─ Registrar*.cs              # 物品/建筑/配方/科技注册与收尾回填
│  ├─ Registrar.TechReplace.cs   # 科技替换模式（占用原版主页 ID）
│  ├─ RecipePickerUiFix.cs       # 配方选择器 UI（Sprite 图标/分类页签/滚动条）
│  ├─ IconSetAtlasFix.cs         # IconSet 图集注入
│  ├─ BuildBarIntegration.cs     # BuildBarTool 第二排绑定
│  └─ *Fix.cs                    # LDBTool/PrefabDesc/SkillSystem/Sandbox 等兼容补丁
├─ build.ps1                     # 编译 + 解压内容归档 + 部署 + 清 LDBTool 缓存
├─ tools/
│  ├─ clean-ldbtool-cache.ps1    # 只清理本 MOD 的 LDBTool ID/GridIndex 缓存
│  └─ pack-content.ps1           # 维护者：把完整内容打成单个 assets/*.zip
├─ assets/
│  └─ DSP-PyPort-content-*.zip   # 完整 PY 内容（单归档，约 25 MB / 3876 文件，供构建自动解压）
├─ content/                      # 仅 8 文件 schema 样例（真实内容在 assets 归档 / Release）
├─ build/content/                # 构建时自动解压的内容（gitignore）
├─ README-交付说明.md            # 详细开发/验收记录（中文，76 KB）
├─ CHANGELOG.md
├─ NOTICE.md                     # 第三方内容与许可声明
└─ LICENSE (MIT)
```

完整内容包（3876 文件）与历史版本见 [Releases](https://github.com/Star4wish/DSP-PyPort/releases)。

## 9. 许可

- 代码：**MIT**（见 [LICENSE](LICENSE)）。
- PY 内容（配方/物品/科技数据与图标）来自 Factorio 的 Pyanodons 模组包，版权归其作者；
  本仓库**不再分发**这些素材的原始文件，完整内容包按上游许可在 Releases 中提供，详见 [NOTICE.md](NOTICE.md)。

---

## English

**DSP-PyPort** is a BepInEx plugin that ports the *Pyanodons* Factorio overhaul to **Dyson Sphere Program**.

- 8 content packages: 3193 items, 5738 recipes, 733 techs, 18 Pyanodons buildings.
- DSP hardcodes `TechProto.page => ID >= 2000` (upgrade page), so PY techs **replace vanilla main-page tech IDs (1–1999)**
  in place; the whole Pyanodons tech tree (781 nodes incl. other mods) lives on the main tech page again.
- Recipe picker: category tabs + scrollbar + page numbers + exact sprites; 18 buildings are bound to
  BuildBarTool's second quick-build row.
- **Requirements**: DSP 0.10.35.x, BepInEx 5.4.17 x64, CommonAPI 1.6.7, LDBTool 3.0.3
  (optional: BuildBarTool 1.0.4, DSP-RecipeSlots).
- **Install**: grab the content bundle from [Releases](https://github.com/Star4wish/DSP-PyPort/releases), unzip `DSP-PyPort/` into
  `<GameDir>\BepInEx\plugins\`.
- **Build from source**: `dotnet build DSP-PyPort.csproj -c Release -p:DSPGameDir="<game dir>"`.
  The repo ships the full generated content as a single `assets/DSP-PyPort-content-*.zip`
  (`build.ps1` extracts it automatically); `content/` only tracks a tiny schema sample.
  Ready-to-install bundles are attached to [Releases](https://github.com/Star4wish/DSP-PyPort/releases).
- **New save recommended**: PY techs reuse vanilla main-page tech IDs, so old saves' tech progress will be remapped.
- **License**: code MIT; Pyanodons content/graphics belong to their authors (see [NOTICE.md](NOTICE.md)).
