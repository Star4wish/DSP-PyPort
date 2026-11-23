# content/ 说明（schema 样例）

本目录**只是内容 schema 的 8 文件样例**，用于验证加载器/注册器的最小数据结构，
**不是**完整 PY 内容包：

```
content/
├─ manifest.json          # 模块清单（modules[].enabled）
├─ locale/{zh-CN,en-US}.json
├─ icons/6300.png         # 1 个示例图标
└─ smoke/{items,recipes,techs,buildings}.json
```

## 完整内容包在哪

完整内容包（3876 个文件、约 26 MB zip：3193 物品 / 5738 配方 / 733 科技 / 18 建筑 / 3805 图标）
通过 **GitHub Releases** 分发，或使用本地 `dist/DSP-PyPort-*.zip`：

```
<GameDir>\BepInEx\plugins\DSP-PyPort\
    DSP-PyPort.dll
    content\            <- 完整内容（从 Release 解压）
        manifest.json
        _global\
        pyrawores\
        pycoalprocessing\
        ...
```

部署方式：

```powershell
# 用完整内容打包/部署
powershell -NoProfile -ExecutionPolicy Bypass -File ..\build.ps1 -ContentDir "D:\path\to\full\content"
```

## 为什么不放进仓库

1. 体积：3805 个 PNG + 61 个 JSON，约 26 MB（git 历史会持续膨胀）。
2. 版权：图标与数据派生自 Factorio 的 Pyanodons 模组（见 ../NOTICE.md），以 Release 形式分发更清晰。
3. 生成管线：完整内容由「Factorio 数据提取 → canonical 表 → convert_py2dsp.py 生成」产出，
   该管线与上游数据不在本仓库范围内。

## 格式参考

- 字段定义：`Core/ContentModel.cs`、`Core/ContentLoader.cs`
- 完整格式与转换规则：`../README-交付说明.md`（§4 内容 schema、§7 各包接入步骤）
