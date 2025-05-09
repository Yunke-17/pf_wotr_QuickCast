using System;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityModManagerNet;
using Kingmaker;
using Kingmaker.PubSubSystem;
using Kingmaker.UI.MVVM._PCView.ActionBar;
using Kingmaker.UI.MVVM._VM.ActionBar;
using Kingmaker.GameModes;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic;
using System.Collections.Generic;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.UI.UnitSettings;
using Kingmaker.UnitLogic.Abilities.Blueprints;
using Kingmaker.UnitLogic.Commands;

namespace QuickCast
{
    public class ActionBarManager
    {
        private int _activeQuickCastPage = -1;
        private bool _isQuickCastModeActive = false;

        // Stores the original content of the main action bar slots that were overridden by the mod
        private readonly Dictionary<int, MechanicActionBarSlot> _mainBarOverriddenSlotsOriginalContent;
        // Maps a cast key (e.g., KeyCode.Alpha1) to a main action bar slot index (e.g., 0)
        private readonly Dictionary<KeyCode, int> _castKeyToMainBarSlotIndexMapping;
        // Stores the mod's spell bindings: SpellLevel -> (CastKey -> AbilityData)
        public static Dictionary<int, Dictionary<KeyCode, AbilityData>> QuickCastBindings { get; set; } // Made static and public for broader access, will need proper init and persistence later

        private readonly MechanicActionBarSlotEmpty _emptySlotPlaceholder;

        private readonly UnityModManager.ModEntry _modEntry;
        private readonly Func<ActionBarPCView> _getActionBarPCView;
        private readonly Func<FieldInfo> _getSpellsGroupFieldInfo;

        // New static fields to track recently bound slots on the main action bar
        public static readonly HashSet<int> RecentlyBoundSlotHashes = new HashSet<int>();
        public static int FrameOfLastBindingRefresh = -1;

        public ActionBarManager(UnityModManager.ModEntry modEntry, Func<ActionBarPCView> getActionBarPCView, Func<FieldInfo> getSpellsGroupFieldInfo)
        {
            _modEntry = modEntry;
            _getActionBarPCView = getActionBarPCView;
            _getSpellsGroupFieldInfo = getSpellsGroupFieldInfo;

            _mainBarOverriddenSlotsOriginalContent = new Dictionary<int, MechanicActionBarSlot>();
            _emptySlotPlaceholder = new MechanicActionBarSlotEmpty();

            // Define which key presses correspond to which main action bar slots for quick casting
            // Example: KeyCode.Alpha1 (key '1') maps to action bar slot 0, Alpha2 to slot 1, etc.
            _castKeyToMainBarSlotIndexMapping = new Dictionary<KeyCode, int>
            {
                { KeyCode.Alpha1, 0 }, { KeyCode.Alpha2, 1 }, { KeyCode.Alpha3, 2 },
                { KeyCode.Alpha4, 3 }, { KeyCode.Alpha5, 4 }, { KeyCode.Alpha6, 5 }
                // We can extend this to more keys like 7,8,9,0,-,= if needed, mapping to slots 6-11
            };

            // Initialize QuickCastBindings - for now, empty.
            // In a real scenario, this would be loaded from a save file or UMM settings.
            if (QuickCastBindings == null) // Ensure it's initialized if static
            {
                 QuickCastBindings = new Dictionary<int, Dictionary<KeyCode, AbilityData>>();
            }
            // TODO: Add test data to QuickCastBindings for development if needed
            // Example:
            // if (!QuickCastBindings.ContainsKey(1)) QuickCastBindings[1] = new Dictionary<KeyCode, AbilityData>();
            // QuickCastBindings[1][KeyCode.Alpha1] = someDummyAbilityDataForLevel1Slot1;
        }

