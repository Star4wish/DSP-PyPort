using System;
using System.Collections.Generic;
using CommonAPI.Systems;
using UnityEngine;

namespace DSPPyPort.Core
{
    internal sealed partial class Registrar
    {
        // ------------------------------------------------------------------ items
        private void RegisterItems()
        {
            int ok = 0;
            int tried = 0;
            int buildingEntriesSkipped = 0;
            for (int m = 0; m < content.Modules.Count; m++)
            {
                ModuleContent module = content.Modules[m];
                if (module.Refused)
                {
                    continue;
                }
                for (int i = 0; i < module.Items.Count; i++)
                {
                    ItemDef def = module.Items[i];
                    if (def.Skip)
                    {
                        if (def.SkipReason != null && def.SkipReason.StartsWith("source=mapped", StringComparison.Ordinal))
                        {
                            mappedCount++;
                        }
                        else
                        {
                            skippedInvalid++;
                            log.LogWarning("[PyPort] 跳过物品 " + def.Id + "：" + def.SkipReason);
                        }
                        continue;
                    }
                    if (def.IsBuilding)
                    {
                        buildingEntriesSkipped++;
                        continue; // 由 RegisterBuildings 用 buildings.json 的参数注册
                    }
                    tried++;
                    if (RegisterPlainItem(def, module, "物品") != null)
                    {
                        ok++;
                    }
                }
            }
            log.LogInfo(string.Format("[PyPort] 物品注册：{0}/{1}（液体 {2} 个，mapped 跳过 {3}，建筑物品条目 {4} 交给建筑注册）",
                ok, tried, fluidCount, mappedCount, buildingEntriesSkipped));
        }

        private ItemProto RegisterPlainItem(ItemDef def, ModuleContent module, string kind)
        {
            if (!MarkItemId(def.Id, module.Id, kind))
            {
                return null;
            }
            try
            {
                string key = def.EffectiveKey;
                int grid = def.GridIndex > 0 ? def.GridIndex : AllocateItemGrid();
                def.GridIndex = grid;
                string iconPath = ResolveIconPath(def.IconFile, def.IconSource, def.IsFluid, kind + " " + def.Id);
                ItemProto proto = ProtoRegistry.RegisterItem(
                    def.Id,
                    key + "_Name",
                    key + "_Desc",
                    iconPath,
                    grid,
                    def.Stack,
                    (EItemType)(byte)def.ItemType);
                if (proto == null)
                {
                    log.LogError("[PyPort] 注册" + kind + " " + def.Id + " 返回 null");
                    return null;
                }
                if (def.IsFluid)
                {
                    proto.IsFluid = true;
                    fluidCount++;
                }
                registeredItemIdList.Add(def.Id);
                items.Add(new RegisteredItem { Def = def, Proto = proto, ExpectedId = def.Id });
                AddItemTranslations(def);
                return proto;
            }
            catch (Exception ex)
            {
                log.LogError("[PyPort] 注册" + kind + " " + def.Id + " 失败：" + ex);
                return null;
            }
        }

