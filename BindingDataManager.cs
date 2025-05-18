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

namespace QuickCast
{
    public static class BindingDataManager
    {
        private static Dictionary<string, Dictionary<int, Dictionary<int, string>>> PerCharacterQuickCastSpellIds = new Dictionary<string, Dictionary<int, Dictionary<int, string>>>();
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

        internal static Dictionary<int, Dictionary<int, string>> GetCurrentCharacterBindings(bool createIfMissing = false)
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
                    bindings = new Dictionary<int, Dictionary<int, string>>();
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

        internal static AbilityData GetAbilityDataFromSpellGuid(UnitEntityData unit, string spellGuid)
        {
            if (unit == null || string.IsNullOrEmpty(spellGuid) || unit.Descriptor == null)
            {
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
                            if (slot.SpellShell != null && slot.SpellShell.Blueprint.AssetGuidThreadSafe == spellGuid)
                            {
                                return slot.SpellShell;
                            }
                        }
                    }
                    var cantripAbility = spellbook.GetKnownSpells(0).FirstOrDefault(a => a.Blueprint.AssetGuidThreadSafe == spellGuid);
                    if (cantripAbility != null)
                    {
                        return cantripAbility;
                    }
                }
                else if (spellbook.Blueprint.Spontaneous)
                {
                    var ability = spellbook.GetAllKnownSpells().FirstOrDefault(a => a.Blueprint.AssetGuidThreadSafe == spellGuid);
                    if (ability != null)
                    {
                        return ability;
                    }
                }
            }
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
                    var loadedBindings = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<int, Dictionary<int, string>>>>(json);
                    if (loadedBindings != null)
                    {
                        PerCharacterQuickCastSpellIds = loadedBindings;
                        LogDebug($"[BindingDataManager LoadBindings] Successfully loaded bindings from {filePath}");
                    }
                    else
                    {
                        Debug.LogWarning($"[QuickCast BindingDataManager LoadBindings] Deserialized bindings from {filePath} are null. Using new/empty bindings.");
                        PerCharacterQuickCastSpellIds = new Dictionary<string, Dictionary<int, Dictionary<int, string>>>();
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[QuickCast BindingDataManager LoadBindings] Error loading bindings from {filePath}: {ex.ToString()}. Using new/empty bindings.");
                    PerCharacterQuickCastSpellIds = new Dictionary<string, Dictionary<int, Dictionary<int, string>>>();
                }
            }
            else
            {
                LogDebug($"[BindingDataManager LoadBindings] Bindings file {filePath} not found. Starting with new/empty bindings.");
                PerCharacterQuickCastSpellIds = new Dictionary<string, Dictionary<int, Dictionary<int, string>>>();
            }
        }

        internal static bool ValidateAndCleanupBindings(UnitEntityData unit, Dictionary<int, Dictionary<int, string>> characterSpellIdBindings)
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
                    string spellGuid = slotEntry.Value;

                    AbilityData ability = GetAbilityDataFromSpellGuid(unit, spellGuid);
                    if (ability == null)
                    {
                        Log($"[BindingDataManager ValidateBindings] Invalid binding for unit {unit.CharacterName}: Lvl {spellLevel}, Slot {logicalSlot}, GUID {spellGuid}. Marking for removal.");
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