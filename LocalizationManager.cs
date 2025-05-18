using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityModManagerNet;
using UnityEngine; // Required for Application.systemLanguage

namespace QuickCast
{
    public static class LocalizationManager
    {
        private static Dictionary<string, string> _translations;
        private static bool _isInitialized = false;
        private static string _currentLanguage = "en"; // Default to English

        public static string CurrentLanguage => _currentLanguage; // Expose current language

        public static void Initialize(UnityModManager.ModEntry modEntry)
        {
            if (_isInitialized) return;

            string targetLanguage = "en"; // Default

            if (Main.Settings != null && !string.IsNullOrEmpty(Main.Settings.SelectedLanguage))
            {
                targetLanguage = Main.Settings.SelectedLanguage;
                Main.LogDebug($"[LocalizationManager] Using language from settings: {targetLanguage}");
            }
            else if (Application.systemLanguage == SystemLanguage.Chinese || Application.systemLanguage == SystemLanguage.ChineseSimplified)
            {
                targetLanguage = "zh-CN";
                Main.LogDebug($"[LocalizationManager] Using system language: {targetLanguage}");
            }
            else
            {
                Main.LogDebug($"[LocalizationManager] Using default language: {targetLanguage}");
            }
            
            LoadTranslations(modEntry, targetLanguage);
            _isInitialized = true;
        }

        private static void LoadTranslations(UnityModManager.ModEntry modEntry, string languageCode)
        {
            _currentLanguage = languageCode; // Update current language tracker
            string langFilePath = Path.Combine(modEntry.Path, "Localization", $"{languageCode}.json");
            Main.LogDebug($"[LocalizationManager] Attempting to load translations from: {langFilePath}");

            if (File.Exists(langFilePath))
            {
                try
                {
                    string json = File.ReadAllText(langFilePath);
                    _translations = JsonConvert.DeserializeObject<Dictionary<string, string>>(json);
                    Main.LogDebug($"[LocalizationManager] Successfully loaded translations for {languageCode}. Deserialized {_translations?.Count ?? 0} entries.");
                    
                    // New detailed logging of keys
                    if (Main.Settings.EnableVerboseLogging && _translations != null && _translations.Count > 0)
                    {
                        System.Text.StringBuilder sb = new System.Text.StringBuilder();
                        sb.AppendLine($"[DEBUG] [LocalizationManager] Keys loaded for {languageCode}:");
                        foreach (var key in _translations.Keys)
                        {
                            sb.AppendLine($"[DEBUG] - {key}");
                        }
                        Main.ModEntry?.Logger.Log(sb.ToString()); // Directly use ModEntry.Logger for this debug log
                    }
                    else if (_translations == null)
                    {
                         Main.Log($"[LocalizationManager] _translations dictionary is null after deserialization for {languageCode}.");
                    }
                    else // Count is 0
                    {
                         Main.Log($"[LocalizationManager] _translations dictionary is empty (0 entries) after deserialization for {languageCode}.");
                    }
                }
                catch (System.Exception ex)
                {
                    Main.Log($"[LocalizationManager] Error loading translations for {languageCode}: {ex.Message}. JSON content may be invalid.");
                    Main.Log($"[LocalizationManager] Attempted to parse file: {langFilePath}"); 
                    _translations = new Dictionary<string, string>(); 
                }
            }
            else
            {
                Main.LogDebug($"[LocalizationManager] Translation file not found for {languageCode}.");
                _translations = new Dictionary<string, string>(); 
                if (languageCode != "en") // Fallback to English if the selected/system language file is missing
                {
                    Main.LogDebug($"[LocalizationManager] Attempting to load English fallback translations as {languageCode}.json was not found.");
                    LoadTranslations(modEntry, "en");
                }
            }
        }

        public static void ChangeLanguageAndSave(string languageCode, UnityModManager.ModEntry modEntry)
        {
            Main.LogDebug($"[LocalizationManager] Changing language to: {languageCode}");
            LoadTranslations(modEntry, languageCode);
            if (Main.Settings != null)
            {
                Main.Settings.SelectedLanguage = languageCode;
                Main.Settings.Save(modEntry); // Save settings after changing language
                Main.LogDebug($"[LocalizationManager] Saved selected language ({languageCode}) to settings.");
            }
            else
            {
                Main.Log("[LocalizationManager] Warning: Settings instance is null. Cannot save selected language.");
            }
            // After changing language, UI labels need to be re-initialized.
            SettingsUIManager.InitializeSettingLabels();
        }

        public static string GetString(string key, params object[] args)
        {
            if (!_isInitialized)
            {
                Main.Log($"[LocalizationManager] Warning: GetString called before initialization for key '{key}'.");
                return key;
            }

            if (_translations != null && _translations.TryGetValue(key, out string translatedFormat))
            {
                try
                {
                    return (args == null || args.Length == 0) ? translatedFormat : string.Format(translatedFormat, args);
                }
                catch (System.FormatException ex)
                {
                    Main.Log($"[LocalizationManager] Error formatting string for key '{key}' with format '{translatedFormat}': {ex.Message}");
                    return translatedFormat; 
                }
            }
            Main.Log($"[LocalizationManager] Warning: Translation not found for key '{key}' for language '{_currentLanguage}'. Using key as fallback.");
            return key; 
        }
    }
} 