        // ------------------------------------------------------------------ buildings
        private void RegisterBuildings()
        {
            int ok = 0;
            int tried = 0;
            int autoModel = 0;
            for (int m = 0; m < content.Modules.Count; m++)
            {
                ModuleContent module = content.Modules[m];
                if (module.Refused)
                {
                    continue;
                }
                for (int i = 0; i < module.Buildings.Count; i++)
                {
                    BuildingDef def = module.Buildings[i];
                    if (def.Skip)
                    {
                        skippedInvalid++;
                        log.LogWarning("[PyPort] 跳过建筑 item " + def.ItemId + "：" + def.SkipReason);
                        continue;
                    }
                    tried++;
                    if (def.ModelId <= 0)
                    {
                        def.ModelId = AllocateModelId();
                        autoModel++;
                    }
                    if (!MarkItemId(def.ItemId, module.Id, "建筑"))
                    {
                        continue;
                    }
                    if (!MarkModelId(def.ModelId, module.Id))
                    {
                        continue;
                    }
                    try
                    {
                        ItemDef itemEntry = FindItemEntry(module, def.ItemId);
                        InheritFromItemEntry(def, itemEntry);

                        string sourceDesc;
                        ModelProto sourceModel = ResolveSourceModel(def, out sourceDesc);
                        if (sourceModel == null)
                        {
                            log.LogError("[PyPort] 建筑 item " + def.ItemId + " 找不到克隆源（clone_from=" +
                                         (def.CloneFrom ?? "null") + "，clone_prefab_model_id=" + def.ClonePrefabModelId + "），跳过");
                            skippedInvalid++;
                            continue;
                        }

                        ModelProto model = ProtoRegistry.RegisterModel(def.ModelId, sourceModel.PrefabPath);
                        if (model != null)
                        {
                            model.HpMax = sourceModel.HpMax;
                            model.HpUpgrade = sourceModel.HpUpgrade;
                            model.HpRecover = sourceModel.HpRecover;
                            model.ObjectType = sourceModel.ObjectType;
                            model.RendererType = sourceModel.RendererType;
                            model.RotSymmetry = sourceModel.RotSymmetry;
                            model.Order = sourceModel.Order;
                        }

                        string key = def.EffectiveKey;
                        int grid = def.GridIndex > 0 ? def.GridIndex : AllocateItemGrid();
                        def.GridIndex = grid;
                        int buildIndex = def.BuildIndex > 0 ? def.BuildIndex : nextBuildIndex++;
                        if (buildIndex < 100 || buildIndex > 999)
                        {
                            log.LogWarning("[PyPort] 建筑 item " + def.ItemId + " 的 build_index=" + buildIndex +
                                           " 超出 100-999，建造栏位置可能异常");
                        }
                        string iconPath = ResolveIconPath(def.IconFile, def.IconSource, false, "建筑 " + def.ItemId);
                        ItemProto item = ProtoRegistry.RegisterItem(
                            def.ItemId,
                            key + "_Name",
                            key + "_Desc",
                            iconPath,
                            grid,
                            50,
                            EItemType.Production);
                        if (item == null || model == null)
                        {
                            log.LogError("[PyPort] 建筑 item " + def.ItemId + " 注册失败（model=" + (model != null) +
                                         " item=" + (item != null) + "）");
                            continue;
                        }
                        ProtoRegistry.AddModelToItemProto(model, item, new int[] { 1 }, buildIndex);
                        item.PreTechOverride = def.PreTech;
                        if (def.RecipeType >= BuildingRecipeTypeMin && def.RecipeType <= BuildingRecipeTypeMax)
                        {
                            usedCustomRecipeTypes.Add((byte)def.RecipeType);
                        }

                        registeredItemIdList.Add(def.ItemId);
                        buildings.Add(new RegisteredBuilding
                        {
                            Def = def,
                            Item = item,
                            Model = model,
                            ExpectedItemId = def.ItemId,
                            ExpectedModelId = def.ModelId
                        });
                        AddBuildingTranslations(def);
                        ok++;
                    }
                    catch (Exception ex)
                    {
                        log.LogError("[PyPort] 注册建筑 item " + def.ItemId + " 失败：" + ex);
                    }
                }
            }
            log.LogInfo(string.Format("[PyPort] 建筑注册：{0}/{1}（模型 800–999 段，自动分配 model_id {2} 个；建造索引从 900 起）",
                ok, tried, autoModel));

            // 兜底：items.json 里 is_building=true 但没有 buildings.json 参数的条目，按普通物品注册（不静默丢弃）
            for (int m = 0; m < content.Modules.Count; m++)
            {
                ModuleContent module = content.Modules[m];
                if (module.Refused)
                {
                    continue;
                }
                for (int i = 0; i < module.Items.Count; i++)
                {
                    ItemDef def = module.Items[i];
                    if (!def.IsBuilding || def.Skip || usedItemIds.Contains(def.Id))
                    {
                        continue;
                    }
                    log.LogWarning("[PyPort] 建筑物品 " + def.Id +
                                   " 在 items.json 里 is_building=true，但 buildings.json 没有对应参数，按普通物品注册");
                    RegisterPlainItem(def, module, "建筑兜底物品");
                }
            }
        }

