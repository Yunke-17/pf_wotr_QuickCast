using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.UnitLogic.Abilities.Blueprints;
using UnityModManagerNet;
using Newtonsoft.Json; // For JsonConvert
using System.IO; // For Path, File
using UnityEngine; // For Debug.LogError
using Kingmaker.Blueprints;
using Kingmaker.RuleSystem.Rules;
using Kingmaker.RuleSystem;

namespace QuickCast
{
    public static class BindingDataManager
    {
        private static Dictionary<string, Dictionary<int, Dictionary<int, Tuple<string, int, int, int, int>>>> PerCharacterQuickCastSpellIds =
            new Dictionary<string, Dictionary<int, Dictionary<int, Tuple<string, int, int, int, int>>>>();
        private const string BindingsFileName = "QuickCastCharacterBindings.json";

        // Helper for logging, assuming Main.Log is accessible or we add a local one if needed.
        private static void Log(string message) => Main.ModEntry?.Logger.Log(message);
        private static void LogDebug(string message)
        {
            if (Main.Settings != null && Main.Settings.EnableVerboseLogging)
            {
                Main.ModEntry?.Logger.Log("[DEBUG] " + message);
            }
        }

        internal static Dictionary<int, Dictionary<int, Tuple<string, int, int, int, int>>> GetCurrentCharacterBindings(bool createIfMissing = false)
        {
            var selectedUnit = Kingmaker.Game.Instance?.SelectionCharacter?.CurrentSelectedCharacter;
            if (selectedUnit == null || selectedUnit.UniqueId == null)
            {
                Log("[BindingDataManager GetCurrentCharBindings] Error: No selected unit or unit UniqueId is null.");
                return null;
            }

            string charId = selectedUnit.UniqueId;
            if (!PerCharacterQuickCastSpellIds.TryGetValue(charId, out var bindings))
            {
                if (createIfMissing)
                {
                    LogDebug($"[BindingDataManager GetCurrentCharBindings] No bindings found for char {selectedUnit.CharacterName} ({charId}). Creating new set.");
                    bindings = new Dictionary<int, Dictionary<int, Tuple<string, int, int, int, int>>>();
                    PerCharacterQuickCastSpellIds[charId] = bindings;
                }
                else
                {
                    LogDebug($"[BindingDataManager GetCurrentCharBindings] No bindings found for char {selectedUnit.CharacterName} ({charId}). Not creating.");
                    return null;
                }
            }
            return bindings;
        }

