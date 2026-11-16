using System;
using System.Collections.Generic;
using CommonAPI.Systems;

namespace DSPPyPort.Core
{
    internal sealed partial class Registrar
    {
        // ================================================================== post load
        internal void ApplyPostLoad()
        {
            postLoadReached = true;
            try
            {
                lastBuildingApplied = ApplyBuildingPrefabs(true);
            }
            catch (Exception ex)
            {
                log.LogError("[PyPort] 收尾回填建筑 prefabDesc 失败：" + ex);
            }
            try
            {
                ApplyRecipeFlags(true);
            }
            catch (Exception ex)
            {
                log.LogError("[PyPort] 收尾回填配方标志失败：" + ex);
            }
            try
            {
                AppendRecipeUnlocksToTechs(true);
            }
            catch (Exception ex)
            {
                log.LogError("[PyPort] 收尾补全科技解锁清单失败：" + ex);
            }
            try
            {
                ApplyIcons();
            }
            catch (Exception ex)
            {
                log.LogError("[图标] 自定义图标应用失败（已隔离，沿用原版图标）：" + ex);
            }
            try
            {
                // LDBTool.GridIndexBind 在 PreAddProto 时用 CustomGridIndex.cfg 旧值覆盖过 GridIndex，这里写回分配值。
                ReapplyItemGridIndexes(true);
            }
            catch (Exception ex)
            {
                log.LogWarning("[GridIndex] 回填物品格位失败：" + ex.Message);
            }
            try
            {
                ReportSelfCheck(true);
            }
            catch (Exception ex)
            {
                log.LogWarning("[PyPort] 自检输出失败（不影响功能）：" + ex.Message);
            }
            log.LogInfo("[PyPort] onLoadingFinished 收尾完成（等待 Update 探针在 LDBTool EditDataAction 之后兜底回填）");
        }

        /// <summary>
        /// LDBTool 的 EditDataAction 在 onLoadingFinished 之后执行并会用 CopyPropsTo 覆盖原版 proto；
        /// 这里在 Update 里做一次「就绪探针 + 回填」，成功一次后不再执行（手册 §1.2 第 3 条）。
        /// 另外做 onLoadingFinished 未触发时的兜底（别的 MOD 在 CommonAPI OnPostAdd 抛异常会中断整个收尾链）。
        /// </summary>
        internal void Tick()
        {
            if (postEditsApplied)
            {
                return;
            }
            try
            {
                if (probeAttempts++ > 1800)
                {
                    if (!probeTimeoutReported)
                    {
                        probeTimeoutReported = true;
                        postEditsApplied = true;
                        log.LogError("[PyPort] 等待 LDB 就绪超时（30s）：建筑配方类型/配方标志可能未在 LDBTool 之后回填，请检查日志");
                    }
                    return;
                }
                if (!IsLdbReady())
                {
                    return;
                }
                if (!postLoadReached)
                {
                    log.LogWarning("[PyPort] onLoadingFinished 未触发（可能有其它 MOD 在 CommonAPI OnPostAdd 中抛异常），" +
                                   "改用 Update 探针兜底执行收尾回填");
                    ApplyPostLoad();
                    return;
                }

                int applied = ApplyBuildingPrefabs(false);
                ApplyRecipeFlags(false);
                AppendRecipeUnlocksToTechs(false);
                ReapplyItemGridIndexes(false);
                ReportSelfCheck(false);

                if (buildings.Count > 0 && applied < buildings.Count && probeAttempts < 900)
                {
                    return; // prefabDesc 还没就绪，下一帧再试
                }
                postEditsApplied = true;
                log.LogInfo("[PyPort] LDBTool EditDataAction 之后回填完成（Update 探针，建筑 prefabDesc " +
                            applied + "/" + buildings.Count + "）");
            }
            catch (Exception ex)
            {
                log.LogError("[PyPort] Update 探针回填失败：" + ex);
                postEditsApplied = true;
            }
        }

