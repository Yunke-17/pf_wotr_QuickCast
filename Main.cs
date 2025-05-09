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
using Kingmaker.UI.MVVM._VM.ActionBar; // Added for ActionBarVM, SelectorType
using Kingmaker.UI.MVVM._VM.InGame; // Added for InGameVM, InGameStaticPartVM
using Kingmaker.GameModes; // Added for GameModeType
using Kingmaker.UnitLogic; // Added for Spellbook access
using Kingmaker.EntitySystem.Entities; // Added for UnitEntityData
using Kingmaker.UI.MVVM._PCView.ActionBar; // Added for ActionBarPCView
using Kingmaker.UnitLogic.Abilities; // For AbilityData
using Kingmaker.UI.UnitSettings; // Added for MechanicActionBarSlotSpell
using Owlcat.Runtime.UI.Controls.Button; // Added for OwlcatMultiButton
using Kingmaker.UI.MVVM; // Added for ViewBase
using UniRx; // Corrected UniRx namespace

namespace QuickCast
{
    public static class Main // Made static as per UMM guidelines for entry points
    {
        public static bool IsEnabled { get; private set; }
        public static UnityModManager.ModEntry ModEntry { get; private set; }

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

        // Add this new field to track the previous state of spellbook UI activation
        private static bool? _previousSpellbookActiveAndInteractableState = null;

        private static FieldInfo _slotsListFieldInfo; // ActionBarGroupPCView.m_SlotsList
        private static FieldInfo _mainButtonFieldInfo; // ActionBarSlotPCView.m_MainButton
        private static FieldInfo _buttonIsHoveredFieldInfo; // OwlcatMultiButton.m_IsHovered
        private static PropertyInfo _viewViewModelPropertyInfo; // ViewBase<T>.ViewModel
        private static FieldInfo _baseSlotPCViewFieldInfo; // ActionBarBaseSlotPCView.m_SlotPCView
        internal static ActionBarManager _actionBarManager; // Changed to internal static

        // Reference to the VM hovered via patch
        public static ActionBarSlotVM CurrentlyHoveredSpellbookSlotVM => QuickCastPatches.CurrentlyHoveredSpellbookSlotVM;

        public static bool Load(UnityModManager.ModEntry modEntry)
        {
            ModEntry = modEntry;
            IsEnabled = true;
            modEntry.OnUpdate = OnUpdate;
            modEntry.OnToggle = OnToggle;
            modEntry.OnGUI = OnGUI;

            var harmony = new Harmony(modEntry.Info.Id);
            harmony.PatchAll(Assembly.GetExecutingAssembly()); // Apply all patches in this assembly
            Log("Applied Harmony patches.");

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
                // Corrected to ActionBarBaseView as per UMM error log
                var destroyViewMethod = typeof(Kingmaker.UI.MVVM._PCView.ActionBar.ActionBarBaseView).GetMethod("DestroyViewImplementation", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public); 
                if (destroyViewMethod != null)
                {
                    var destroyViewPostfix = typeof(ActionBarPCView_Patches).GetMethod(nameof(ActionBarPCView_Patches.DestroyViewImplementation_Postfix), BindingFlags.Static | BindingFlags.Public);
                    harmony.Patch(destroyViewMethod, postfix: new HarmonyMethod(destroyViewPostfix));
                    Log("Successfully patched ActionBarBaseView.DestroyViewImplementation."); 
                }
                else
                {
                    Log("Error: Could not find ActionBarBaseView.DestroyViewImplementation method for patching."); 
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

            // Cache reflection info for hover detection
            try
            {
                _slotsListFieldInfo = AccessTools.Field(typeof(ActionBarGroupPCView), "m_SlotsList");
                _mainButtonFieldInfo = AccessTools.Field(typeof(ActionBarSlotPCView), "m_MainButton");
                _buttonIsHoveredFieldInfo = AccessTools.Field(typeof(OwlcatMultiButton), "m_IsHovered"); 
                _viewViewModelPropertyInfo = AccessTools.Property(typeof(ActionBarSlotPCView), "ViewModel"); 
                _baseSlotPCViewFieldInfo = AccessTools.Field(typeof(ActionBarBaseSlotPCView), "m_SlotPCView");

                if (_slotsListFieldInfo == null) Log("Error caching m_SlotsList FieldInfo.");
                if (_mainButtonFieldInfo == null) Log("Error caching m_MainButton FieldInfo.");
                if (_buttonIsHoveredFieldInfo == null) Log("Warning: Could not cache m_IsHovered FieldInfo for OwlcatMultiButton."); 
                if (_viewViewModelPropertyInfo == null) Log("Error caching ViewModel PropertyInfo.");
                if (_baseSlotPCViewFieldInfo == null) Log("Error caching m_SlotPCView FieldInfo.");
            }
            catch (Exception ex)
            {
                 Log($"Exception during hover detection reflection caching: {ex.ToString()}");
            }

            _actionBarManager = new ActionBarManager(
                ModEntry,
                () => CachedActionBarPCView, 
                () => _spellsGroupFieldInfo    
            );

            Log("QuickCast Mod Load method finished."); 
            return true;
        }

        private static bool OnToggle(UnityModManager.ModEntry modEntry, bool value)
        {
            IsEnabled = value;
            Log($"QuickCast Mod {(IsEnabled ? "Enabled" : "Disabled")}");
            if (!IsEnabled && _actionBarManager != null && _actionBarManager.IsQuickCastModeActive)
            {
                _actionBarManager.TryDeactivateQuickCastMode(true); 
                _previousSpellbookActiveAndInteractableState = null; 
            }
            return true; 
        }

        private static void OnUpdate(UnityModManager.ModEntry modEntry, float dt)
        {
            if (!IsEnabled || _actionBarManager == null) return;

            // Clear the recently bound slots hash set at the beginning of each new frame (if frame changed)
            ActionBarManager.ClearRecentlyBoundSlotsIfNewFrame();

            HandleInput();
        }

        private static void HandleInput()
        {
            if (!IsEnabled || _actionBarManager == null) return;

            bool ctrlHeld = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);

            if (ctrlHeld)
            {
                foreach (var kvp in PageActivationKeys)
                {
                    if (Input.GetKeyDown(kvp.Key))
                    {
                        _actionBarManager.TryActivateQuickCastPage(kvp.Value);
                        return; 
                    }
                }
            }
            else if (_actionBarManager.IsQuickCastModeActive) 
            {
                bool currentSpellbookActiveAndInteractable = IsSpellbookInterfaceActive(); 
                if (_previousSpellbookActiveAndInteractableState == null || _previousSpellbookActiveAndInteractableState.Value != currentSpellbookActiveAndInteractable)
                {
                    Log($"[QC Input] QC Active. Spellbook UI ActiveAndInteractable: {currentSpellbookActiveAndInteractable}");
                    _previousSpellbookActiveAndInteractableState = currentSpellbookActiveAndInteractable;
                }

                foreach (var kvpBinding in _actionBarManager.GetCastKeyToSlotMapping()) 
                {
                    KeyCode castKey = kvpBinding.Key;
                    if (Input.GetKeyDown(castKey))
                    {
                        if (currentSpellbookActiveAndInteractable) 
                        {
                            AbilityData hoveredAbility = GetHoveredAbilityInSpellBookUIActions();
                            
                            if (hoveredAbility != null)
                            {
                                Log($"[QC Input] Attempting Binding: Key {castKey} pressed. Hovered: {hoveredAbility.Name}. QC Page: {_actionBarManager.ActiveQuickCastPage}");
                                _actionBarManager.BindSpellToCastKey(_actionBarManager.ActiveQuickCastPage, castKey, hoveredAbility);
                                return; 
                            }
                            else
                            {
                                Log($"[QC Input] Spellbook active, but no spell hovered. Attempting to CAST spell with key {castKey} from QuickCast page {_actionBarManager.ActiveQuickCastPage}.");
                                _actionBarManager.AttemptToCastBoundSpell(castKey);
                                return; 
                            }
                        }
                        else
                        {
                            Log($"[QC Input] Spellbook NOT active. Attempting to CAST spell with key {castKey} from QuickCast page {_actionBarManager.ActiveQuickCastPage}.");
                            _actionBarManager.AttemptToCastBoundSpell(castKey);
                            return; 
                        }
                    }
                }
            }

            if (Input.GetKeyDown(ReturnKey)) 
            {
                if (_actionBarManager.IsQuickCastModeActive)
                {
                    _actionBarManager.TryDeactivateQuickCastMode();
                    _previousSpellbookActiveAndInteractableState = null; 
                    return; 
                }
            }
        }