        // Public static method to clear the tracking info at the start of a new frame
        public static void ClearRecentlyBoundSlotsIfNewFrame()
        {
            if (Time.frameCount != FrameOfLastBindingRefresh)
            {
                RecentlyBoundSlotHashes.Clear();
                // FrameOfLastBindingRefresh will be updated when a refresh actually happens
            }
        }

        private void Log(string message) => _modEntry?.Logger.Log(message);

        public bool IsQuickCastModeActive => _isQuickCastModeActive;
        public int ActiveQuickCastPage => _activeQuickCastPage;

        // Expose the mapping for Main.cs to know which keys are cast keys
        public IReadOnlyDictionary<KeyCode, int> GetCastKeyToSlotMapping() => _castKeyToMainBarSlotIndexMapping;

        private void RestoreMainActionBarDisplay()
        {
            var actionBarVM = Game.Instance?.RootUiContext?.InGameVM?.StaticPartVM?.ActionBarVM;
            if (actionBarVM == null || actionBarVM.Slots == null)
            {
                Log("[ABM] Cannot restore action bar: ActionBarVM or Slots not available.");
                _mainBarOverriddenSlotsOriginalContent.Clear(); // Clear to prevent stale data issues
                return;
            }

            Log($"[ABM] Restoring main action bar display. {_mainBarOverriddenSlotsOriginalContent.Count} slots to restore.");
            foreach (var entry in _mainBarOverriddenSlotsOriginalContent)
            {
                int slotIndex = entry.Key;
                MechanicActionBarSlot originalContent = entry.Value;

                if (slotIndex >= 0 && slotIndex < actionBarVM.Slots.Count)
                {
                    ActionBarSlotVM slotVM = actionBarVM.Slots[slotIndex];
                    if (slotVM != null)
                    {
                        slotVM.SetMechanicSlot(originalContent);
                        Log($"[ABM] Restored slot {slotIndex}.");
                    }
                    else
                    {
                        Log($"[ABM] Warning: SlotVM at index {slotIndex} is null during restore.");
                    }
                }
                else
                {
                    Log($"[ABM] Warning: Invalid slot index {slotIndex} during restore.");
                }
            }
            _mainBarOverriddenSlotsOriginalContent.Clear();
        }

