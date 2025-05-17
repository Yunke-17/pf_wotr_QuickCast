using UnityEngine;
using UnityModManagerNet;
using System; // For Array.Copy

namespace QuickCast
{
    public static class SettingsUIManager
    {
        // Fields for tracking current setting key, labels from Main.cs
        private static int _currentlySettingBindKeyForSlot = -1;
        internal static string[] _logicalSlotLabels; // internal for Main to Initialize

        private static int _currentlySettingPageKeyForLevel = -1;
        internal static string[] _pageActivationLevelLabels; // internal for Main to Initialize

        private static bool _isSettingReturnKey = false;

        // Called from Main.Load
        public static void InitializeSettingLabels()
        {
            if (Main.Settings == null) return; // Guard clause

            // Initialize _logicalSlotLabels
            if (_logicalSlotLabels == null || _logicalSlotLabels.Length != Main.Settings.BindKeysForLogicalSlots.Length)
            {
                _logicalSlotLabels = new string[Main.Settings.BindKeysForLogicalSlots.Length];
            }
            for (int i = 0; i < _logicalSlotLabels.Length; i++)
            {
                _logicalSlotLabels[i] = LocalizationManager.GetString("LogicalSlotBindKeyFormat", i + 1);
            }

            // Initialize _pageActivationLevelLabels
            if (_pageActivationLevelLabels == null || _pageActivationLevelLabels.Length != Main.Settings.PageActivation_Keys.Length)
            {
                _pageActivationLevelLabels = new string[Main.Settings.PageActivation_Keys.Length];
            }
            for (int i = 0; i < _pageActivationLevelLabels.Length; i++)
            {
                string levelDisplay = i.ToString();
                if (i == 0) levelDisplay = LocalizationManager.GetString("SpellLevel0");
                else if (i == 10) levelDisplay = LocalizationManager.GetString("SpellLevel10");
                _pageActivationLevelLabels[i] = LocalizationManager.GetString("ActivateSpellLevelPageFormat", levelDisplay);
            }
        }

        public static void DrawSettingsGUI(UnityModManager.ModEntry modEntry)
        {
            GUILayout.Label(LocalizationManager.GetString("SettingsTitle"), new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Bold });
            GUILayout.Space(5);

            DrawLanguageSelection(modEntry);
            GUILayout.Space(15);

            GUILayout.Label(LocalizationManager.GetString("InstructionsHeader"));
            GUILayout.Label(LocalizationManager.GetString("InstructionBindKey"), GUILayout.ExpandWidth(false));
            GUILayout.Label(LocalizationManager.GetString("InstructionCancelKey"), GUILayout.ExpandWidth(false));
            GUILayout.Label(LocalizationManager.GetString("InstructionClearKey"), GUILayout.ExpandWidth(false));
            GUILayout.Label(LocalizationManager.GetString("InstructionCtrlModifier"), GUILayout.ExpandWidth(false));
            GUILayout.Label(LocalizationManager.GetString("InstructionConflict"), GUILayout.ExpandWidth(false));
            GUILayout.Space(15);

            DrawBindKeySettings();
            GUILayout.Space(10);

            DrawPageActivationKeySettings();
            GUILayout.Space(10);

            DrawReturnKeySettings();
            GUILayout.Space(20);

            DrawBehaviorSettings();
            GUILayout.Space(20);

            DrawDebugSettings();
            GUILayout.Space(20);

            DrawResetToDefaultsButton(modEntry);
            GUILayout.Space(20);

            HandleKeyCaptureForSettings();
        }

        private static void DrawLanguageSelection(UnityModManager.ModEntry modEntry)
        {
            GUILayout.Label(LocalizationManager.GetString("LanguageSettingsHeader"), GUILayout.ExpandWidth(false));
            GUILayout.Label(LocalizationManager.GetString("CurrentLanguageLabel", LocalizationManager.CurrentLanguage == "zh-CN" ? LocalizationManager.GetString("LanguageChinese") : LocalizationManager.GetString("LanguageEnglish")));
            
            GUILayout.BeginHorizontal();
            GUILayout.Label(LocalizationManager.GetString("SelectLanguageLabel"), GUILayout.Width(150));
            if (GUILayout.Button(LocalizationManager.GetString("LanguageEnglish"), GUILayout.ExpandWidth(false)))
            {
                if (LocalizationManager.CurrentLanguage != "en")
                {
                    LocalizationManager.ChangeLanguageAndSave("en", modEntry);
                }
            }
            if (GUILayout.Button(LocalizationManager.GetString("LanguageChinese"), GUILayout.ExpandWidth(false)))
            {
                if (LocalizationManager.CurrentLanguage != "zh-CN")
                {
                    LocalizationManager.ChangeLanguageAndSave("zh-CN", modEntry);
                }
            }
            GUILayout.EndHorizontal();
        }