        internal static AbilityData GetBoundAbilityData(UnitEntityData unit, string spellGuid, int metamagicMask, int storedHeightenLevel, int storedDecorationColor, int storedDecorationBorder)
        {
            if (unit == null || string.IsNullOrEmpty(spellGuid) || unit.Descriptor == null)
            {
                return null;
            }

            BlueprintAbility blueprint = ResourcesLibrary.TryGetBlueprint<BlueprintAbility>(spellGuid);
            if (blueprint == null)
            {
                LogDebug($"[BindingDataManager GetBoundAbilityData] Blueprint with GUID {spellGuid} not found.");
                return null;
            }

            foreach (var spellbook in unit.Descriptor.Spellbooks)
            {
                if (spellbook.Blueprint.MemorizeSpells)
                {
                    for (int spellLevel = 0; spellLevel <= spellbook.MaxSpellLevel; spellLevel++)
                    {
                        var memorizedSlots = spellbook.GetMemorizedSpellSlots(spellLevel);
                        foreach (var slot in memorizedSlots)
                        {
                            if (slot.SpellShell != null &&
                                slot.SpellShell.Blueprint.AssetGuidThreadSafe == spellGuid &&
                                (slot.SpellShell.MetamagicData != null ? (int)slot.SpellShell.MetamagicData.MetamagicMask : 0) == metamagicMask &&
                                slot.SpellShell.DecorationColorNumber == storedDecorationColor &&
                                slot.SpellShell.DecorationBorderNumber == storedDecorationBorder)
                            {
                                return slot.SpellShell;
                            }
                        }
                    }
                    // Check cantrips (level 0 spells)
                    var cantripAbility = spellbook.GetKnownSpells(0).FirstOrDefault(a =>
                        a.Blueprint.AssetGuidThreadSafe == spellGuid &&
                        (a.MetamagicData != null ? (int)a.MetamagicData.MetamagicMask : 0) == metamagicMask &&
                        a.DecorationColorNumber == storedDecorationColor &&
                        a.DecorationBorderNumber == storedDecorationBorder);
                    if (cantripAbility != null)
                    {
                        return cantripAbility;
                    }
                }
                else if (spellbook.Blueprint.Spontaneous)
                {
                    AbilityData baseAbilityData = spellbook.GetKnownSpell(blueprint);
                    if (baseAbilityData == null)
                    {
                        // LogDebug($"[BindingDataManager GetBoundAbilityData] Spontaneous: Base spell {blueprint.Name} (GUID: {spellGuid}) not found in spellbook {spellbook.Blueprint.Name} for unit {unit.CharacterName}.");
                        continue; 
                    }

                    if (metamagicMask == 0)
                    {
                        // Expecting a spell without metamagic.
                        // Return baseAbilityData only if it also has no metamagic.
                        if (baseAbilityData.MetamagicData == null || ((int)baseAbilityData.MetamagicData.MetamagicMask == 0 && baseAbilityData.MetamagicData.HeightenLevel == 0))
                        {
                            return baseAbilityData;
                        }
                        continue; 
                    }

                    // Metamagic is requested (metamagicMask != 0 or storedHeightenLevel != 0)
                    int baseSpellbookLevel = spellbook.GetMinSpellLevel(blueprint);
                    if (baseSpellbookLevel < 0)
                    {
                        LogDebug($"[BindingDataManager GetBoundAbilityData] Spontaneous: Spell {blueprint.Name} (GUID: {spellGuid}) not in spellbook {spellbook.Blueprint.Name} (GetMinSpellLevel) for unit {unit.CharacterName}.");
                        continue;
                    }

                    List<Metamagic> appliedMetamagics = new List<Metamagic>();
                    foreach (Metamagic m_flag in Enum.GetValues(typeof(Metamagic)))
                    {
                        // Metamagic enum values are flags, 0 is not a defined Metamagic member representing "None"
                        // So, we just check if the flag is set in the mask.
                        if (((Metamagic)metamagicMask & m_flag) == m_flag && (int)m_flag != 0) // Ensure we don't add a hypothetical "0" value if it existed
                        {
                            appliedMetamagics.Add(m_flag);
                        }
                    }

                    if (appliedMetamagics.Count == 0 && metamagicMask != 0) {
                         LogDebug($"[BindingDataManager GetBoundAbilityData] Spontaneous: metamagicMask {metamagicMask} resulted in an empty appliedMetamagics list for {blueprint.Name}. This is unexpected.");
                         continue;
                    }
                    
                    // Determine the heighten level to pass to the rule.
                    // If Metamagic.Heighten is part of the mask, use the storedHeightenLevel. Otherwise, pass 0.
                    int effectiveHeightenLevelForRule = 0;
                    if (appliedMetamagics.Contains(Metamagic.Heighten) && storedHeightenLevel > 0)
                    {
                        effectiveHeightenLevelForRule = storedHeightenLevel;
                    }

                    RuleApplyMetamagic rule = new RuleApplyMetamagic(spellbook, blueprint, appliedMetamagics, baseSpellbookLevel, effectiveHeightenLevelForRule); 
                    Rulebook.Trigger(rule);
                    MetamagicData resultMetamagicDataFromRule = rule.Result;

                    if (resultMetamagicDataFromRule == null)
                    {
                        LogDebug($"[BindingDataManager GetBoundAbilityData] Spontaneous: RuleApplyMetamagic for {blueprint.Name} with applied metamagics [{string.Join(", ", appliedMetamagics)}] (from mask {metamagicMask}) returned null MetamagicData.");
                        continue;
                    }

                    // Construct the AbilityData with the MetamagicData from the rule.
                    AbilityData reconstructedAbility = new AbilityData(blueprint, spellbook, baseSpellbookLevel);
                    reconstructedAbility.MetamagicData = resultMetamagicDataFromRule;
                    reconstructedAbility.DecorationColorNumber = storedDecorationColor;
                    reconstructedAbility.DecorationBorderNumber = storedDecorationBorder;
                    
                    int finalSpellLevelByRule = spellbook.GetSpellLevel(reconstructedAbility); // Uses MetamagicData.SpellLevelCost

                    LogDebug($"[BindingDataManager GetBoundAbilityData] Spontaneous: Reconstructed {blueprint.Name} for unit {unit.CharacterName}. " +
                             $"Original Mask Int: {metamagicMask} (Flags: {(Metamagic)metamagicMask}). " +
                             $"Applied List: [{string.Join(", ", appliedMetamagics)}]. " +
                             $"Rule Result: Mask='{resultMetamagicDataFromRule.MetamagicMask}', Cost={resultMetamagicDataFromRule.SpellLevelCost}, HeightenLvl={resultMetamagicDataFromRule.HeightenLevel}. " +
                             $"Final Level by Rule: {finalSpellLevelByRule}.");

                    // For binding retrieval, we ideally want an exact match to the stored metamagicMask AND storedHeightenLevel.
                    // The rule's resulting HeightenLevel should also match if Heighten was applied.
                    bool heightLevelMatches = true;
                    if (appliedMetamagics.Contains(Metamagic.Heighten)) {
                        heightLevelMatches = resultMetamagicDataFromRule.HeightenLevel == storedHeightenLevel;
                    }

                    if ((int)resultMetamagicDataFromRule.MetamagicMask == metamagicMask && heightLevelMatches)
                    {
                        return reconstructedAbility;
                    }
                    else
                    {
                        // This indicates a discrepancy. 
                        Log($"[BindingDataManager GetBoundAbilityData] Spontaneous: Mismatch for {blueprint.Name} (Unit: {unit.CharacterName}). " +
                            $"Stored Mask: {metamagicMask} ({(Metamagic)metamagicMask}), Rule Mask: {(int)resultMetamagicDataFromRule.MetamagicMask} ({resultMetamagicDataFromRule.MetamagicMask}). " +
                            $"Stored Heighten: {storedHeightenLevel}, Rule Heighten: {resultMetamagicDataFromRule.HeightenLevel}. " +
                            $"Mismatch Components: MaskSame={((int)resultMetamagicDataFromRule.MetamagicMask == metamagicMask)}, HeightenSame={heightLevelMatches}.");
                        LogDebug($"[BindingDataManager GetBoundAbilityData] Spontaneous: Declining reconstructed {blueprint.Name} due to MetamagicMask or HeightenLevel mismatch.");
                        continue; 
                    }
                }
            }
            LogDebug($"[BindingDataManager GetBoundAbilityData] Spell {blueprint?.Name ?? "UnknownSpell"} (GUID {spellGuid}, Meta {metamagicMask}) not found for unit {unit.CharacterName} after checking all spellbooks.");
            return null;
        }