        // New private method to refresh the main action bar based on current bindings
        private void RefreshMainActionBarForCurrentQuickCastPage()
        {
            if (!_isQuickCastModeActive || _activeQuickCastPage == -1) return;

            var actionBarVM = Game.Instance?.RootUiContext?.InGameVM?.StaticPartVM?.ActionBarVM;
            var currentUnit = actionBarVM?.SelectedUnit.Value;

            if (actionBarVM == null || actionBarVM.Slots == null || currentUnit == null)
            {
                Log("[ABM] Cannot refresh action bar: ViewModel, Slots or Unit not available.");
                return;
            }

            // Clear at the beginning of a refresh operation if it's a new frame,
            // or ensure it's cleared if we are in the same frame but doing another refresh (e.g. multiple binds)
            if (Time.frameCount != FrameOfLastBindingRefresh) {
                RecentlyBoundSlotHashes.Clear();
            }
            FrameOfLastBindingRefresh = Time.frameCount; // Mark this frame as having a binding refresh

            Log($"[ABM] Refreshing main action bar for quick cast page {_activeQuickCastPage}.");
            Dictionary<KeyCode, AbilityData> currentLevelBindings = null;
            if (QuickCastBindings.TryGetValue(_activeQuickCastPage, out var bindingsForLevel))
            {
                currentLevelBindings = bindingsForLevel;
                Log($"[ABM RefreshDetails] Found {bindingsForLevel.Count} bindings for active page {_activeQuickCastPage}.");
                foreach (var kvp in bindingsForLevel)
                {
                    Log($"[ABM RefreshDetails]   Binding: Key {kvp.Key}, Spell: {kvp.Value?.Name ?? "NULL"}");
                }
            }
            else
            {
                Log($"[ABM RefreshDetails] No bindings dictionary found for active page {_activeQuickCastPage}.");
            }

            foreach (var mappingEntry in _castKeyToMainBarSlotIndexMapping)
            {
                KeyCode castKey = mappingEntry.Key;
                int slotIndexToRefresh = mappingEntry.Value;

                if (slotIndexToRefresh >= 0 && slotIndexToRefresh < actionBarVM.Slots.Count)
                {
                    ActionBarSlotVM slotVM = actionBarVM.Slots[slotIndexToRefresh];
                    if (slotVM != null)
                    {
                        // We assume the original content was already saved when TryActivateQuickCastPage was first called.
                        // Or, if called after a bind, the slot is already showing a QuickCast item or an empty placeholder.

                        AbilityData boundAbility = null;
                        currentLevelBindings?.TryGetValue(castKey, out boundAbility);

                        if (boundAbility != null)
                        {
                            var newSpellSlot = new QuickCastMechanicActionBarSlotSpell(boundAbility, currentUnit);
                            slotVM.SetMechanicSlot(newSpellSlot);
                            RecentlyBoundSlotHashes.Add(newSpellSlot.GetHashCode()); // Track this newly placed slot
                            Log($"[ABM] Refreshed Slot {slotIndexToRefresh} to spell: {boundAbility.Name}. Hash: {newSpellSlot.GetHashCode()}");
                            
                            // Corrected log to check VM state after setting slot
                            string iconName = slotVM.Icon.Value != null ? slotVM.Icon.Value.name : "null";
                            string spellName = slotVM.MechanicActionBarSlot?.GetTitle() ?? "null"; // Get title from the MechanicActionBarSlot
                            Log($"[ABM] After SetMechanicSlot for Slot {slotIndexToRefresh}: VM reports Icon as {iconName}, Spell as {spellName}");
                        }
                        else
                        {
                            slotVM.SetMechanicSlot(_emptySlotPlaceholder);
                            Log($"[ABM] Refreshed Slot {slotIndexToRefresh} cleared (no binding for key {castKey} on level {_activeQuickCastPage}).");
                        }
                    }
                }
            }
        }

        public void BindSpellToCastKey(int spellLevel, KeyCode castKey, AbilityData spellData)
        {
            if (spellData == null)
            {
                Log($"[ABM] BindSpellToCastKey: spellData is null for level {spellLevel}, key {castKey}. Cannot bind.");
                return;
            }

            if (!_castKeyToMainBarSlotIndexMapping.ContainsKey(castKey))
            {
                Log($"[ABM] BindSpellToCastKey: key {castKey} is not a registered cast key. Cannot bind.");
                return;
            }

            if (!QuickCastBindings.ContainsKey(spellLevel))
            {
                QuickCastBindings[spellLevel] = new Dictionary<KeyCode, AbilityData>();
            }
            QuickCastBindings[spellLevel][castKey] = spellData;
            Log($"[ABM] Spell '{spellData.Name}' (Lvl: {spellData.SpellLevel}, From UI Lvl: {spellLevel}) bound to CastKey {castKey}.");

            // Enhanced logging for binding
            Log($"[ABM BindDetails] Current Bindings for Level {spellLevel}:");
            if (QuickCastBindings.TryGetValue(spellLevel, out var bindings))
            {
                foreach (var kvp in bindings)
                {
                    Log($"[ABM BindDetails]   Key: {kvp.Key}, Spell: {kvp.Value?.Name ?? "NULL"}");
                }
            }
            else
            {
                Log("[ABM BindDetails]   No bindings found for this level after update.");
            }

            // If this binding is for the currently active quick cast page, refresh the action bar display immediately
            if (_isQuickCastModeActive && _activeQuickCastPage == spellLevel)
            {
                RefreshMainActionBarForCurrentQuickCastPage();
            }
            
            // TODO: Add UI feedback for binding success
            // TODO: Trigger save of QuickCastBindings (persistence)
        }

