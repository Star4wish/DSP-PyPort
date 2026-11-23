using System;
using System.Collections.Generic;
using CommonAPI.Systems;
using CommonAPI.Systems.ModLocalization;
using UnityEngine;

namespace DSPPyPort.Core
{
    internal sealed partial class Registrar
    {
        // ------------------------------------------------------------------ recipes
        private void RegisterRecipes()
        {
            int ok = 0;
            int tried = 0;
            for (int m = 0; m < content.Modules.Count; m++)
            {
                ModuleContent module = content.Modules[m];
                if (module.Refused)
                {
                    continue;
                }
                for (int i = 0; i < module.Recipes.Count; i++)
                {
                    RecipeDef def = module.Recipes[i];
                    if (def.Skip)
                    {
                        skippedInvalid++;
                        log.LogWarning("[PyPort] 跳过配方 " + def.Id + "：" + def.SkipReason);
                        continue;
                    }
                    tried++;
                    if (!MarkRecipeId(def.Id, module.Id))
                    {
                        continue;
                    }
                    try
                    {
                        if (def.GridIndex < RecipeGridFloor)
                        {
                            def.GridIndex = nextRecipeGrid++;
                        }
                        string key = def.EffectiveKey;
                        string description = string.IsNullOrEmpty(def.DescZh) ? string.Empty : key + "_Desc";
                        string iconPath = IconLoader.RecipeIconPath(def.IconFile, def.IconSource);
                        ERecipeType type = (ERecipeType)(byte)def.Type;
                        RecipeProto proto = ProtoRegistry.RegisterRecipe(
                            def.Id,
                            type,
                            def.TimeTicks,
                            def.Inputs,
                            def.InCounts,
                            def.Outputs,
                            def.OutCounts,
                            description,
                            def.TechId,
                            def.GridIndex,
                            key + "_Name",
                            iconPath);
                        if (proto == null)
                        {
                            log.LogError("[PyPort] 注册配方 " + def.Id + " 返回 null");
                            continue;
                        }
                        proto.Handcraft = def.Handcraft;
                        proto.Explicit = false;
                        if (def.IsWide)
                        {
                            wideRecipeCount++;
                        }
                        recipes.Add(new RegisteredRecipe
                        {
                            Def = def,
                            Proto = proto,
                            ExpectedId = def.Id,
                            AssignedGridIndex = def.GridIndex
                        });
                        IconLoader.RegisterRecipe(def.Id, def.IconFile, def.IconSource);
                        RecipeCategories.Set(def.Id, def.Category);
                        AddRecipeTranslations(def);
                        ok++;
                    }
                    catch (Exception ex)
                    {
                        log.LogError("[PyPort] 注册配方 " + def.Id + " 失败：" + ex);
                    }
                }
            }
            log.LogInfo(string.Format("[PyPort] 配方注册：{0}/{1}（宽配方 >6 原料或 >2 产物：{2} 条）", ok, tried, wideRecipeCount));
        }

        // ------------------------------------------------------------------ techs
        private void RegisterTechs()
        {
            int ok = 0;
            int tried = 0;
            for (int m = 0; m < content.Modules.Count; m++)
            {
                ModuleContent module = content.Modules[m];
                if (module.Refused)
                {
                    continue;
                }
                for (int i = 0; i < module.Techs.Count; i++)
                {
                    TechDef def = module.Techs[i];
                    if (def.Skip)
                    {
                        skippedInvalid++;
                        log.LogWarning("[PyPort] 跳过科技 " + def.Id + "：" + def.SkipReason);
                        continue;
                    }
                    tried++;
                    if (!MarkTechId(def.Id, module.Id))
                    {
                        continue;
                    }
                    try
                    {
                        // 替换模式：占用原版主页科技槽位（ID 1..1999 且注册前已存在）时就地覆盖，
                        // 不新建 proto —— TechProto.page 只按 ID 判定主页/升级页，新 proto 的 ID 无法 <2000。
                        if (IsTechSlotToReplace(def.Id))
                        {
                            if (ReplaceVanillaTech(def))
                            {
                                ok++;
                            }
                            continue;
                        }
                        string key = def.EffectiveKey;
                        Vector2 position = def.HasPosition
                            ? new Vector2(def.PosX, def.PosY)
                            : new Vector2(60f, -10f - (nextTechPositionIndex++ * 4f));
                        string iconPath = ResolveIconPath(def.IconFile, def.IconSource, false, "科技 " + def.Id);
                        TechProto proto = ProtoRegistry.RegisterTech(
                            def.Id,
                            key + "_Name",
                            key + "_Desc",
                            key + "_Conclusion",
                            iconPath,
                            def.PreTechs,
                            def.Matrices,
                            def.MatrixPoints,
                            def.HashNeeded,
                            def.UnlockRecipes,
                            position);
                        if (proto == null)
                        {
                            log.LogError("[PyPort] 注册科技 " + def.Id + " 返回 null");
                            continue;
                        }
                        // 契约要求：IsLabTech 一律显式置 true（不依赖 CommonAPI 用 LabComponent.matrixIds 的自动判定，
                        // 自建矩阵 / 7+ 矩阵时自动判定会失败，手册 §5.3）。
                        proto.IsLabTech = true;
                        proto.IsHiddenTech = def.Hidden;
                        proto.PreTechs = def.PreTechs;
                        proto.PreTechsImplicit = new int[0];
                        techs.Add(new RegisteredTech { Def = def, Proto = proto, ExpectedId = def.Id });
                        IconLoader.RegisterTech(def.Id, def.IconFile, def.IconSource);
                        AddTechTranslations(def);
                        newTechCount++;
                        ok++;
                    }
                    catch (Exception ex)
                    {
                        log.LogError("[PyPort] 注册科技 " + def.Id + " 失败：" + ex);
                    }
                }
            }
            log.LogInfo(string.Format("[PyPort] 科技注册：{0}/{1}", ok, tried));
            HideUnreplacedVanillaMainTechs();
            LogTechReplacementSummary();
        }