        public static void SaveBindings(UnityModManager.ModEntry modEntry)
        {
            if (modEntry == null || string.IsNullOrEmpty(modEntry.Path))
            {
                Debug.LogError("[QuickCast BindingDataManager SaveBindings] ModEntry or ModEntry.Path is null. Cannot determine save path.");
                return;
            }

            string filePath = Path.Combine(modEntry.Path, BindingsFileName);
            try
            {
                string json = JsonConvert.SerializeObject(PerCharacterQuickCastSpellIds, Formatting.Indented);
                File.WriteAllText(filePath, json);
                LogDebug($"[BindingDataManager SaveBindings] Successfully saved bindings to {filePath}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[QuickCast BindingDataManager SaveBindings] Error saving bindings to {filePath}: {ex.ToString()}");
            }
        }

        public static void LoadBindings(UnityModManager.ModEntry modEntry)
        {
            if (modEntry == null || string.IsNullOrEmpty(modEntry.Path))
            {
                Debug.LogError("[QuickCast BindingDataManager LoadBindings] ModEntry or ModEntry.Path is null. Cannot determine load path.");
                return;
            }

            string filePath = Path.Combine(modEntry.Path, BindingsFileName);
            if (File.Exists(filePath))
            {
                try
                {
                    string json = File.ReadAllText(filePath);
                    var loadedBindings = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<int, Dictionary<int, Tuple<string, int, int, int, int>>>>>(json);
                    if (loadedBindings != null)
                    {
                        PerCharacterQuickCastSpellIds = loadedBindings;
                        LogDebug($"[BindingDataManager LoadBindings] Successfully loaded bindings from {filePath}");
                    }
                    else
                    {
                        Debug.LogWarning($"[QuickCast BindingDataManager LoadBindings] Deserialized bindings from {filePath} are null. Using new/empty bindings.");
                        PerCharacterQuickCastSpellIds = new Dictionary<string, Dictionary<int, Dictionary<int, Tuple<string, int, int, int, int>>>>();
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[QuickCast BindingDataManager LoadBindings] Error loading bindings from {filePath}: {ex.ToString()}. Using new/empty bindings.");
                    PerCharacterQuickCastSpellIds = new Dictionary<string, Dictionary<int, Dictionary<int, Tuple<string, int, int, int, int>>>>();
                }
            }
            else
            {
                LogDebug($"[BindingDataManager LoadBindings] Bindings file {filePath} not found. Starting with new/empty bindings.");
                PerCharacterQuickCastSpellIds = new Dictionary<string, Dictionary<int, Dictionary<int, Tuple<string, int, int, int, int>>>>();
            }
        }

        internal static bool ValidateAndCleanupBindings(UnitEntityData unit, Dictionary<int, Dictionary<int, Tuple<string, int, int, int, int>>> characterSpellIdBindings)
        {
            if (unit == null || characterSpellIdBindings == null)
            {
                LogDebug("[BindingDataManager ValidateBindings] Unit or bindings dictionary is null. Skipping validation.");
                return false;
            }

            bool bindingsChanged = false;
            List<Tuple<int, int>> bindingsToRemove = new List<Tuple<int, int>>();

            LogDebug($"[BindingDataManager ValidateBindings] Validating bindings for unit {unit.CharacterName}...");

            foreach (var levelEntry in characterSpellIdBindings.ToList()) // Use ToList() for safe modification
            {
                int spellLevel = levelEntry.Key;
                var slotBindings = levelEntry.Value;
                // List<int> slotsInLevelToRemove = new List<int>(); // Not needed with Tuple list

                foreach (var slotEntry in slotBindings.ToList()) // Use ToList() for safe modification
                {
                    int logicalSlot = slotEntry.Key;
                    Tuple<string, int, int, int, int> spellInfo = slotEntry.Value; // spellInfo is (GUID, MetaMask, HeightenLvl, DecoColor, DecoBorder)

                    if (spellInfo == null) // Should not happen if data is saved correctly, but good to check
                    {
                        Log($"[BindingDataManager ValidateBindings] Invalid NULL spellInfo for unit {unit.CharacterName}: Lvl {spellLevel}, Slot {logicalSlot}. Marking for removal.");
                        bindingsToRemove.Add(Tuple.Create(spellLevel, logicalSlot));
                        bindingsChanged = true;
                        continue;
                    }
                    
                    string spellGuid = spellInfo.Item1;
                    int metamagicMask = spellInfo.Item2;
                    int targetHeightenLevel = spellInfo.Item3;
                    int decorationColor = spellInfo.Item4;
                    int decorationBorder = spellInfo.Item5;

                    AbilityData ability = GetBoundAbilityData(unit, spellGuid, metamagicMask, targetHeightenLevel, decorationColor, decorationBorder);
                    if (ability == null)
                    {
                        Log($"[BindingDataManager ValidateBindings] Invalid binding for unit {unit.CharacterName}: Lvl {spellLevel}, Slot {logicalSlot}, GUID {spellGuid}, MetaMask {metamagicMask}, Heighten {targetHeightenLevel}, DecoColor {decorationColor}, DecoBorder {decorationBorder}. Marking for removal.");
                        bindingsToRemove.Add(Tuple.Create(spellLevel, logicalSlot));
                        bindingsChanged = true;
                    }
                }
            }

            foreach (var itemToRemove in bindingsToRemove)
            {
                if (characterSpellIdBindings.TryGetValue(itemToRemove.Item1, out var slotsInLevel))
                {
                    slotsInLevel.Remove(itemToRemove.Item2);
                    if (slotsInLevel.Count == 0)
                    {
                        characterSpellIdBindings.Remove(itemToRemove.Item1);
                    }
                }
            }

            if (bindingsChanged)
            {
                LogDebug($"[BindingDataManager ValidateBindings] Finished for {unit.CharacterName}. Bindings modified. Saving...");
                SaveBindings(Main.ModEntry); 
            }
            else
            {
                LogDebug($"[BindingDataManager ValidateBindings] Finished for {unit.CharacterName}. No changes.");
            }
            return bindingsChanged;
        }
    }
} 