        private bool IsLdbReady()
        {
            if (LDB.items == null || LDB.items.dataArray == null || LDB.items.dataArray.Length == 0)
            {
                return false;
            }
            if (LDB.recipes == null || LDB.recipes.dataArray == null || LDB.recipes.dataArray.Length == 0)
            {
                return false;
            }
            if (recipes.Count > 0)
            {
                return LDB.recipes.Select(recipes[0].Proto.ID) != null;
            }
            if (items.Count > 0)
            {
                return LDB.items.Select(items[0].Proto.ID) != null;
            }
            return true;
        }

        // ------------------------------------------------------------------ building prefab
        private int ApplyBuildingPrefabs(bool verbose)
        {
            int applied = 0;
            Dictionary<int, int> typeCounts = new Dictionary<int, int>();
            int speedSet = 0;
            int energySet = 0;
            for (int i = 0; i < buildings.Count; i++)
            {
                RegisteredBuilding building = buildings[i];
                if (building.Item == null)
                {
                    continue;
                }
                PrefabDesc desc = building.Item.prefabDesc;
                if (desc == null && building.Model != null)
                {
                    desc = building.Model.prefabDesc;
                }
                if (desc == null)
                {
                    continue;
                }
                try
                {
                    if (building.Def.RecipeType >= BuildingRecipeTypeMin && building.Def.RecipeType <= BuildingRecipeTypeMax)
                    {
                        ERecipeType type = (ERecipeType)(byte)building.Def.RecipeType;
                        desc.assemblerRecipeType = type;
                        if (building.Model != null && building.Model.prefabDesc != null &&
                            !ReferenceEquals(building.Model.prefabDesc, desc))
                        {
                            building.Model.prefabDesc.assemblerRecipeType = type;
                        }
                        int count;
                        typeCounts.TryGetValue(building.Def.RecipeType, out count);
                        typeCounts[building.Def.RecipeType] = count + 1;
                    }
                    if (building.Def.AssemblerSpeed > 0)
                    {
                        desc.assemblerSpeed = (int)Math.Round(building.Def.AssemblerSpeed * 10000.0);
                        speedSet++;
                    }
                    if (building.Def.WorkEnergyPerTick > 0)
                    {
                        desc.workEnergyPerTick = building.Def.WorkEnergyPerTick;
                        energySet++;
                    }
                    if (building.Def.IdleEnergyPerTick > 0)
                    {
                        desc.idleEnergyPerTick = building.Def.IdleEnergyPerTick;
                        energySet++;
                    }
                    applied++;
                }
                catch (Exception ex)
                {
                    log.LogError("[PyPort] 建筑 item " + building.Def.ItemId + " 写 prefabDesc 失败：" + ex.Message);
                }
            }
            if (verbose || (buildings.Count > 0 && applied == buildings.Count))
            {
                string types = string.Empty;
                for (int type = BuildingRecipeTypeMin; type <= BuildingRecipeTypeMax; type++)
                {
                    int count;
                    if (typeCounts.TryGetValue(type, out count))
                    {
                        if (types.Length > 0)
                        {
                            types += "、";
                        }
                        types += type + "→" + count + " 座";
                    }
                }
                log.LogInfo(string.Format(
                    "[PyPort] 建筑 prefabDesc 回填：{0}/{1} 处（assemblerRecipeType：{2}；速度 {3} 座，耗电 {4} 项）",
                    applied, buildings.Count, types.Length > 0 ? types : "无自定义类型", speedSet, energySet));
            }
            return applied;
        }

        private void ApplyRecipeFlags(bool verbose)
        {
            int applied = 0;
            for (int i = 0; i < recipes.Count; i++)
            {
                RegisteredRecipe recipe = recipes[i];
                if (recipe.Proto == null)
                {
                    continue;
                }
                try
                {
                    recipe.Proto.Type = (ERecipeType)(byte)recipe.Def.Type;
                    recipe.Proto.Handcraft = recipe.Def.Handcraft;
                    recipe.Proto.Explicit = false;
                    if (recipe.AssignedGridIndex >= RecipeGridFloor)
                    {
                        recipe.Proto.GridIndex = recipe.AssignedGridIndex;
                    }
                    applied++;
                }
                catch (Exception ex)
                {
                    log.LogWarning("[PyPort] 回填配方 " + recipe.Def.Id + " 标志失败：" + ex.Message);
                }
            }
            if (verbose)
            {
                log.LogInfo("[PyPort] 配方标志回填：" + applied + "/" + recipes.Count + " 条");
            }
        }

