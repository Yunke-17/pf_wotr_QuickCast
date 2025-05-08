using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection; // Added for FieldInfo
using System.Text;
using System.Threading.Tasks;
using UnityEngine; // Required for Input class
using UnityModManagerNet;
using Kingmaker.PubSubSystem; // Added for EventBus and ILogMessageUIHandler
using Kingmaker; // Added for Game
using Kingmaker.UI.MVVM; // Added for RootUIContext
using Kingmaker.UI.MVVM._VM.ActionBar; // Added for ActionBarVM, SelectorType
using Kingmaker.UI.MVVM._VM.InGame; // Added for InGameVM, InGameStaticPartVM
using Kingmaker.GameModes; // Added for GameModeType
using Kingmaker.UnitLogic; // Added for Spellbook access
using Kingmaker.EntitySystem.Entities; // Added for UnitEntityData
using Kingmaker.UI.MVVM._PCView.ActionBar; // Added for ActionBarPCView

namespace QuickCast
{
    public static class Main // Made static as per UMM guidelines for entry points
    {
        public static bool IsEnabled { get; private set; }
        public static UnityModManager.ModEntry ModEntry { get; private set; }

        // Stores the currently active quick cast page. -1 means no page is active.
        // 0-9 for spell levels 0-9, 10 for spell level 10 (mythic).
        private static int _activeQuickCastPage = -1;
        private static bool _isQuickCastModeActive = false;

        // Default key for returning to main hotbar
        private static readonly KeyCode ReturnKey = KeyCode.X;

        // Default Page Activation Keys (Ctrl + Key)
        private static readonly Dictionary<KeyCode, int> PageActivationKeys = new Dictionary<KeyCode, int>
        {
            { KeyCode.Alpha1, 1 }, { KeyCode.Alpha2, 2 }, { KeyCode.Alpha3, 3 },
            { KeyCode.Alpha4, 4 }, { KeyCode.Alpha5, 5 }, { KeyCode.Alpha6, 6 },
            { KeyCode.Q, 7 }, { KeyCode.W, 8 }, { KeyCode.E, 9 }, { KeyCode.R, 10 }, // R for 10th level as an example
            { KeyCode.BackQuote, 0 } // Usually ~ key (BackQuote for US layout)
        };

        // Cached instance of ActionBarPCView
        public static ActionBarPCView CachedActionBarPCView { get; internal set; }
        // Cached FieldInfo for m_SpellsGroup to avoid repeated reflection
        private static FieldInfo _spellsGroupFieldInfo;

