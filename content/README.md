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

完整内容（3876 个文件：3193 物品 / 5738 配方 / 733 科技 / 18 建筑 / 3805 图标）以**单个压缩包**形式
随仓库放在 `../assets/DSP-PyPort-content-<版本>.zip`：

```
assets/
└─ DSP-PyPort-content-v0.9.3.zip     # 约 25 MB，manifest.json 在压缩包根目录
```

`build.ps1` 未指定 `-ContentDir` 时会自动把它解压到 `build\content\`（已 gitignore）并部署：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File ..\build.ps1
# 或显式指定： -ContentDir <完整内容目录> / -ContentZip <压缩包路径>
```

GitHub Releases 上另有“可直接解压安装”的完整发布包（`DSP-PyPort/` 目录内含 DLL + content）。

## 为什么是单个压缩包

1. 上传/克隆效率：3805 个 PNG + 61 个 JSON 若逐文件提交，GitHub 上传与仓库体积都很难受；单归档一次上传即可。
2. 体积：Git 对 ZIP 内 PNG 无法再压缩，归档约 25 MB（原始 38 MB）。
3. 版权：图标与数据派生自 Factorio 的 Pyanodons 模组（见 ../NOTICE.md），以归档/Release 形式分发更清晰。
4. 生成管线：完整内容由「Factorio 数据提取 → canonical 表 → convert_py2dsp.py 生成」产出，
   该管线与上游数据不在本仓库范围内；维护者更新内容后用 `../tools/pack-content.ps1` 重新打包。

## 格式参考

- 字段定义：`Core/ContentModel.cs`、`Core/ContentLoader.cs`
- 完整格式与转换规则：`../README-交付说明.md`（§4 内容 schema、§7 各包接入步骤）