        public void TryActivateQuickCastPage(int targetPageLevel)
        {
            Log($"[ABM] Attempting to activate quick cast page: {targetPageLevel}");
            var game = Game.Instance;
            var actionBarVM = game?.RootUiContext?.InGameVM?.StaticPartVM?.ActionBarVM;
            var cachedView = _getActionBarPCView();
            var spellsField = _getSpellsGroupFieldInfo();

            if (cachedView == null || spellsField == null || actionBarVM == null || actionBarVM.Slots == null)
            {
                Log("[ABM] Error: ActionBarPCView, m_SpellsGroup, ActionBarVM or ActionBarVM.Slots is not available.");
                if (_isQuickCastModeActive) TryDeactivateQuickCastMode(true);
                else { _activeQuickCastPage = -1; _isQuickCastModeActive = false; }
                return;
            }

            var currentUnit = actionBarVM.SelectedUnit.Value;
            if (currentUnit == null)
            {
                Log("[ABM] No unit selected.");
                if (_isQuickCastModeActive) TryDeactivateQuickCastMode(true);
                else { _activeQuickCastPage = -1; _isQuickCastModeActive = false; }
                return;
            }

            // First, if already in quick cast mode, restore the previous state
            if (_isQuickCastModeActive)
            {
                RestoreMainActionBarDisplay();
                // We are switching from one quick cast page to another,
                // so don't fully deactivate, just prepare for the new page.
            }
            _mainBarOverriddenSlotsOriginalContent.Clear(); // Ensure it's clean before populating for the new page

            bool canOpenForTargetLevel = false;
            if (currentUnit.Descriptor?.Spellbooks != null)
            {
                foreach (var spellbook in currentUnit.Descriptor.Spellbooks)
                {
                    if (spellbook.Blueprint.MaxSpellLevel >= targetPageLevel)
                    {
                        if (spellbook.GetKnownSpells(targetPageLevel).Any() ||
                            spellbook.GetMemorizedSpells(targetPageLevel).Any() ||
                            (spellbook.Blueprint.Spontaneous && spellbook.GetSpecialSpells(targetPageLevel).Any()))
                        {
                            canOpenForTargetLevel = true;
                            break;
                        }
                    }
                }
            }

            if (!canOpenForTargetLevel)
            {
                string charName = currentUnit.CharacterName;
                string levelText = targetPageLevel == 0 ? "戏法" : $"{targetPageLevel}环";
                string warningMessage = $"快捷施法: 角色 {charName} 没有 {levelText} 法术或对应法术书.";
                EventBus.RaiseEvent<ILogMessageUIHandler>(h => h.HandleLogMessage(warningMessage));
                Log($"[ABM] {warningMessage}");
                // If we were in quick cast mode and tried to switch to an invalid page, restore the bar.
                if (_isQuickCastModeActive) RestoreMainActionBarDisplay(); 
                _activeQuickCastPage = -1; _isQuickCastModeActive = false; // Fully deactivate
                return;
            }

            try
            {
                // Save original contents BEFORE they might be changed by RefreshMainActionBarForCurrentQuickCastPage
                // This needs to be done carefully if we are switching between quick cast pages.
                // The current logic in TryActivateQuickCastPage already calls RestoreMainActionBarDisplay 
                // if _isQuickCastModeActive is true, which clears _mainBarOverriddenSlotsOriginalContent.
                // Then it sets _mainBarOverriddenSlotsOriginalContent.Clear(); again.
                // So, we need to save originals for the *new* page *after* any restoration but *before* the refresh.

                // If not already in quick cast mode, or if switching pages, save current bar state before overriding.
                // The _mainBarOverriddenSlotsOriginalContent should be clear at this point if we came from another QC page (due to Restore) or from normal mode.
                if (_mainBarOverriddenSlotsOriginalContent.Count == 0) // Only save if it's currently empty (i.e., we are not just re-activating the same page)
                {
                    Log("[ABM] Saving current action bar state before override.");
                    foreach (var mappingEntry in _castKeyToMainBarSlotIndexMapping)
                    {
                        int slotIndexToSave = mappingEntry.Value;
                        if (slotIndexToSave >= 0 && slotIndexToSave < actionBarVM.Slots.Count)
                        {
                            ActionBarSlotVM slotVM = actionBarVM.Slots[slotIndexToSave];
                            if (slotVM != null)
                            {
                                _mainBarOverriddenSlotsOriginalContent[slotIndexToSave] = slotVM.MechanicActionBarSlot;
                            }
                        }
                    }
                }

                // Open the spellbook UI part
                var spellsGroupView = spellsField.GetValue(cachedView) as ActionBarGroupPCView;
                if (spellsGroupView == null)
                {
                    Log("[ABM] Error: m_SpellsGroup instance is null after reflection. Cannot open spellbook UI part.");
                    RestoreMainActionBarDisplay(); // Critical failure, restore bar
                     _activeQuickCastPage = -1; _isQuickCastModeActive = false;
                    return;
                }
                Log($"[ABM] Calling SetVisible(true, true) on m_SpellsGroup for page {targetPageLevel}.");
                spellsGroupView.SetVisible(true, true);

                if (game.CurrentMode == GameModeType.Default ||
                    game.CurrentMode == GameModeType.TacticalCombat ||
                    game.CurrentMode == GameModeType.Pause)
                {
                    actionBarVM.CurrentSpellLevel.Value = targetPageLevel;
                    Log($"[ABM] Spell level set to {targetPageLevel}.");
                }
                else
                {
                    Log($"[ABM] Game not in a suitable mode ({game.CurrentMode}) to set spell level.");
                }

                _isQuickCastModeActive = true;
                _activeQuickCastPage = targetPageLevel;

                RefreshMainActionBarForCurrentQuickCastPage(); // New call to unified refresh logic

                string pageNameDisplay = _activeQuickCastPage == 0 ? "戏法" : $"{_activeQuickCastPage}环";
                if (_activeQuickCastPage == 10) pageNameDisplay = "10环(神话)";
                string message = $"快捷施法: {pageNameDisplay} 已激活";
                EventBus.RaiseEvent<ILogMessageUIHandler>(h => h.HandleLogMessage(message));
                Log($"[ABM] Internal: QuickCast Page {pageNameDisplay} Activated successfully.");
            }
            catch (Exception ex)
            {
                Log($"[ABM] Error during TryActivateQuickCastPage: {ex.ToString()}");
                RestoreMainActionBarDisplay(); // Restore display on error
                _activeQuickCastPage = -1; _isQuickCastModeActive = false;
            }
        }

