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
        public static Settings settings { get; private set; } // To store loaded settings
        private static int _currentlySettingBindKeyForSlot = -1; // -1 means not setting any key
        private static string[] _logicalSlotLabels; // For descriptive labels in UI
        private static int _currentlySettingPageKeyForLevel = -1; // For Page Activation Key GUI
        private static string[] _pageActivationLevelLabels; // For Page Activation Key GUI
        private static bool _isSettingReturnKey = false; // For Return Key GUI

        // For Double-Tap to Return feature
        private static KeyCode _lastPageActivationKeyPressed = KeyCode.None;
        private static float _lastPageActivationKeyPressTime = 0f;
        private const float DoubleTapTimeThreshold = 0.3f; // Seconds for double tap detection

        // Default key for returning to main hotbar - This is now configured in Settings
        // private static readonly KeyCode ReturnKey = KeyCode.X;

        // Default Page Activation Keys (Ctrl + Key) - These are now configured in Settings
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
        private static PropertyInfo _viewViewModelPropertyInfo; // ViewBase<T>.ViewModel
        private static FieldInfo _baseSlotPCViewFieldInfo; // ActionBarBaseSlotPCView.m_SlotPCView
        internal static ActionBarManager _actionBarManager; // Changed to internal static

        // Reference to the VM hovered via patch
        public static ActionBarSlotVM CurrentlyHoveredSpellbookSlotVM => QuickCastPatches.CurrentlyHoveredSpellbookSlotVM;

        public static bool Load(UnityModManager.ModEntry modEntry)
        {
            ModEntry = modEntry;
            IsEnabled = true;

            // Load settings
            settings = UnityModManager.ModSettings.Load<Settings>(modEntry); 
            if (settings == null) 
            {
                settings = new Settings(); 
                Log("Warning: Settings could not be loaded, created new instance.");
            }
            // Initialize labels 
            if (_logicalSlotLabels == null || _logicalSlotLabels.Length != settings.BindKeysForLogicalSlots.Length)
            {
                _logicalSlotLabels = new string[settings.BindKeysForLogicalSlots.Length];
                for (int i = 0; i < _logicalSlotLabels.Length; i++)
                {
                    _logicalSlotLabels[i] = $"逻辑槽位 {i + 1} 绑定键:";
                }
            }
            // Initialize labels for Page Activation Keys
            if (_pageActivationLevelLabels == null || _pageActivationLevelLabels.Length != settings.PageActivation_Keys.Length)
            {
                _pageActivationLevelLabels = new string[settings.PageActivation_Keys.Length];
                for (int i = 0; i < _pageActivationLevelLabels.Length; i++)
                {
                    string levelDisplay = (i == 10) ? "10 (神话)" : i.ToString();
                    if (i == 0) levelDisplay = "0 (戏法)";
                    _pageActivationLevelLabels[i] = $"激活 {levelDisplay}环 页 (Ctrl+):";
                }
            }

            modEntry.OnUpdate = OnUpdate;
            modEntry.OnToggle = OnToggle;
            modEntry.OnGUI = OnGUI; 
            modEntry.OnSaveGUI = OnSaveGUI; 

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
                _viewViewModelPropertyInfo = AccessTools.Property(typeof(ActionBarSlotPCView), "ViewModel"); 
                _baseSlotPCViewFieldInfo = AccessTools.Field(typeof(ActionBarBaseSlotPCView), "m_SlotPCView");

                if (_slotsListFieldInfo == null) Log("Error caching m_SlotsList FieldInfo.");
                if (_mainButtonFieldInfo == null) Log("Error caching m_MainButton FieldInfo.");
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
            if (!IsEnabled) 
            {
                // ModEntry?.Logger.Log("[QC Input] Disabled, skipping HandleInput."); // Potentially too noisy
                return;
            }
            if (_actionBarManager == null)
            {
                Log("[QC Input ERROR] _actionBarManager is NULL in HandleInput. Cannot process input.");
                return;
            }
            // Log("[QC Input] HandleInput called."); // Generally too noisy

            bool ctrlHeld = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            // Log($"[QC Input] ctrlHeld: {ctrlHeld}"); // Logged further down if relevant

            bool noModifiers = !ctrlHeld && !Input.GetKey(KeyCode.LeftShift) && !Input.GetKey(KeyCode.RightShift) && !Input.GetKey(KeyCode.LeftAlt) && !Input.GetKey(KeyCode.RightAlt);

            if (ctrlHeld) 
            {
                Log($"[QC Input DEBUG] Ctrl key is held. Checking PageActivation_Keys. Array Length: {settings.PageActivation_Keys.Length}");
                for (int spellLevel = 0; spellLevel < settings.PageActivation_Keys.Length; spellLevel++)
                {
                    KeyCode pageKey = settings.PageActivation_Keys[spellLevel];
                    Log($"[QC Input LOOP] spellLevel: {spellLevel}, Configured pageKey: {pageKey}"); 

                    if (pageKey == KeyCode.None) 
                    {
                        // Log($"[QC Input LOOP] pageKey is None for spellLevel: {spellLevel}. Continuing."); // Optional
                        continue; 
                    }

                    // ---- TEMPORARY DETAILED LOG FOR CTRL+1 ----
                    if (spellLevel == 1) // Specifically for 1-ring
                    {
                        bool isAlpha1Down = Input.GetKeyDown(KeyCode.Alpha1);
                        bool isConfiguredKeyAlpha1 = (pageKey == KeyCode.Alpha1);
                        Log($"[QC Input DETAIL Lvl1] Target Ctrl+1: ConfiguredKeyForLvl1Page: {pageKey}, IsKeyCodeAlpha1DownThisFrame: {isAlpha1Down}, IsConfiguredKeyActuallyAlpha1: {isConfiguredKeyAlpha1}.");
                    }
                    // ---- END TEMPORARY LOG ----

                    if (Input.GetKeyDown(pageKey))
                    {
                        Log($"[QC Input SUCCESS] Ctrl + {pageKey} (for spell level {spellLevel}) detected!");
                        if (settings.EnableDoubleTapToReturn && 
                            _actionBarManager.IsQuickCastModeActive &&
                            _actionBarManager.ActiveQuickCastPage == spellLevel && 
                            pageKey == _lastPageActivationKeyPressed && 
                            (Time.time - _lastPageActivationKeyPressTime) < DoubleTapTimeThreshold)
                        {
                            Log($"[QC Input] Double-tap detected on active page key {pageKey}. Returning to main hotbar.");
                            _actionBarManager.TryDeactivateQuickCastMode();
                            _lastPageActivationKeyPressed = KeyCode.None; 
                        }
                        else
                        {
                            Log($"[QC Input] Attempting to call _actionBarManager.TryActivateQuickCastPage({spellLevel})");
                            _actionBarManager.TryActivateQuickCastPage(spellLevel);
                            _lastPageActivationKeyPressed = pageKey; 
                            _lastPageActivationKeyPressTime = Time.time;
                        }
                        return; 
                    }
                }
                // If loop finishes, means Ctrl was held but no configured key was pressed *this specific frame*
                // Log("[QC Input DEBUG] Ctrl held, but no matching PageActivation_Key was pressed down this frame.");
            }
            // This 'else if' ensures that ReturnKey and BindKeys are not processed if a Ctrl+Key (Page Activation) was handled.
            else if (_actionBarManager.IsQuickCastModeActive) 
            {
                bool spellbookUIVisible = IsSpellbookInterfaceActive(); 
                if (_previousSpellbookActiveAndInteractableState == null || _previousSpellbookActiveAndInteractableState.Value != spellbookUIVisible)
                {
                    Log($"[QC Input] QC Active. Spellbook UI Visible: {spellbookUIVisible}");
                    _previousSpellbookActiveAndInteractableState = spellbookUIVisible;
                }

                if (spellbookUIVisible)
                {
                    AbilityData hoveredAbility = GetHoveredAbilityInSpellBookUIActions();
                    if (hoveredAbility != null) 
                    {
                        for (int i = 0; i < settings.BindKeysForLogicalSlots.Length; i++)
                        {
                            KeyCode configuredBindKey = settings.BindKeysForLogicalSlots[i];
                            // Ensure BindKeys are only processed if no other modifiers are held (or define specific modifier behavior for them)
                            if (configuredBindKey != KeyCode.None && noModifiers && Input.GetKeyDown(configuredBindKey))
                            {
                                Log($"[QC Input] Attempting Binding: Configured BindKey {configuredBindKey} (for Slot {i}) pressed. Hovered: {hoveredAbility.Name}. QC Page: {_actionBarManager.ActiveQuickCastPage}");
                                _actionBarManager.BindSpellToLogicalSlot(_actionBarManager.ActiveQuickCastPage, i, hoveredAbility);
                                return; 
                            }
                        }
                    }
                }

                // Native Cast Key presses for spell casting (typically Alpha1-6, etc.)
                // These should also ideally only trigger if no other major modifiers are held, or be specifically Ctrl/Shift/Alt aware if designed so.
                if (noModifiers) // Added noModifiers check for casting as well for consistency
                {
                    foreach (var kvpMapping in _actionBarManager.GetCastKeyToSlotMapping()) 
                    {
                        KeyCode nativeCastKey = kvpMapping.Key; 
                        if (Input.GetKeyDown(nativeCastKey))
                        {
                            Log($"[QC Input] Native CastKey {nativeCastKey} pressed. Attempting to CAST spell from QuickCast page {_actionBarManager.ActiveQuickCastPage}.");
                            _actionBarManager.AttemptToCastBoundSpell(nativeCastKey);
                            return; 
                        }
                    }
                }
            }

            // ReturnKey: Process if no other modifiers are held typically, or if it's specifically designed for combos.
            // For simplicity, checking noModifiers alongside the actual ReturnKey.
            if (settings.ReturnToMainKey != KeyCode.None && noModifiers && Input.GetKeyDown(settings.ReturnToMainKey)) 
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

        private static void OnSaveGUI(UnityModManager.ModEntry modEntry)
        {
            settings.Save(modEntry);
        }

        private static void OnGUI(UnityModManager.ModEntry modEntry)
        {
            if (!IsEnabled) return; 
            if (settings == null) { /* ... error handling ... */ return; }

            // --- Bind Key Settings --- 
            GUILayout.Label("绑定键配置 (Bind Key Settings for Logical Slots):", GUILayout.ExpandWidth(false));
            GUILayout.Space(5);
            if (settings.BindKeysForLogicalSlots != null && _logicalSlotLabels != null)
            {
                for (int i = 0; i < settings.BindKeysForLogicalSlots.Length; i++)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(_logicalSlotLabels[i], GUILayout.Width(200)); 

                    string currentKeyDisplay = (_currentlySettingBindKeyForSlot == i)
                        ? "请按键..."
                        : settings.BindKeysForLogicalSlots[i].ToString();
                    GUILayout.Label(currentKeyDisplay, GUILayout.Width(120));

                    if (GUILayout.Button("设置", GUILayout.Width(60)))
                    {
                        _isSettingReturnKey = false; // Cancel other key settings
                        _currentlySettingPageKeyForLevel = -1;
                        _currentlySettingBindKeyForSlot = (_currentlySettingBindKeyForSlot == i) ? -1 : i;
                    }
                    GUILayout.EndHorizontal();
                }
            }
            GUILayout.Space(10);

            // --- Page Activation Key Settings --- 
            GUILayout.Label("页面激活键配置 (Page Activation Keys - Ctrl + YourKey):", GUILayout.ExpandWidth(false));
            GUILayout.Space(5);
            if (settings.PageActivation_Keys != null && _pageActivationLevelLabels != null)
            {
                for (int i = 0; i < settings.PageActivation_Keys.Length; i++) // 0-10 for spell levels
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(_pageActivationLevelLabels[i], GUILayout.Width(200));

                    string currentKeyDisplay = (_currentlySettingPageKeyForLevel == i)
                        ? "请按键..."
                        : settings.PageActivation_Keys[i].ToString();
                    GUILayout.Label(currentKeyDisplay, GUILayout.Width(120));

                    if (GUILayout.Button("设置", GUILayout.Width(60)))
                    {
                        _isSettingReturnKey = false;
                        _currentlySettingBindKeyForSlot = -1;
                        _currentlySettingPageKeyForLevel = (_currentlySettingPageKeyForLevel == i) ? -1 : i;
                    }
                    GUILayout.EndHorizontal();
                }
            }
            GUILayout.Space(10);

            // --- Return Key Setting --- 
            GUILayout.Label("返回主快捷栏键配置 (Return to Main Hotbar Key):", GUILayout.ExpandWidth(false));
            GUILayout.Space(5);
            GUILayout.BeginHorizontal();
            GUILayout.Label("返回键:", GUILayout.Width(200));
            string returnKeyDisplay = _isSettingReturnKey ? "请按键..." : settings.ReturnToMainKey.ToString();
            GUILayout.Label(returnKeyDisplay, GUILayout.Width(120));
            if (GUILayout.Button("设置", GUILayout.Width(60)))
            {
                _currentlySettingBindKeyForSlot = -1;
                _currentlySettingPageKeyForLevel = -1;
                _isSettingReturnKey = !_isSettingReturnKey;
            }
            GUILayout.EndHorizontal();
            GUILayout.Space(20);

            // --- Behavior Settings --- 
            GUILayout.Label("行为设置 (Behavior Settings):", GUILayout.ExpandWidth(false));
            GUILayout.Space(5);

            settings.EnableDoubleTapToReturn = GUILayout.Toggle(settings.EnableDoubleTapToReturn, "允许双击页面激活键返回主快捷栏 (Enable Double-Tap Page Activation Key to Return)");
            settings.AutoReturnAfterCast = GUILayout.Toggle(settings.AutoReturnAfterCast, "施法启动后自动返回主快捷栏 (Auto Return to Main Hotbar After Initiating Spellcast)");
            
            GUILayout.Space(20);

            // --- Key Capture Logic --- 
            if (_currentlySettingBindKeyForSlot != -1 || _currentlySettingPageKeyForLevel != -1 || _isSettingReturnKey)
            {
                string settingWhichKey = "";
                if (_currentlySettingBindKeyForSlot != -1) settingWhichKey = _logicalSlotLabels[_currentlySettingBindKeyForSlot];
                else if (_currentlySettingPageKeyForLevel != -1) settingWhichKey = _pageActivationLevelLabels[_currentlySettingPageKeyForLevel];
                else if (_isSettingReturnKey) settingWhichKey = "返回键";
                
                GUILayout.Label($"正在为 [{settingWhichKey}] 设置按键。按Esc取消。", GUILayout.ExpandWidth(true));
                
                Event e = Event.current;
                if (e.isKey && e.type == EventType.KeyDown)
                {
                    if (e.keyCode == KeyCode.Escape)
                    {
                        _currentlySettingBindKeyForSlot = -1; 
                        _currentlySettingPageKeyForLevel = -1;
                        _isSettingReturnKey = false;
                        e.Use(); 
                    }
                    else if (e.keyCode != KeyCode.None) // Allow any key except None
                    {
                        if (_currentlySettingBindKeyForSlot != -1)
                        {
                            settings.BindKeysForLogicalSlots[_currentlySettingBindKeyForSlot] = e.keyCode;
                            _currentlySettingBindKeyForSlot = -1;
                        }
                        else if (_currentlySettingPageKeyForLevel != -1)
                        {
                            settings.PageActivation_Keys[_currentlySettingPageKeyForLevel] = e.keyCode;
                            _currentlySettingPageKeyForLevel = -1;
                        }
                        else if (_isSettingReturnKey)
                        {
                            settings.ReturnToMainKey = e.keyCode;
                            _isSettingReturnKey = false;
                        }
                        // settings.SetChanged(); // UMM handles saving via OnSaveGUI
                        e.Use(); 
                    }
                }
            }
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

    public class Settings : UnityModManager.ModSettings, IDrawable
    {
        // New setting for Bind Keys, one for each logical slot
        // We support up to 12 logical slots for binding, corresponding to typical action bar sizes.
        public KeyCode[] BindKeysForLogicalSlots = new KeyCode[12];
        public KeyCode[] PageActivation_Keys = new KeyCode[11]; // For spell levels 0-10
        public KeyCode ReturnToMainKey = KeyCode.X;
        public bool EnableDoubleTapToReturn = false; // Default to false
        public bool AutoReturnAfterCast = false; // Default to false

        // Add a default constructor to initialize new settings
        public Settings()
        {
            // Ensure BindKeysForLogicalSlots is initialized, especially if UMM creates a new instance.
            // If it was already loaded, this might re-initialize if not handled carefully,
            // but for newly added settings, this is a good place.
            if (BindKeysForLogicalSlots == null || BindKeysForLogicalSlots.Length != 12)
            {
                BindKeysForLogicalSlots = new KeyCode[12];
                for (int i = 0; i < BindKeysForLogicalSlots.Length; i++)
                {
                    // We could assign defaults like KeyCode.F1, KeyCode.F2 ... KeyCode.F12 here
                    // For now, let's default all to KeyCode.None, meaning unbound.
                    BindKeysForLogicalSlots[i] = KeyCode.None;
                }
            }
            if (PageActivation_Keys == null || PageActivation_Keys.Length != 11)
            {
                PageActivation_Keys = new KeyCode[11];
                // Defaults based on quickcast.md (Ctrl + Key)
                PageActivation_Keys[0] = KeyCode.BackQuote; // Level 0 (BackQuote for ~)
                PageActivation_Keys[1] = KeyCode.Alpha1;
                PageActivation_Keys[2] = KeyCode.Alpha2;
                PageActivation_Keys[3] = KeyCode.Alpha3;
                PageActivation_Keys[4] = KeyCode.Alpha4;
                PageActivation_Keys[5] = KeyCode.Alpha5;
                PageActivation_Keys[6] = KeyCode.Alpha6;
                PageActivation_Keys[7] = KeyCode.Q;
                PageActivation_Keys[8] = KeyCode.W;
                PageActivation_Keys[9] = KeyCode.E;
                PageActivation_Keys[10] = KeyCode.R; // Level 10
            }
            // ReturnToMainKey is already initialized by its declaration with KeyCode.X
        }

        public override void Save(UnityModManager.ModEntry modEntry)
        {
            Save(this, modEntry);
        }

        public void OnChange() { /* If you have specific logic on change */ }
    }
}