        // ------------------------------------------------------------------ localization
        private void RegisterLocalization()
        {
            // 1) 内联 name_zh/name_en 已在上面的 Register* 里写入 translations；
            // 2) content/locale/*.json 覆盖内联值；
            // 3) 英文缺失时回退中文（首期内容包只提供 zh-CN）。
            foreach (KeyValuePair<string, string> pair in content.LocaleZh)
            {
                string en;
                content.LocaleEn.TryGetValue(pair.Key, out en);
                AddTranslation(pair.Key, en, pair.Value);
            }
            foreach (KeyValuePair<string, string> pair in content.LocaleEn)
            {
                if (content.LocaleZh.ContainsKey(pair.Key))
                {
                    continue;
                }
                AddTranslation(pair.Key, pair.Value, null);
            }

            int enFallback = 0;
            foreach (KeyValuePair<string, Translation> pair in translations)
            {
                Translation translation = pair.Value;
                if (string.IsNullOrEmpty(translation.En) && !string.IsNullOrEmpty(translation.Cn))
                {
                    translation.En = translation.Cn;
                    enFallback++;
                }
            }

            int ok = 0;
            int failed = 0;
            foreach (KeyValuePair<string, Translation> pair in translations)
            {
                Translation translation = pair.Value;
                if (string.IsNullOrEmpty(translation.En) && string.IsNullOrEmpty(translation.Cn))
                {
                    continue;
                }
                try
                {
                    LocalizationModule.RegisterTranslation(
                        pair.Key,
                        translation.En ?? string.Empty,
                        translation.Cn ?? string.Empty,
                        string.Empty);
                    ok++;
                }
                catch (Exception ex)
                {
                    failed++;
                    if (failed <= 5)
                    {
                        log.LogError("[PyPort] 注册文案失败 " + pair.Key + "：" + ex.Message);
                    }
                }
            }
            localeCount = ok;
            log.LogInfo("[PyPort] 本地化注册：" + ok + " 条" +
                        (failed > 0 ? "（失败 " + failed + " 条）" : string.Empty) +
                        (missingNameCount > 0 ? "；无内联名称（由 locale 文件补齐）" + missingNameCount + " 条" : string.Empty) +
                        (enFallback > 0 ? "；英文缺失回退中文 " + enFallback + " 条" : string.Empty));
        }

        private void AddRecipeTranslations(RecipeDef def)
        {
            string key = def.EffectiveKey;
            AddNamedTranslation(key + "_Name", def.NameZh, def.NameEn);
            if (!string.IsNullOrEmpty(def.DescZh))
            {
                AddTranslation(key + "_Desc", def.DescZh, def.DescZh);
            }
        }

        private void AddTechTranslations(TechDef def)
        {
            string key = def.EffectiveKey;
            AddNamedTranslation(key + "_Name", def.NameZh, def.NameEn);
            if (!string.IsNullOrEmpty(def.DescZh))
            {
                AddTranslation(key + "_Desc", def.DescZh, def.DescZh);
            }
            if (!string.IsNullOrEmpty(def.ConclusionZh))
            {
                AddTranslation(key + "_Conclusion", def.ConclusionZh, def.ConclusionZh);
            }
        }
    }
}
