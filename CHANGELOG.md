# Changelog

本项目按 [Semantic Versioning](https://semver.org/lang/zh-CN/) 管理版本。
完整开发/验收记录（日志摘录、设计取舍）见 [README-交付说明.md](README-交付说明.md)。

## [0.9.3] - 2026-11-24

### 新增
- **仓库随附压缩内容包**：完整 PY 内容（3876 文件 / 61 json + 3805 png，原始 38 MB）打包为单个
  `assets/DSP-PyPort-content-v0.9.3.zip`（约 25 MB），避免仓库出现数千个小文件；
  `build.ps1` 新增自动解压（`-ContentDir` > `-ContentZip` > `assets/*.zip` > `content/` 样例）并部署，
  新增 `tools/pack-content.ps1` 供内容更新后重新打包（保证 `assets/` 内只留一个归档）。
- **科技替换模式（TechReplaceMode）**：PY 科技占用原版主页科技 ID（1–1999）并原位覆盖原版 `TechProto`，
  绕过 DSP 硬编码 `TechProto.page => ID >= 2000 ? 升级页 : 主页`。
  - 103 条原版主页科技原位替换 + 630 条空洞 ID 新增 = 733 条科技全部落在主页（page 0）。
  - 收尾补跑 `Preload()/Preload2()`（CommonAPI 只处理新增 proto）并重算 6048 条配方 `preTech`。
  - 未被覆盖的原版主页科技自动标记 obsolete（本次 0 条）；保留 1508/1509/1901–1904。
  - 新增 `[科技树自检]`：主页节点数 / 名称翻译 / Preload2 数组一致性 / 坐标 / 重名运行时校验。
  - 沙盒解锁补全为 733/733 科技（旧版漏掉替换型科技）。
- `Core/TimelineProbe.cs`：启动耗时时间线探针（`DSPPYPORT_TIMELINE=1` 时启用，默认不生效）。
- `README.md` 正式化、`.gitignore`、`CHANGELOG.md`、`NOTICE.md`。

### 修复
- **与 DSP-MechDawn 的配方 ID 冲突**：PY 生成器 p7 配方主段旧配置 7600–8199 的尾部（8001–8199）
  与 MechDawn 运行期配方段 8001–8213 重叠，导致 MechDawn 198 条配方注册失败、UXAssist 生产目录崩溃。
  p7 主段改为 7600–7999，199 条配方迁到 11400–11598；生成器新增主段/全局断言与一次性基线迁移。
- `DSP-PyPort.csproj` 版本号 0.1.0 → 0.9.3；支持 `DSP_GAME_DIR` 环境变量与更多 Steam 默认路径；
  CommonAPI/LDBTool 引用支持多种安装目录布局，`build.ps1` 编译前先校验游戏/BepInEx/CommonAPI/LDBTool 依赖并给出明确报错。
- `build.ps1` 支持 `-GameDir` / `DSP_GAME_DIR`，并在注释中说明默认只部署 schema 样例内容。

### 验证
- 启动：进程启动→主菜单 64–67 s；本插件 Awake 1.2–1.3 s。
- `[PyPort] 注册汇总：物品 3175 / 建筑 18 / 配方 5738 / 科技 733 / 本地化 20061`。
- `[科技树自检] page0 781 条 = PY 733 + 其它 MOD/保留原版 48；坐标异常 0；主页重名 0`。
- `[BBT] 18/18`；`[图标图集] 索引复核：3193/733/5738 全部 > 0`；`ArgumentNullException = 0`。

## [0.9.1] - 2026-11-19

- 18 座 PY 建筑绑定 **BuildBarTool 第二排快速建造栏**（category 5 slot 1–8 / category 8 slot 1–10），
  `BuildIndex = 0` 移出原版建造菜单，不动原版建筑（`[Compat] UseBuildBarToolTier2`）。

## [0.9.0] - 2026-11-19

- **P0-1 战斗崩溃修复**：`SkillSystem` 的 `fire_lock` / `combat_stat_rw_lock` 是字段初始化器，
  旧补丁跳过原构造函数导致 null；改为默认走原构造函数（模型 ID 已不越界）+ 运行时兜底。
- **P0-2 图标图集注入**：原版 IconSet 2000×2000 / 625 格 / 80px；把 PY 图标注入空闲格并重写
  item/recipe/tech 索引，不再全部显示第 0 格（铁矿）。

## [0.8.0] - 2026-11-18

- 配方选择器分页（原版只认 GridIndex 千位 1/2 → 支持任意页，每页 112 槽）。
- 内容 GridIndex 重排为每类型 page1 起连续分页（最大 15 页）。
- 沙盒模式自动解锁 PY 配方/科技。
- 建造菜单 `BuildIndex` 自动分配（原版 `slot <= 12` 限制）。

## [0.7.2] - 2026-11-18

- 建新档崩溃修复：建筑模型 ID 800–817 → 720–737（`SkillSystem..ctor` 等 13 处按模型 ID 下标写数组）。
- 启动 346 s → ~56 s：Harmony 跳过 LDBTool 对本 MOD proto 的 ID/GridIndex 配置绑定
  （LDBTool 每遇新 key 整文件保存，约 9663 个 proto ≈ 7 GB 磁盘 I/O）。

## [0.7.0] - 2026-11-18

- 8 个内容包全部接入（pyrawores / pycoalprocessing / pyindustry / pypetroleumhandling /
  pyhightech / pyfusionenergy / pyalternativeenergy / pyalienlife）。
- 建新档崩溃修复 + 启动性能优化（task-13）。

## [0.6.0] / [0.5.0] / [0.4.0] / [0.3.0] / [0.2.0] - 2026-11-17

- 逐包接入：pyfusionenergy（0.6.0）、pyhightech（0.5.0）、pypetroleumhandling（0.4.0）、
  pyindustry（0.3.0）、pyrawores + pycoalprocessing（0.2.0）。
- 物品 GridIndex 分配器重做（0.6.0）：tab1–5 可见格 + tab6+ 降级顺序格，确定性分配。

## [1.0.0] - 2026-11-17

- 全部 9 个 PY 包接入完成（8 个内容包；pypostprocessing 无内容）。
- 配方 5738 / 科技 733 / 物品 3193 / PY 建筑 18 的内容基线冻结，自检 PASS。