        public static bool Load(UnityModManager.ModEntry modEntry)
        {
            ModEntry = modEntry;
            IsEnabled = true;
            modEntry.OnUpdate = OnUpdate;
            modEntry.OnToggle = OnToggle;
            modEntry.OnGUI = OnGUI;

            var harmony = new Harmony(modEntry.Info.Id);
            // harmony.PatchAll(); // Comment out PatchAll

            // Explicitly patch BindViewImplementation
            try
            {
                var bindViewMethod = typeof(ActionBarPCView).GetMethod("BindViewImplementation", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (bindViewMethod != null)
                {
                    var bindViewPostfix = typeof(ActionBarPCView_Patches).GetMethod(nameof(ActionBarPCView_Patches.BindViewImplementation_Postfix), BindingFlags.Static | BindingFlags.Public);
                    harmony.Patch(bindViewMethod, postfix: new HarmonyMethod(bindViewPostfix));
                    Log("Successfully patched ActionBarPCView.BindViewImplementation.");
                }
                else
                {
                    Log("Error: Could not find ActionBarPCView.BindViewImplementation method for patching.");
                }
            }
            catch (Exception ex)
            {
                Log($"Exception while patching BindViewImplementation: {ex.ToString()}");
            }

            // Explicitly patch DestroyViewImplementation
            try
            {
                var destroyViewMethod = typeof(ActionBarPCView).GetMethod("DestroyViewImplementation", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (destroyViewMethod != null)
                {
                    var destroyViewPostfix = typeof(ActionBarPCView_Patches).GetMethod(nameof(ActionBarPCView_Patches.DestroyViewImplementation_Postfix), BindingFlags.Static | BindingFlags.Public);
                    harmony.Patch(destroyViewMethod, postfix: new HarmonyMethod(destroyViewPostfix));
                    Log("Successfully patched ActionBarPCView.DestroyViewImplementation.");
                }
                else
                {
                    Log("Error: Could not find ActionBarPCView.DestroyViewImplementation method for patching.");
                }
            }
            catch (Exception ex)
            {
                Log($"Exception while patching DestroyViewImplementation: {ex.ToString()}");
            }

            // Cache reflection for m_SpellsGroup (remains the same)
            try
            {
                _spellsGroupFieldInfo = typeof(ActionBarPCView).GetField("m_SpellsGroup", BindingFlags.NonPublic | BindingFlags.Instance);
                if (_spellsGroupFieldInfo == null)
                {
                    Log("Error: Could not find m_SpellsGroup field in ActionBarPCView via reflection.");
                }
                else
                {
                    Log("Successfully cached FieldInfo for m_SpellsGroup.");
                }
            }
            catch (Exception ex)
            {
                Log($"Error during reflection caching for m_SpellsGroup: {ex.Message}");
            }

            Log("QuickCast Mod Load method finished."); // Changed log message slightly for clarity
            return true;
        }

        private static bool OnToggle(UnityModManager.ModEntry modEntry, bool value)
        {
            IsEnabled = value;
            Log($"QuickCast Mod {(IsEnabled ? "Enabled" : "Disabled")}");
            if (!IsEnabled && _isQuickCastModeActive)
            {
                // If mod is disabled while a page is active, reset the state.
                DeactivateQuickCastMode();
            }
            return true; // Accept the new state
        }

        private static void OnUpdate(UnityModManager.ModEntry modEntry, float dt)
        {
            if (!IsEnabled) return;

            HandleInput();
        }

        private static void HandleInput()
        {
            // Check for Ctrl key being held down for page activation
            bool ctrlHeld = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);

            if (ctrlHeld)
            {
                foreach (var kvp in PageActivationKeys)
                {
                    if (Input.GetKeyDown(kvp.Key))
                    {
                        ActivateQuickCastPage(kvp.Value);
                        return; // Consume input
                    }
                }
            }

            // Check for ReturnKey
            if (Input.GetKeyDown(ReturnKey))
            {
                if (_isQuickCastModeActive)
                {
                    DeactivateQuickCastMode();
                }
                // Potentially: if not in quick cast mode and X is pressed, do nothing or game's default X action.
                // For now, it only acts as a return key if a page is active.
                return; // Consume input if it deactivated a page
            }

            // Optional: Double-tap activation key to return (as per quickcast.md 3.2.4)
            // This logic would be more complex, requiring tracking last key press time, etc.
            // Will be added in a later stage if desired.
        }

        private static void ActivateQuickCastPage(int targetPageLevel)
        {
            Log($"Attempting to activate quick cast page: {targetPageLevel}");
            var game = Game.Instance;
            var actionBarVM = game?.RootUiContext?.InGameVM?.StaticPartVM?.ActionBarVM;

            if (CachedActionBarPCView == null || _spellsGroupFieldInfo == null || actionBarVM == null)
            {
                Log("Error: ActionBarPCView, m_SpellsGroup FieldInfo, or ActionBarVM is not available. Cannot activate page via SetVisible.");
                if (_isQuickCastModeActive) DeactivateQuickCastMode(); 
                else { _activeQuickCastPage = -1; _isQuickCastModeActive = false; }
                return;
            }

            var currentUnit = actionBarVM.SelectedUnit.Value;
            if (currentUnit == null)
            {
                Log("No unit selected. Cannot activate page.");
                if (_isQuickCastModeActive) DeactivateQuickCastMode();
                else { _activeQuickCastPage = -1; _isQuickCastModeActive = false; }
                return;
            }

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
                Log(warningMessage);
                if (_isQuickCastModeActive) DeactivateQuickCastMode();
                else { _activeQuickCastPage = -1; _isQuickCastModeActive = false; }
                return;
            }

            try
            {
                var spellsGroupView = _spellsGroupFieldInfo.GetValue(CachedActionBarPCView) as ActionBarGroupPCView;
                if (spellsGroupView == null)
                {
                    Log("Error: m_SpellsGroup instance is null after reflection. Cannot call SetVisible.");
                    return;
                }

                Log($"Calling SetVisible(true, true) on m_SpellsGroup for page {targetPageLevel}.");
                spellsGroupView.SetVisible(true, true); 

                if (game.CurrentMode == GameModeType.Default ||
                    game.CurrentMode == GameModeType.TacticalCombat ||
                    game.CurrentMode == GameModeType.Pause)
                {
                    actionBarVM.CurrentSpellLevel.Value = targetPageLevel;
                    // actionBarVM.UpdateGroupState(ActionBarGroupType.Spell, true); // This might now be redundant
                    Log($"Spell level set to {targetPageLevel}. Group state update call to VM is currently commented out.");
                }
                else
                {
                     Log($"Game not in a suitable mode ({game.CurrentMode}) to set spell level. Panel might show default or last level.");
                }

                _isQuickCastModeActive = true;
                _activeQuickCastPage = targetPageLevel;

                string pageNameDisplay = _activeQuickCastPage == 0 ? "戏法" : $"{_activeQuickCastPage}环";
                if (_activeQuickCastPage == 10) pageNameDisplay = "10环(神话)";
                string message = $"快捷施法: {pageNameDisplay} 已激活";
                EventBus.RaiseEvent<ILogMessageUIHandler>(h => h.HandleLogMessage(message));
                Log($"Internal: QuickCast Page {pageNameDisplay} Activated successfully via SetVisible.");

            }
            catch (Exception ex)
            {
                Log($"Error during ActivateQuickCastPage via SetVisible: {ex.ToString()}");
                if (_isQuickCastModeActive) DeactivateQuickCastMode();
                else { _activeQuickCastPage = -1; _isQuickCastModeActive = false; }
            }
        }

