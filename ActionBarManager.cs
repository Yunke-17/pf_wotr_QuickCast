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
        // This mapping now primarily serves to link a game's native Cast Key (used for actual casting)
        // to a Logical Slot Index (0-11) which is used internally for binding and display.
        private readonly Dictionary<KeyCode, int> _castKeyToMainBarSlotIndexMapping;
        // Stores the mod's spell bindings: SpellLevel -> (LogicalSlotIndex -> AbilityData)
        public static Dictionary<int, Dictionary<int, AbilityData>> QuickCastBindings { get; set; } 

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
            if (QuickCastBindings == null) 
            {
                 QuickCastBindings = new Dictionary<int, Dictionary<int, AbilityData>>();
            }
            // TODO: Add test data to QuickCastBindings for development if needed
            // Example for new structure:
            // if (!QuickCastBindings.ContainsKey(1)) QuickCastBindings[1] = new Dictionary<int, AbilityData>();
            // QuickCastBindings[1][0] = someDummyAbilityDataForLevel1Slot0; // Slot 0 for Alpha1
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

            if (Time.frameCount != FrameOfLastBindingRefresh) {
                RecentlyBoundSlotHashes.Clear();
            }
            FrameOfLastBindingRefresh = Time.frameCount; 

            Log($"[ABM] Refreshing main action bar for quick cast page {_activeQuickCastPage}.");
            Dictionary<int, AbilityData> currentLevelBindings = null;
            if (QuickCastBindings.TryGetValue(_activeQuickCastPage, out var bindingsForLevel))
            {
                currentLevelBindings = bindingsForLevel;
                Log($"[ABM RefreshDetails] Found {bindingsForLevel.Count} bindings for active page {_activeQuickCastPage}.");
                foreach (var kvp in bindingsForLevel)
                {
                    // kvp.Key is now logicalSlotIndex, kvp.Value is AbilityData
                    Log($"[ABM RefreshDetails]   Binding: SlotIndex {kvp.Key}, Spell: {kvp.Value?.Name ?? "NULL"}");
                }
            }
            else
            {
                Log($"[ABM RefreshDetails] No bindings dictionary found for active page {_activeQuickCastPage}.");
            }

            // Iterate through the _castKeyToMainBarSlotIndexMapping to know which UI slots to update.
            // This mapping tells us which game's native Cast Key (e.g. Alpha1) corresponds to which logical slot index (e.g. 0).
            foreach (var mappingEntry in _castKeyToMainBarSlotIndexMapping) 
            {
                // KeyCode castKey = mappingEntry.Key; // This is the game's native cast key for the slot
                int logicalSlotIndexToRefresh = mappingEntry.Value; // This is the slot index (0, 1, 2...)

                if (logicalSlotIndexToRefresh >= 0 && logicalSlotIndexToRefresh < actionBarVM.Slots.Count)
                {
                    ActionBarSlotVM slotVM = actionBarVM.Slots[logicalSlotIndexToRefresh];
                    if (slotVM != null)
                    {
                        AbilityData boundAbility = null;
                        // Try to get the bound ability for the current logicalSlotIndex from the currentLevelBindings
                        currentLevelBindings?.TryGetValue(logicalSlotIndexToRefresh, out boundAbility);

                        if (boundAbility != null)
                        {
                            var newSpellSlot = new QuickCastMechanicActionBarSlotSpell(boundAbility, currentUnit);
                            slotVM.SetMechanicSlot(newSpellSlot);
                            RecentlyBoundSlotHashes.Add(newSpellSlot.GetHashCode()); 
                            Log($"[ABM] Refreshed Slot UI Index {logicalSlotIndexToRefresh} to spell: {boundAbility.Name}. Hash: {newSpellSlot.GetHashCode()}");
                            
                            string iconName = slotVM.Icon.Value != null ? slotVM.Icon.Value.name : "null";
                            string spellName = slotVM.MechanicActionBarSlot?.GetTitle() ?? "null"; 
                            Log($"[ABM] After SetMechanicSlot for Slot UI Index {logicalSlotIndexToRefresh}: VM reports Icon as {iconName}, Spell as {spellName}");
                        }
                        else
                        {
                            slotVM.SetMechanicSlot(_emptySlotPlaceholder);
                            Log($"[ABM] Refreshed Slot UI Index {logicalSlotIndexToRefresh} cleared (no binding for logical slot {logicalSlotIndexToRefresh} on level {_activeQuickCastPage}).");
                        }
                    }
                }
            }
        }

        // Method to bind a spell to a specific logical slot on a given spell level page
        public void BindSpellToLogicalSlot(int spellLevel, int logicalSlotIndex, AbilityData spellData)
        {
            if (spellData == null)
            {
                Log($"[ABM] BindSpellToLogicalSlot: spellData is null for level {spellLevel}, slotIndex {logicalSlotIndex}. Cannot bind.");
                return;
            }

            // Validate logicalSlotIndex (e.g., 0-11 if we support 12 slots)
            // This check depends on how many slots _castKeyToMainBarSlotIndexMapping covers or a fixed max.
            // For now, assuming valid if it's within typical action bar range.
            if (logicalSlotIndex < 0 || logicalSlotIndex >= 12) // Assuming 12 slots max for now
            {
                Log($"[ABM] BindSpellToLogicalSlot: logicalSlotIndex {logicalSlotIndex} is out of range. Cannot bind.");
                return;
            }

            if (!QuickCastBindings.ContainsKey(spellLevel))
            {
                QuickCastBindings[spellLevel] = new Dictionary<int, AbilityData>();
            }
            QuickCastBindings[spellLevel][logicalSlotIndex] = spellData;
            Log($"[ABM] Spell '{spellData.Name}' (Lvl: {spellData.SpellLevel}, From UI Lvl: {spellLevel}) bound to LogicalSlotIndex {logicalSlotIndex}.");

            Log($"[ABM BindDetails] Current Bindings for Level {spellLevel}:");
            if (QuickCastBindings.TryGetValue(spellLevel, out var bindings))
            {
                foreach (var kvp in bindings)
                {
                    Log($"[ABM BindDetails]   LogicalSlot: {kvp.Key}, Spell: {kvp.Value?.Name ?? "NULL"}");
                }
            }
            else
            {
                Log("[ABM BindDetails]   No bindings found for this level after update.");
            }

            if (_isQuickCastModeActive && _activeQuickCastPage == spellLevel)
            {
                RefreshMainActionBarForCurrentQuickCastPage();
            }
        }

        public void TryActivateQuickCastPage(int targetPageLevel)
        {
            Log($"[ABM] Attempting to activate quick cast page: {targetPageLevel}");
            var game = Game.Instance;
            var actionBarVM = game?.RootUiContext?.InGameVM?.StaticPartVM?.ActionBarVM;
            var cachedView = _getActionBarPCView();
            var spellsFieldInfo = _getSpellsGroupFieldInfo(); // This is already cached in Main and passed to ABM constructor

            if (cachedView == null)
            {
                Log("[ABM TryActivate] Error: Cached ActionBarPCView is null.");
                RestoreMainActionBarDisplay(); _activeQuickCastPage = -1; _isQuickCastModeActive = false; return;
            }
            if (spellsFieldInfo == null)
            {
                Log("[ABM TryActivate] Error: spellsGroupFieldInfo (for m_SpellsGroup) is null.");
                RestoreMainActionBarDisplay(); _activeQuickCastPage = -1; _isQuickCastModeActive = false; return;
            }
            if (actionBarVM == null)
            {
                Log("[ABM TryActivate] Error: ActionBarVM is null.");
                RestoreMainActionBarDisplay(); _activeQuickCastPage = -1; _isQuickCastModeActive = false; return;
            }

            var currentUnit = actionBarVM.SelectedUnit.Value;
            if (currentUnit == null)
            {
                Log("[ABM] No unit selected.");
                if (_isQuickCastModeActive) TryDeactivateQuickCastMode(true);
                else { _activeQuickCastPage = -1; _isQuickCastModeActive = false; }
                return;
            }

            // If already in quick cast mode, restore the previous state
            if (_isQuickCastModeActive)
            {
                RestoreMainActionBarDisplay();
            }
            _mainBarOverriddenSlotsOriginalContent.Clear(); // Ensure it's clean before potentially populating for the new page

            // Save the original content of the action bar slots that QuickCast will override.
            // This must be done AFTER any restore (if switching pages) and BEFORE RefreshMainActionBarForCurrentQuickCastPage.
            var currentActionBarVMForSaving = game?.RootUiContext?.InGameVM?.StaticPartVM?.ActionBarVM;
            if (currentActionBarVMForSaving != null)
            {
                Log("[ABM TryActivate] Attempting to save current action bar state before override for QuickCast page.");
                foreach (var mappingEntry in _castKeyToMainBarSlotIndexMapping)
                {
                    int slotIndexToSave = mappingEntry.Value; // This is the UI slot index (0, 1, etc.)
                    if (slotIndexToSave >= 0 && slotIndexToSave < currentActionBarVMForSaving.Slots.Count)
                    {
                        ActionBarSlotVM slotVM = currentActionBarVMForSaving.Slots[slotIndexToSave];
                        if (slotVM != null)
                        {
                            // Important: We need to ensure we are not saving a QuickCastMechanicActionBarSlotSpell
                            // if, for some reason, a previous QuickCast state wasn't fully cleared.
                            // RestoreMainActionBarDisplay should handle this, but as a safeguard:
                            if (slotVM.MechanicActionBarSlot is QuickCastMechanicActionBarSlotSpell)
                            {
                                Log($"[ABM TryActivate WARNING] Slot {slotIndexToSave} contained a QC spell even after potential restore. This might indicate an issue. Not saving it as original.");
                                // Optionally, try to get a more "original" state or leave it to be overwritten.
                                // For now, we just log and it will be treated as if it was empty or had non-QC content that was already restored.
                            }
                            else
                            {
                                _mainBarOverriddenSlotsOriginalContent[slotIndexToSave] = slotVM.MechanicActionBarSlot;
                                Log($"[ABM TryActivate] Saved original content for slot index {slotIndexToSave}: {slotVM.MechanicActionBarSlot?.GetType().Name}");
                            }
                        }
                        else
                        {
                            Log($"[ABM TryActivate] Cannot save original for slot index {slotIndexToSave}: SlotVM is null.");
                        }
                    }
                    else
                    {
                        Log($"[ABM TryActivate] Cannot save original for slot index {slotIndexToSave}: Index out of bounds for {currentActionBarVMForSaving.Slots.Count} slots.");
                    }
                }
            }
            else
            {
                 Log("[ABM TryActivate] Cannot save original action bar state: currentActionBarVMForSaving is null.");
            }

            try
            {
                var actionBarVMToSetLevel = game?.RootUiContext?.InGameVM?.StaticPartVM?.ActionBarVM;

                if (cachedView == null)
                {
                    Log("[ABM TryActivate] Error: Cached ActionBarPCView is null.");
                    RestoreMainActionBarDisplay(); _activeQuickCastPage = -1; _isQuickCastModeActive = false; return;
                }
                if (spellsFieldInfo == null)
                {
                    Log("[ABM TryActivate] Error: spellsGroupFieldInfo (for m_SpellsGroup) is null.");
                    RestoreMainActionBarDisplay(); _activeQuickCastPage = -1; _isQuickCastModeActive = false; return;
                }
                if (actionBarVMToSetLevel == null)
                {
                    Log("[ABM TryActivate] Error: ActionBarVM for setting spell level is null.");
                    RestoreMainActionBarDisplay(); _activeQuickCastPage = -1; _isQuickCastModeActive = false; return;
                }

                var spellsGroupInstance = spellsFieldInfo.GetValue(cachedView) as ActionBarGroupPCView;
                if (spellsGroupInstance == null)
                {
                    Log("[ABM TryActivate] Error: Failed to get m_SpellsGroup instance from ActionBarPCView via reflection.");
                    RestoreMainActionBarDisplay(); _activeQuickCastPage = -1; _isQuickCastModeActive = false; return;
                }

                Log("[ABM TryActivate] Attempting to directly call m_SpellsGroup.SetVisible(true, true).");
                spellsGroupInstance.SetVisible(true, true); // Force visibility
                Log("[ABM TryActivate] Called m_SpellsGroup.SetVisible(true, true).");

                // Set the spell level in the ViewModel AFTER making the spell group visible
                // because SetVisible might trigger other groups to hide and could potentially reset spell level if done before.
                if (game.CurrentMode == GameModeType.Default ||
                    game.CurrentMode == GameModeType.TacticalCombat ||
                    game.CurrentMode == GameModeType.Pause)
                {
                    Log($"[ABM TryActivate] Setting CurrentSpellLevel.Value to {targetPageLevel}. Previous value: {actionBarVMToSetLevel.CurrentSpellLevel.Value}");
                    actionBarVMToSetLevel.CurrentSpellLevel.Value = targetPageLevel;
                    Log($"[ABM TryActivate] Spell level set to {targetPageLevel} via VM. New value: {actionBarVMToSetLevel.CurrentSpellLevel.Value}");
                }
                else
                {
                    Log($"[ABM TryActivate] Game not in a suitable mode ({game.CurrentMode}) to set spell level.");
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
            var spellsFieldInfo = _getSpellsGroupFieldInfo(); // This is already cached in Main and passed to ABM constructor

            if (cachedView != null && spellsFieldInfo != null)
            {
                try
                {
                    var spellsGroupInstance = spellsFieldInfo.GetValue(cachedView) as ActionBarGroupPCView;
                    if (spellsGroupInstance != null)
                    {
                        Log("[ABM] Calling SetVisible(false, true) on m_SpellsGroup.");
                        spellsGroupInstance.SetVisible(false, true);
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

        public void AttemptToCastBoundSpell(KeyCode castKey) // castKey is the game's native key (e.g. KeyCode.Alpha1)
        {
            if (!_isQuickCastModeActive || _activeQuickCastPage == -1)
            {
                Log($"[ABM CastAttempt] Cannot cast: QuickCast mode not active or no page selected. QC Active: {_isQuickCastModeActive}, Page: {_activeQuickCastPage}");
                return;
            }

            // Convert the game's native castKey to its corresponding logicalSlotIndex
            if (!_castKeyToMainBarSlotIndexMapping.TryGetValue(castKey, out int logicalSlotIndex))
            {
                Log($"[ABM CastAttempt] Key {castKey} is not a mapped QuickCast CastKey. Cannot determine logical slot.");
                return; // This key isn't one of the ones QuickCast manages for casting (e.g. not Alpha1-6)
            }

            Log($"[ABM CastAttempt] Attempting to cast spell for native key {castKey} (LogicalSlot {logicalSlotIndex}) on page {_activeQuickCastPage}");

            AbilityData abilityToCast = null;
            if (QuickCastBindings.TryGetValue(_activeQuickCastPage, out var pageBindings))
            {
                if (pageBindings.TryGetValue(logicalSlotIndex, out var spell))
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

                Log($"[ABM CastAttempt] Found spell '{abilityToCast.Name}' for native key {castKey} (LogicalSlot {logicalSlotIndex}). TargetAnchor: {abilityToCast.TargetAnchor}, Range: {abilityToCast.Range}");

                bool castInitiated = false;
                if (abilityToCast.TargetAnchor == AbilityTargetAnchor.Owner || abilityToCast.Range == AbilityRange.Personal)
                {
                    Log($"[ABM CastAttempt] Casting self-targeted or personal range spell: {abilityToCast.Name}");
                    caster.Commands.Run(UnitUseAbility.CreateCastCommand(abilityToCast, caster));
                    castInitiated = true;
                }
                else
                {
                    Log($"[ABM CastAttempt] Setting ability for target selection: {abilityToCast.Name}");
                    Game.Instance.SelectedAbilityHandler.SetAbility(abilityToCast);
                    castInitiated = true;
                }

                // Check for Auto Return after cast initiation
                if (castInitiated && Main.settings != null && Main.settings.AutoReturnAfterCast)
                {
                    Log($"[ABM CastAttempt] Auto-returning to main hotbar after initiating cast for '{abilityToCast.Name}'.");
                    // We might want a slight delay here if it feels too jarring, but TryDeactivateQuickCastMode itself doesn't have a delay.
                    // For now, direct call.
                    TryDeactivateQuickCastMode(false); 
                    // Note: If TryDeactivateQuickCastMode is called, it will also clear _lastPageActivationKeyPressed in Main.cs if we make that call from here.
                    // However, TryDeactivateQuickCastMode doesn't know about those Main.cs static fields directly.
                    // The _previousSpellbookActiveAndInteractableState is reset in Main.cs when TryDeactivate is called from there.
                }
            }
            else
            {
                Log($"[ABM CastAttempt] No spell bound to native key {castKey} (LogicalSlot {logicalSlotIndex}) on page {_activeQuickCastPage}.");
            }
        }
    }
} 