        private static void DrawBindKeySettings()
        {
            GUILayout.Label(LocalizationManager.GetString("BindKeySettingsHeader"), GUILayout.ExpandWidth(false));
            GUILayout.Space(5);
            if (Main.Settings.BindKeysForLogicalSlots != null && _logicalSlotLabels != null)
            {
                for (int i = 0; i < Main.Settings.BindKeysForLogicalSlots.Length; i++)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(_logicalSlotLabels[i], GUILayout.Width(200));
                    string currentKeyDisplay = (_currentlySettingBindKeyForSlot == i) ? LocalizationManager.GetString("PressKeyPrompt") : Main.Settings.BindKeysForLogicalSlots[i].ToString();

                    string conflictWarning = "";
                    if (Main.Settings.BindKeysForLogicalSlots[i] != KeyCode.None)
                    {
                        for (int j = 0; j < Main.Settings.BindKeysForLogicalSlots.Length; j++)
                        {
                            if (i != j && Main.Settings.BindKeysForLogicalSlots[j] == Main.Settings.BindKeysForLogicalSlots[i])
                            {
                                conflictWarning = LocalizationManager.GetString("KeyConflictWarning");
                                break;
                            }
                        }
                    }
                    GUILayout.Label(currentKeyDisplay + conflictWarning, GUILayout.Width(180));

                    if (GUILayout.Button(LocalizationManager.GetString("SetButton"), GUILayout.Width(60)))
                    {
                        _isSettingReturnKey = false;
                        _currentlySettingPageKeyForLevel = -1;
                        _currentlySettingBindKeyForSlot = (_currentlySettingBindKeyForSlot == i) ? -1 : i;
                    }
                    GUILayout.EndHorizontal();
                }
            }
        }

        private static void DrawPageActivationKeySettings()
        {
            GUILayout.Label(LocalizationManager.GetString("PageActivationKeySettingsHeader"), GUILayout.ExpandWidth(false));
            GUILayout.Space(5);
            if (Main.Settings.PageActivation_Keys != null && _pageActivationLevelLabels != null)
            {
                for (int i = 0; i < Main.Settings.PageActivation_Keys.Length; i++)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(_pageActivationLevelLabels[i], GUILayout.Width(200));
                    string currentKeyDisplay = (_currentlySettingPageKeyForLevel == i) ? LocalizationManager.GetString("PressKeyPrompt") : Main.Settings.PageActivation_Keys[i].ToString();

                    string conflictWarning = "";
                    if (Main.Settings.PageActivation_Keys[i] != KeyCode.None)
                    {
                        for (int j = 0; j < Main.Settings.PageActivation_Keys.Length; j++)
                        {
                            if (i != j && Main.Settings.PageActivation_Keys[j] == Main.Settings.PageActivation_Keys[i])
                            {
                                conflictWarning = LocalizationManager.GetString("KeyConflictWarning");
                                break;
                            }
                        }
                    }
                    GUILayout.Label(currentKeyDisplay + conflictWarning, GUILayout.Width(180));

                    if (GUILayout.Button(LocalizationManager.GetString("SetButton"), GUILayout.Width(60)))
                    {
                        _isSettingReturnKey = false;
                        _currentlySettingBindKeyForSlot = -1;
                        _currentlySettingPageKeyForLevel = (_currentlySettingPageKeyForLevel == i) ? -1 : i;
                    }
                    GUILayout.EndHorizontal();
                }
            }
        }

        private static void DrawReturnKeySettings()
        {
            GUILayout.Label(LocalizationManager.GetString("ReturnKeySettingsHeader"), GUILayout.ExpandWidth(false));
            GUILayout.Space(5);
            GUILayout.BeginHorizontal();
            GUILayout.Label(LocalizationManager.GetString("ReturnKeyLabel"), GUILayout.Width(200));
            string returnKeyDisplay = _isSettingReturnKey ? LocalizationManager.GetString("PressKeyPrompt") : Main.Settings.ReturnToMainKey.ToString();
            GUILayout.Label(returnKeyDisplay, GUILayout.Width(120));
            if (GUILayout.Button(LocalizationManager.GetString("SetButton"), GUILayout.Width(60)))
            {
                _currentlySettingBindKeyForSlot = -1;
                _currentlySettingPageKeyForLevel = -1;
                _isSettingReturnKey = !_isSettingReturnKey;
            }
            GUILayout.EndHorizontal();
        }

        private static void DrawBehaviorSettings()
        {
            GUILayout.Label(LocalizationManager.GetString("BehaviorSettingsHeader"), GUILayout.ExpandWidth(false));
            GUILayout.Space(5);
            Main.Settings.EnableDoubleTapToReturn = GUILayout.Toggle(Main.Settings.EnableDoubleTapToReturn, LocalizationManager.GetString("EnableDoubleTapToReturnLabel"));
            Main.Settings.AutoReturnAfterCast = GUILayout.Toggle(Main.Settings.AutoReturnAfterCast, LocalizationManager.GetString("AutoReturnAfterCastLabel"));
        }

        private static void DrawDebugSettings()
        {
            GUILayout.Label(LocalizationManager.GetString("DebugSettingsHeader"), GUILayout.ExpandWidth(false));
            GUILayout.Space(5);
            if (Main.Settings != null)
            {
                Main.Settings.EnableVerboseLogging = GUILayout.Toggle(Main.Settings.EnableVerboseLogging, LocalizationManager.GetString("EnableVerboseLoggingLabel"));
            }
            else
            {
                GUILayout.Label(LocalizationManager.GetString("ErrorSettingsInstanceNull"));
            }
        }

        private static void DrawResetToDefaultsButton(UnityModManager.ModEntry modEntry)
        {
            GUILayout.Label(LocalizationManager.GetString("RestoreDefaultsHeader"), GUILayout.ExpandWidth(false));
            GUILayout.Space(5);
            if (GUILayout.Button(LocalizationManager.GetString("RestoreDefaultsButton"), GUILayout.ExpandWidth(true)))
            {
                if (Main.Settings != null)
                {
                    Main.Log(LocalizationManager.GetString("LogUserResetDefaults"));
                    Settings temporaryDefaultSettings = new Settings();

                    if (temporaryDefaultSettings.BindKeysForLogicalSlots != null && Main.Settings.BindKeysForLogicalSlots.Length == temporaryDefaultSettings.BindKeysForLogicalSlots.Length)
                    {
                        Array.Copy(temporaryDefaultSettings.BindKeysForLogicalSlots, Main.Settings.BindKeysForLogicalSlots, temporaryDefaultSettings.BindKeysForLogicalSlots.Length);
                    }
                    if (temporaryDefaultSettings.PageActivation_Keys != null && Main.Settings.PageActivation_Keys.Length == temporaryDefaultSettings.PageActivation_Keys.Length)
                    {
                        Array.Copy(temporaryDefaultSettings.PageActivation_Keys, Main.Settings.PageActivation_Keys, temporaryDefaultSettings.PageActivation_Keys.Length);
                    }
                    Main.Settings.ReturnToMainKey = temporaryDefaultSettings.ReturnToMainKey;
                    Main.Settings.EnableDoubleTapToReturn = temporaryDefaultSettings.EnableDoubleTapToReturn;
                    Main.Settings.AutoReturnAfterCast = temporaryDefaultSettings.AutoReturnAfterCast;
                    Main.Settings.EnableVerboseLogging = temporaryDefaultSettings.EnableVerboseLogging;
                    Main.Settings.SelectedLanguage = ""; // Reset selected language to allow auto-detection
                    Main.Log(LocalizationManager.GetString("LogSettingsReset"));
                    // Reload translations and re-initialize labels for the new (auto-detected or default) language
                    LocalizationManager.ChangeLanguageAndSave(Main.Settings.SelectedLanguage, modEntry);
                }
                else
                {
                    Main.Log(LocalizationManager.GetString("LogErrorResetNull"));
                }
            }
        }

        private static void HandleKeyCaptureForSettings()
        {
            if (!(_currentlySettingBindKeyForSlot != -1 || _currentlySettingPageKeyForLevel != -1 || _isSettingReturnKey))
            {
                return;
            }

            string settingWhichKeyLabel = "";
            if ( _logicalSlotLabels == null || _pageActivationLevelLabels == null) InitializeSettingLabels();
            
            if (_currentlySettingBindKeyForSlot != -1 && _logicalSlotLabels != null && _currentlySettingBindKeyForSlot < _logicalSlotLabels.Length) 
                settingWhichKeyLabel = _logicalSlotLabels[_currentlySettingBindKeyForSlot];
            else if (_currentlySettingPageKeyForLevel != -1 && _pageActivationLevelLabels != null && _currentlySettingPageKeyForLevel < _pageActivationLevelLabels.Length) 
                settingWhichKeyLabel = _pageActivationLevelLabels[_currentlySettingPageKeyForLevel];
            else if (_isSettingReturnKey) 
                settingWhichKeyLabel = LocalizationManager.GetString("ReturnKeyLabel"); 

            GUILayout.Label(LocalizationManager.GetString("SettingKeyPromptFormat", settingWhichKeyLabel.TrimEnd(':')), GUILayout.ExpandWidth(true));

            Event e = Event.current;
            if (e.isKey && e.type == EventType.KeyDown)
            {
                if (e.keyCode == KeyCode.Escape)
                {
                    Main.Log(LocalizationManager.GetString("LogKeyCaptureCancelledEscapeFormat", settingWhichKeyLabel.TrimEnd(':')));
                    _currentlySettingBindKeyForSlot = -1;
                    _currentlySettingPageKeyForLevel = -1;
                    _isSettingReturnKey = false;
                }
                else if (e.keyCode != KeyCode.None)
                {
                    string keyToLog = e.keyCode.ToString();
                    string targetLabelToLog = "";
                    if (_currentlySettingBindKeyForSlot != -1)
                    {
                        Main.Settings.BindKeysForLogicalSlots[_currentlySettingBindKeyForSlot] = e.keyCode;
                        if (_logicalSlotLabels != null && _currentlySettingBindKeyForSlot < _logicalSlotLabels.Length) targetLabelToLog = _logicalSlotLabels[_currentlySettingBindKeyForSlot].TrimEnd(':');
                        _currentlySettingBindKeyForSlot = -1;
                    }
                    else if (_currentlySettingPageKeyForLevel != -1)
                    {
                        Main.Settings.PageActivation_Keys[_currentlySettingPageKeyForLevel] = e.keyCode;
                        if (_pageActivationLevelLabels != null && _currentlySettingPageKeyForLevel < _pageActivationLevelLabels.Length) targetLabelToLog = _pageActivationLevelLabels[_currentlySettingPageKeyForLevel].TrimEnd(':');
                        _currentlySettingPageKeyForLevel = -1;
                    }
                    else if (_isSettingReturnKey)
                    {
                        Main.Settings.ReturnToMainKey = e.keyCode;
                        targetLabelToLog = LocalizationManager.GetString("ReturnKeyLabel").TrimEnd(':');
                        _isSettingReturnKey = false;
                    }
                    Main.Log(LocalizationManager.GetString("LogKeySetFormat", keyToLog, targetLabelToLog));
                }
                e.Use();
            }
            else if (e.isMouse && e.type == EventType.MouseDown && e.button == 1) // Right-click
            {
                string targetLabelToLogForClear = "";
                bool cancelled = false;
                if (_currentlySettingBindKeyForSlot != -1)
                {
                    if (_logicalSlotLabels != null && _currentlySettingBindKeyForSlot < _logicalSlotLabels.Length) targetLabelToLogForClear = _logicalSlotLabels[_currentlySettingBindKeyForSlot].TrimEnd(':');
                    Main.Settings.BindKeysForLogicalSlots[_currentlySettingBindKeyForSlot] = KeyCode.None;
                    _currentlySettingBindKeyForSlot = -1;
                    cancelled = true;
                }
                else if (_currentlySettingPageKeyForLevel != -1)
                {
                    if (_pageActivationLevelLabels != null && _currentlySettingPageKeyForLevel < _pageActivationLevelLabels.Length) targetLabelToLogForClear = _pageActivationLevelLabels[_currentlySettingPageKeyForLevel].TrimEnd(':');
                    Main.Settings.PageActivation_Keys[_currentlySettingPageKeyForLevel] = KeyCode.None;
                    _currentlySettingPageKeyForLevel = -1;
                    cancelled = true;
                }
                else if (_isSettingReturnKey)
                {
                    targetLabelToLogForClear = LocalizationManager.GetString("ReturnKeyLabel").TrimEnd(':');
                    Main.Settings.ReturnToMainKey = KeyCode.None;
                    _isSettingReturnKey = false;
                    cancelled = true;
                }

                if (cancelled)
                {
                    Main.Log(LocalizationManager.GetString("LogKeyCaptureClearedRightClickFormat", targetLabelToLogForClear));
                    e.Use();
                }
            }
        }
    }
} 