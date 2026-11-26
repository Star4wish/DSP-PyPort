# 第三方组件与内容声明（NOTICE）

本项目（DSP-PyPort）的**代码**以 MIT 许可发布（见 [LICENSE](LICENSE)）。
以下第三方组件与内容按各自的许可/条款使用，其版权归原作者所有。

## 1. 内容数据与美术素材

| 来源 | 用途 | 说明 |
|---|---|---|
| **Pyanodons（PY）模组包**（Factorio） | PY 配方/物品/科技数据、图标 PNG | 版权归 Pyanodons 团队所有。本 MOD 的内容包由 PY 模组的公开数据与图形资源转换而来：仓库以**单个压缩包** `assets/DSP-PyPort-content-*.zip` 附带（便于从源码构建），`content/` 目录仅保留 8 个 schema 样例；完整可安装版本同时通过 Releases 分发。若版权方有异议，可删除对应归档/发布包。 |
| Dyson Sphere Program / Youthcat Studio | 运行时游戏数据与 API | 本仓库不包含任何游戏本体文件或反编译产物。 |

## 2. 运行时依赖（不随本仓库分发）

| 组件 | 用途 | 分发方式 |
|---|---|---|
| [BepInEx 5.4.17](https://github.com/BepInEx/BepInEx) | 插件加载框架 | 用户自行安装 |
| [HarmonyX](https://github.com/BepInEx/HarmonyX) | 运行时方法补丁 | 随 BepInEx 提供 |
| [CommonAPI / DSPModSave](https://github.com/kremnev8/CommonAPI) | proto 注册框架 | 用户自行安装 |
| [LDBTool](https://github.com/xiaoye97/LDBTool) | proto ID 持久化 | 用户自行安装 |
| [BuildBarTool](https://github.com/jinxOAO/DSP-BuildBarTool) | 第二排快速建造栏 | 可选，用户自行安装 |
| [DSP-RecipeSlots](https://github.com/MengLeiFudge/MLJ_DSPmods) | 配方原料/产物数量上下限 | 可选，用户自行安装 |
| Unity Engine / .NET Framework 4.7.2 | 运行环境 | 游戏自带 / 系统组件 |

本仓库**不包含**上述任何第三方 DLL 或二进制；构建时通过 `HintPath` 引用本机安装的游戏与 MOD 程序集
（`Private=false`，不会复制进产物）。

## 3. 兼容性适配说明

- 代码中出现的 MOD 名称（DSP-MechDawn、UXAssist、ModFixerOne、ErrorAnalyzer、Nebula 等）仅用于
  兼容性判断/日志说明，不包含其源代码或二进制。
- 反编译研究仅用于公开 API 行为确认（DSP 游戏程序集、CommonAPI、LDBTool、BuildBarTool），
  未复制其实现代码进本仓库。

## 4. 反馈

如您是上述内容的版权方并认为本项目的分发方式不妥，请开 issue 或联系仓库作者，我们会及时调整/移除。