        public void TryDeactivateQuickCastMode(bool force = false)
        {
            Log($"[ABM] Attempting to deactivate quick cast mode. Was active: {_isQuickCastModeActive}, Force: {force}");
            if (!_isQuickCastModeActive && !force)
            {
                Log($"[ABM] Not deactivating as not active and not forced.");
                return;
            }

            RestoreMainActionBarDisplay();

            var cachedView = _getActionBarPCView();
            var spellsField = _getSpellsGroupFieldInfo();

            if (cachedView != null && spellsField != null)
            {
                try
                {
                    var spellsGroupView = spellsField.GetValue(cachedView) as ActionBarGroupPCView;
                    if (spellsGroupView != null)
            {
                        Log("[ABM] Calling SetVisible(false, true) on m_SpellsGroup.");
                        spellsGroupView.SetVisible(false, true);
                    }
                    else
                    {
                        Log("[ABM] Warning: m_SpellsGroup instance is null during deactivation, cannot hide spellbook UI part.");
                    }
                }
                catch (Exception ex)
                {
                     Log($"[ABM] Error hiding m_SpellsGroup: {ex.ToString()}");
                }
            }
            else
            {
                Log("[ABM] Warning: ActionBarPCView or m_SpellsGroup FieldInfo not available for hiding spellbook UI part.");
                // Fallback if the view/field is somehow lost, try to tell VM directly (already in existing code)
                var actionBarVM = Game.Instance?.RootUiContext?.InGameVM?.StaticPartVM?.ActionBarVM;
                if (actionBarVM != null)
                {
                    actionBarVM.UpdateGroupState(ActionBarGroupType.Spell, false);
                    Log("[ABM] Fallback: Called UpdateGroupState(Spell, false) on VM directly.");
                }
            }
            
            string exitedPageNameDisplay = _activeQuickCastPage == 0 ? "戏法" : $"{_activeQuickCastPage}环";
            if (_activeQuickCastPage == 10) exitedPageNameDisplay = "10环(神话)";

            bool wasActuallyActive = _isQuickCastModeActive; // Capture state before reset
                     _isQuickCastModeActive = false; 
                    _activeQuickCastPage = -1;

            if (wasActuallyActive || force) { // Only log deactivation message if it was truly active or forced
                    string message = $"快捷施法: 返回主快捷栏";
                    EventBus.RaiseEvent<ILogMessageUIHandler>(h => h.HandleLogMessage(message));
                    Log($"[ABM] Internal: Returned to Main Hotbar from Page {exitedPageNameDisplay}.");
            }
        }