        private void AppendRecipeUnlocksToTechs(bool verbose)
        {
            int added = 0;
            int touched = 0;
            int missingTech = 0;
            for (int i = 0; i < recipes.Count; i++)
            {
                RegisteredRecipe recipe = recipes[i];
                int techId = recipe.Def.TechId;
                if (techId <= 0)
                {
                    continue;
                }
                TechProto tech = FindRegisteredTech(techId);
                if (tech == null)
                {
                    tech = LDB.techs.Select(techId);
                }
                if (tech == null)
                {
                    missingTech++;
                    continue;
                }
                int recipeId = recipe.Proto.ID;
                int[] unlock = tech.UnlockRecipes ?? new int[0];
                bool exists = false;
                for (int j = 0; j < unlock.Length; j++)
                {
                    if (unlock[j] == recipeId)
                    {
                        exists = true;
                        break;
                    }
                }
                if (exists)
                {
                    continue;
                }
                int[] merged = new int[unlock.Length + 1];
                Array.Copy(unlock, merged, unlock.Length);
                merged[unlock.Length] = recipeId;
                tech.UnlockRecipes = merged;
                added++;
                touched++;
            }
            if (missingTech > 0)
            {
                log.LogWarning("[PyPort] 有 " + missingTech + " 条配方的 tech_id 找不到对应科技（解锁链会断）");
            }
            if (verbose || added > 0)
            {
                log.LogInfo("[PyPort] 解锁闭环：配方→科技 UnlockRecipes 补入 " + added + " 条（涉及 " + touched + " 个科技）");
            }
        }

        private TechProto FindRegisteredTech(int techId)
        {
            for (int i = 0; i < techs.Count; i++)
            {
                if (techs[i].ExpectedId == techId)
                {
                    return techs[i].Proto;
                }
            }
            return null;
        }

        // ------------------------------------------------------------------ icons
        private void ApplyIcons()
        {
            List<IconRequest> requests = new List<IconRequest>();
            List<RecipeProto> followProduct = new List<RecipeProto>();

            for (int i = 0; i < items.Count; i++)
            {
                IconRequest request = BuildIconRequest(IconTargetKind.Item, items[i].Def.IconFile, items[i].Proto, items[i].Def.Id);
                if (request != null)
                {
                    requests.Add(request);
                }
            }
            for (int i = 0; i < buildings.Count; i++)
            {
                IconRequest request = BuildIconRequest(IconTargetKind.Item, buildings[i].Def.IconFile, buildings[i].Item, buildings[i].Def.ItemId);
                if (request != null)
                {
                    requests.Add(request);
                }
            }
            for (int i = 0; i < techs.Count; i++)
            {
                IconRequest request = BuildIconRequest(IconTargetKind.Tech, techs[i].Def.IconFile, techs[i].Proto, techs[i].Def.Id);
                if (request != null)
                {
                    requests.Add(request);
                }
            }
            for (int i = 0; i < recipes.Count; i++)
            {
                if (!string.IsNullOrEmpty(recipes[i].Def.IconFile))
                {
                    IconRequest request = BuildIconRequest(IconTargetKind.Recipe, recipes[i].Def.IconFile, recipes[i].Proto, recipes[i].Def.Id);
                    if (request != null)
                    {
                        requests.Add(request);
                    }
                }
                else
                {
                    followProduct.Add(recipes[i].Proto);
                }
            }

            IconLoader.ApplyAll(requests, followProduct, contentRoot, log);
        }

        private static IconRequest BuildIconRequest(IconTargetKind kind, string iconFile, Proto target, int id)
        {
            if (string.IsNullOrEmpty(iconFile) || target == null)
            {
                return null;
            }
            IconRequest request = new IconRequest();
            request.Kind = kind;
            request.Target = target;
            request.FileName = iconFile;
            request.Id = id;
            return request;
        }
    }
}