        private static void DeactivateQuickCastMode()
        {
            Log("Attempting to deactivate quick cast mode via SetVisible.");
            if (_isQuickCastModeActive)
            {
                if (CachedActionBarPCView == null || _spellsGroupFieldInfo == null)
                {
                    Log("Error: ActionBarPCView or m_SpellsGroup FieldInfo is not available. Cannot deactivate page via SetVisible.");
                     var actionBarVM = Game.Instance?.RootUiContext?.InGameVM?.StaticPartVM?.ActionBarVM;
                     if (actionBarVM != null) { 
                         actionBarVM.UpdateGroupState(ActionBarGroupType.Spell, false);
                         Log("Fallback: Called UpdateGroupState(Spell, false) on VM directly.");
                     }
                    _isQuickCastModeActive = false;
                    _activeQuickCastPage = -1;
                    string fallbackMessage = $"快捷施法: 返回主快捷栏 (fallback)";
                    EventBus.RaiseEvent<ILogMessageUIHandler>(h => h.HandleLogMessage(fallbackMessage));
                    return;
                }

                try
                {
                    var spellsGroupView = _spellsGroupFieldInfo.GetValue(CachedActionBarPCView) as ActionBarGroupPCView;
                    if (spellsGroupView == null)
                    {
                        Log("Error: m_SpellsGroup instance is null after reflection during deactivation. Cannot call SetVisible.");
                        _isQuickCastModeActive = false;
                        _activeQuickCastPage = -1;
                         string errorMsg = $"快捷施法: 返回主快捷栏 (error reflecting m_SpellsGroup)"; // More specific error
                        EventBus.RaiseEvent<ILogMessageUIHandler>(h => h.HandleLogMessage(errorMsg));
                        return;
                    }

                    Log("Calling SetVisible(false, true) on m_SpellsGroup.");
                    spellsGroupView.SetVisible(false, true); 

                    string exitedPageNameDisplay = _activeQuickCastPage == 0 ? "戏法" : $"{_activeQuickCastPage}环";
                    if (_activeQuickCastPage == 10) exitedPageNameDisplay = "10环(神话)";
                    string message = $"快捷施法: 返回主快捷栏";
                    EventBus.RaiseEvent<ILogMessageUIHandler>(h => h.HandleLogMessage(message));
                    Log($"Internal: Returned to Main Hotbar from Page {exitedPageNameDisplay} via SetVisible.");

                    _isQuickCastModeActive = false;
                    _activeQuickCastPage = -1;
                }
                catch (Exception ex)
                {
                    Log($"Error during DeactivateQuickCastMode via SetVisible: {ex.ToString()}");
                    _isQuickCastModeActive = false;
                    _activeQuickCastPage = -1;
                    string errorMsg = $"快捷施法: 返回主快捷栏 (exception)";
                    EventBus.RaiseEvent<ILogMessageUIHandler>(h => h.HandleLogMessage(errorMsg));
                }
            }
        }

        private static void OnGUI(UnityModManager.ModEntry modEntry)
        {
            // The custom GUI indicator previously here has been removed.
            // Activation/deactivation messages are now sent to the game's event log system
            // via EventBus and ILogMessageUIHandler, as per quickcast.md section 4.1.
        }

        public static void Log(string message)
        {
            ModEntry?.Logger.Log(message);
        }
    }

    // Harmony Patches
    // [HarmonyPatch] // Remove this class-level attribute if all patches within are manual or more specific
    public static class ActionBarPCView_Patches
    {
        // No attributes needed here if patched manually by HarmonyMethod
        public static void BindViewImplementation_Postfix(ActionBarPCView __instance)
        {
            Main.Log("ActionBarPCView_BindViewImplementation_Postfix: ActionBarPCView instance captured.");
            Main.CachedActionBarPCView = __instance;
        }

        // No attributes needed here if patched manually by HarmonyMethod
        public static void DestroyViewImplementation_Postfix()
        {
            Main.Log("ActionBarPCView_DestroyViewImplementation_Postfix: ActionBarPCView instance cleared.");
            Main.CachedActionBarPCView = null;
        }
    }
}