        public void AttemptToCastBoundSpell(KeyCode castKey)
        {
            if (!_isQuickCastModeActive || _activeQuickCastPage == -1)
            {
                Log($"[ABM CastAttempt] Cannot cast: QuickCast mode not active or no page selected. QC Active: {_isQuickCastModeActive}, Page: {_activeQuickCastPage}");
                return;
            }

            Log($"[ABM CastAttempt] Attempting to cast spell for key {castKey} on page {_activeQuickCastPage}");

            AbilityData abilityToCast = null;
            if (QuickCastBindings.TryGetValue(_activeQuickCastPage, out var pageBindings))
            {
                if (pageBindings.TryGetValue(castKey, out var spell))
                {
                    abilityToCast = spell;
                }
            }

            if (abilityToCast != null)
            {
                var caster = Game.Instance?.Player?.MainCharacter.Value?.Descriptor?.Unit;
                if (caster == null)
                {
                    Log("[ABM CastAttempt] Caster not found, cannot cast.");
                    return;
                }

                if (!abilityToCast.IsAvailableForCast)
                {
                    Log($"[ABM CastAttempt] Spell '{abilityToCast.Name}' is not available for casting (e.g., no slots, cooldown).");
                    EventBus.RaiseEvent<ILogMessageUIHandler>(h => h.HandleLogMessage($"无法施放: {abilityToCast.Name} (不可用)"));
                    return;
                }

                Log($"[ABM CastAttempt] Found spell '{abilityToCast.Name}' for key {castKey}. TargetAnchor: {abilityToCast.TargetAnchor}, Range: {abilityToCast.Range}");

                if (abilityToCast.TargetAnchor == AbilityTargetAnchor.Owner || abilityToCast.Range == AbilityRange.Personal)
                {
                    Log($"[ABM CastAttempt] Casting self-targeted or personal range spell: {abilityToCast.Name}");
                    caster.Commands.Run(UnitUseAbility.CreateCastCommand(abilityToCast, caster));
                }
                else
                {
                    Log($"[ABM CastAttempt] Setting ability for target selection: {abilityToCast.Name}");
                    Game.Instance.SelectedAbilityHandler.SetAbility(abilityToCast);
                }
            }
            else
            {
                Log($"[ABM CastAttempt] No spell bound to key {castKey} on page {_activeQuickCastPage}.");
                // Optionally, provide feedback to the player (e.g., a subtle sound or log message)
                // EventBus.RaiseEvent<ILogMessageUIHandler>(h => h.HandleLogMessage($"快捷键 {castKey} 未绑定法术"));
            }
        }
    }
} 