        private void InheritFromItemEntry(BuildingDef def, ItemDef entry)
        {
            if (entry == null)
            {
                return;
            }
            if (string.IsNullOrEmpty(def.NameZh)) def.NameZh = entry.NameZh;
            if (string.IsNullOrEmpty(def.NameEn)) def.NameEn = entry.NameEn;
            if (string.IsNullOrEmpty(def.DescZh)) def.DescZh = entry.DescZh;
            if (string.IsNullOrEmpty(def.IconFile)) def.IconFile = entry.IconFile;
            if (string.IsNullOrEmpty(def.IconSource)) def.IconSource = entry.IconSource;
            if (string.IsNullOrEmpty(def.Key)) def.Key = entry.Key;
        }

        private static ItemDef FindItemEntry(ModuleContent module, int itemId)
        {
            for (int i = 0; i < module.Items.Count; i++)
            {
                if (module.Items[i].Id == itemId)
                {
                    return module.Items[i];
                }
            }
            return null;
        }

        private ModelProto ResolveSourceModel(BuildingDef def, out string description)
        {
            description = string.Empty;
            if (def.ClonePrefabModelId > 0)
            {
                ModelProto direct = LDB.models.Select(def.ClonePrefabModelId);
                if (direct != null)
                {
                    description = "model " + def.ClonePrefabModelId;
                    return direct;
                }
                log.LogWarning("[PyPort] 建筑 item " + def.ItemId + " 的 clone_prefab_model_id=" + def.ClonePrefabModelId +
                               " 在 LDB.models 里不存在，回退到 clone_from");
            }

            int sourceItemId;
            string sourceDesc;
            if (!BuildingCloneSources.TryResolve(def.CloneFrom, out sourceItemId, out sourceDesc))
            {
                description = sourceDesc;
                return null;
            }
            ItemProto sourceItem = LDB.items.Select(sourceItemId);
            if (sourceItem == null)
            {
                log.LogError("[PyPort] 克隆源物品 " + sourceItemId + "（" + sourceDesc + "）在 LDB 里不存在（缺原版数据？）");
                return null;
            }
            ModelProto sourceModel = LDB.models.Select(sourceItem.ModelIndex);
            if (sourceModel == null)
            {
                log.LogError("[PyPort] 克隆源物品 " + sourceItemId + " 的模型 " + sourceItem.ModelIndex + " 不存在");
                return null;
            }
            if (string.IsNullOrEmpty(sourceModel.PrefabPath))
            {
                log.LogError("[PyPort] 克隆源模型 " + sourceModel.ID + "（" + sourceDesc + "）的 PrefabPath 为空");
                return null;
            }
            description = sourceDesc;
            return sourceModel;
        }

        // ------------------------------------------------------------------ item/building translations
        private void AddItemTranslations(ItemDef def)
        {
            string key = def.EffectiveKey;
            AddNamedTranslation(key + "_Name", def.NameZh, def.NameEn);
            if (!string.IsNullOrEmpty(def.DescZh))
            {
                AddTranslation(key + "_Desc", def.DescZh, def.DescZh);
            }
        }

        private void AddBuildingTranslations(BuildingDef def)
        {
            string key = def.EffectiveKey;
            AddNamedTranslation(key + "_Name", def.NameZh, def.NameEn);
            if (!string.IsNullOrEmpty(def.DescZh))
            {
                AddTranslation(key + "_Desc", def.DescZh, def.DescZh);
            }
        }
    }
}