        // Helper to check if the ActionBar's spellbook part is likely active and interactable
        internal static bool IsSpellbookInterfaceActive() 
        {
            if (CachedActionBarPCView == null || _spellsGroupFieldInfo == null) return false;
            try
            {
                var spellsGroupView = _spellsGroupFieldInfo.GetValue(CachedActionBarPCView) as ActionBarGroupPCView;
                return spellsGroupView != null && spellsGroupView.gameObject.activeInHierarchy;
            }
            catch (Exception ex)
            {
                Log($"Error checking IsSpellbookInterfaceActive: {ex.Message}");
                return false;
            }
        }

        private static AbilityData GetHoveredAbilityInSpellBookUIActions()
        {
            var actionBarVM = Game.Instance?.RootUiContext?.InGameVM?.StaticPartVM?.ActionBarVM;
            var hoveredVM = CurrentlyHoveredSpellbookSlotVM; 

            if (actionBarVM == null || hoveredVM == null)
            {
                return null;
            }

            if (hoveredVM.SpellLevel == actionBarVM.CurrentSpellLevel.Value)
            {
                if (hoveredVM.MechanicActionBarSlot is MechanicActionBarSlotSpell gameSpellSlot)
                {
                    return gameSpellSlot.Spell;
                }
            }
            
            return null; 
        }

        // Helper to get the spellbook group view instance
        private static ActionBarGroupPCView GetSpellbookGroupView()
        {
             if (CachedActionBarPCView == null || _spellsGroupFieldInfo == null) return null;
             try
             {
                 return _spellsGroupFieldInfo.GetValue(CachedActionBarPCView) as ActionBarGroupPCView;
             }
             catch (Exception ex)
             {
                  Log($"Error getting SpellbookGroupView: {ex.Message}");
                  return null;
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

    public static class ActionBarPCView_Patches 
    {
        public static void BindViewImplementation_Postfix(ActionBarPCView __instance)
        {
            Main.Log("ActionBarPCView_BindViewImplementation_Postfix: ActionBarPCView instance captured.");
            Main.CachedActionBarPCView = __instance;
        }
        public static void DestroyViewImplementation_Postfix()
        {
             Main.Log("ActionBarPCView_DestroyViewImplementation_Postfix: ActionBarPCView instance cleared.");
             Main.CachedActionBarPCView = null;
        }
    }
}
