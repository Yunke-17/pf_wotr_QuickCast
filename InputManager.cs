using UnityEngine;
using Kingmaker; // Required for Game.Instance
using Kingmaker.UnitLogic.Abilities; // Required for AbilityData

namespace QuickCast
{
    public static class InputManager
    {
        // Fields for double-tap logic from Main.cs
        private static KeyCode _lastPageActivationKeyPressed = KeyCode.None;
        private static float _lastPageActivationKeyPressTime = 0f;
        private const float DoubleTapTimeThreshold = 0.3f;

        public static void HandleInput()
        {
            if (!Main.IsEnabled || Main._actionBarManager == null) return;

            bool ctrlHeld = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            // bool noModifiers = !ctrlHeld && !Input.GetKey(KeyCode.LeftShift) && !Input.GetKey(KeyCode.RightShift) && !Input.GetKey(KeyCode.LeftAlt) && !Input.GetKey(KeyCode.RightAlt);

            if (ctrlHeld)
            {
                ProcessPageActivationKeys();
                return;
            }

            // ProcessBindingKeys and ProcessReturnKey check for noModifiers internally or contextually
            if (Main._actionBarManager.IsQuickCastModeActive)
            {
                if (GameUIManager.IsSpellbookInterfaceActive() && ProcessBindingKeys())
                {
                    return;
                }
            }
            
            // Original Main.cs had this check: if (noModifiers && ProcessReturnKey())
            // ProcessReturnKey is simple and only checks its own key, so direct call is fine.
            // The noModifiers check for ProcessReturnKey was mostly to avoid conflict with Ctrl in ProcessPageActivationKeys.
            // Since ProcessPageActivationKeys returns if ctrlHeld is true, this path is only reached if !ctrlHeld.
            // Additional shift/alt checks for ProcessReturnKey aren't strictly necessary unless ReturnKey itself could be Shift/Alt modified.
            // For simplicity and direct porting of existing logic flow:
            bool noOtherModifiersForReturn = !Input.GetKey(KeyCode.LeftShift) && !Input.GetKey(KeyCode.RightShift) && !Input.GetKey(KeyCode.LeftAlt) && !Input.GetKey(KeyCode.RightAlt);
            if (noOtherModifiersForReturn && ProcessReturnKey())
            {
                return;
            }
        }

        private static void ProcessPageActivationKeys()
        {
            if (!GameUIManager.EnsureCachedActionBarView() || GameUIManager.CachedActionBarPCView == null)
            {
                Main.LogDebug("[InputManager ProcessPageActivationKeys] ActionBarPCView 尚未成功缓存，跳过页面激活处理。");
                return;
            }

            for (int spellLevel = 0; spellLevel < Main.Settings.PageActivation_Keys.Length; spellLevel++)
            {
                KeyCode pageKey = Main.Settings.PageActivation_Keys[spellLevel];
                if (pageKey == KeyCode.None) continue;

                if (Input.GetKeyDown(pageKey))
                {
                    Main.LogDebug($"[QC Input SUCCESS] Ctrl + {pageKey} (对应法术等级 {spellLevel}) 已检测到！");
                    if (Main.Settings.EnableDoubleTapToReturn &&
                        Main._actionBarManager.IsQuickCastModeActive &&
                        Main._actionBarManager.ActiveQuickCastPage == spellLevel &&
                        pageKey == _lastPageActivationKeyPressed &&
                        (Time.time - _lastPageActivationKeyPressTime) < DoubleTapTimeThreshold)
                    {
                        Main.LogDebug($"[QC Input] 在活动页面键 {pageKey} 上检测到双击。返回主快捷栏。");
                        Main._actionBarManager.TryDeactivateQuickCastMode();
                        _lastPageActivationKeyPressed = KeyCode.None;
                    }
                    else
                    {
                        Main.LogDebug($"[QC Input] 尝试调用 _actionBarManager.TryActivateQuickCastMode({spellLevel}, false)");
                        Main._actionBarManager.TryActivateQuickCastMode(spellLevel, false);
                        _lastPageActivationKeyPressed = pageKey;
                        _lastPageActivationKeyPressTime = Time.time;
                    }
                    return;
                }
            }
        }

        private static bool ProcessBindingKeys()
        {
            if (GameUIManager.CachedActionBarPCView == null && !GameUIManager.EnsureCachedActionBarView()) {
                 Main.LogDebug("[InputManager ProcessBindingKeys] ActionBarPCView 尚未成功缓存，跳过绑定处理。");
                return false;
            }

            AbilityData hoveredAbility = GameUIManager.GetHoveredAbilityInSpellBookUIActions();
            if (hoveredAbility == null) return false;

            bool noModifiers = !Input.GetKey(KeyCode.LeftControl) && !Input.GetKey(KeyCode.RightControl) &&
                               !Input.GetKey(KeyCode.LeftShift) && !Input.GetKey(KeyCode.RightShift) &&
                               !Input.GetKey(KeyCode.LeftAlt) && !Input.GetKey(KeyCode.RightAlt);
            if (!noModifiers) return false;

            for (int i = 0; i < Main.Settings.BindKeysForLogicalSlots.Length; i++)
            {
                KeyCode configuredBindKey = Main.Settings.BindKeysForLogicalSlots[i];
                if (configuredBindKey != KeyCode.None && Input.GetKeyDown(configuredBindKey))
                {
                    Main.LogDebug($"[QC Input] 尝试绑定：配置的绑定键 {configuredBindKey} (用于槽位 {i}) 已按下。悬停：{hoveredAbility.Name}。QC 页面：{Main._actionBarManager.ActiveQuickCastPage}");
                    Main._actionBarManager.BindSpellToLogicalSlot(Main._actionBarManager.ActiveQuickCastPage, i, hoveredAbility);
                    BindingDataManager.SaveBindings(Main.ModEntry); // 绑定后立即保存
                    return true;
                }
            }
            return false;
        }

        private static bool ProcessReturnKey()
        {
            if (Main._actionBarManager.IsQuickCastModeActive &&
                Main.Settings.ReturnToMainKey != KeyCode.None &&
                Input.GetKeyDown(Main.Settings.ReturnToMainKey))
            {
                Main.LogDebug($"[QC Input] 返回键 {Main.Settings.ReturnToMainKey} 按下。停用快捷施法模式。");
                Main._actionBarManager.TryDeactivateQuickCastMode();
                // Reset double-tap tracking if return key is used
                _lastPageActivationKeyPressed = KeyCode.None;
                // Reset spellbook active state tracking as it might close
                if (GameUIManager._previousSpellbookActiveAndInteractableState.HasValue) GameUIManager._previousSpellbookActiveAndInteractableState = null;
                return true;
            }
            return false;
        }
    }
} 