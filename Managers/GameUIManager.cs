using UnityEngine;
using Kingmaker;
using Kingmaker.UI.MVVM._PCView.ActionBar;
using Kingmaker.GameModes;
using System.Reflection; // For FieldInfo
using Kingmaker.UnitLogic.Abilities; // For AbilityData
using Kingmaker.UI.MVVM._VM.ActionBar; // For ActionBarSlotVM
using Kingmaker.UI.UnitSettings; // For MechanicActionBarSlotSpell
using Kingmaker.UI.MVVM; // For RootUIContext
using Kingmaker.EntitySystem.Persistence; // For LoadingProcess

namespace QuickCast
{
    public static class GameUIManager
    {
        public static ActionBarPCView CachedActionBarPCView { get; internal set; }
        internal static bool? _previousSpellbookActiveAndInteractableState = null; // internal for InputManager to reset

        public static ActionBarGroupPCView GetSpellsGroupView()
        {
            if (CachedActionBarPCView != null)
            {
                return CachedActionBarPCView.m_SpellsGroup;
            }
            return null;
        }
        public static ActionBarPCView GetCachedActionBarPCView() => CachedActionBarPCView; // For ActionBarManager constructor


        public static bool EnsureCachedActionBarView()
        {
            // Check for MainMenu and LoadingScreen states first using their specific properties
            if ((RootUIContext.Instance != null && RootUIContext.Instance.IsMainMenu) ||
                (LoadingProcess.Instance != null && LoadingProcess.Instance.IsLoadingScreenActive))
            {
                if (CachedActionBarPCView != null)
                {
                    Main.LogDebug($"[GameUIManager] EnsureCachedActionBarView: Currently in MainMenu or LoadingScreen. Clearing existing CachedActionBarPCView.");
                    CachedActionBarPCView = null;
                }
                return false; // Do not attempt to find or use ActionBarPCView in these states
            }

            // Then, check for other game modes like GlobalMap and Kingdom using GameModeType
            // This also handles the case where Game.Instance might be null initially
            if (Game.Instance != null && ShouldClearCachedViewForGameMode(Game.Instance.CurrentMode))
            {
                if (CachedActionBarPCView != null)
                {
                    Main.LogDebug($"[GameUIManager] EnsureCachedActionBarView: Current mode is {Game.Instance.CurrentMode} (e.g., GlobalMap/Kingdom), unsuitable for standard action bar. Clearing existing CachedActionBarPCView.");
                    CachedActionBarPCView = null;
                }
                return false; // Do not attempt to find or use ActionBarPCView in these modes
            }

            if (CachedActionBarPCView == null)
            {
                Main.LogDebug("[GameUIManager] CachedActionBarPCView 为空，尝试通过 FindObjectOfType 查找...");
                
                // At this point, we are not in MainMenu, LoadingScreen, GlobalMap, or Kingdom (checked above).
                // So, it should be generally safe to try finding the view if it's truly null.

                CachedActionBarPCView = UnityEngine.Object.FindObjectOfType<ActionBarPCView>();
                if (CachedActionBarPCView != null)
                {
                    Main.LogDebug($"[GameUIManager] 成功通过 FindObjectOfType 找到并缓存了 ActionBarPCView: {CachedActionBarPCView.gameObject.name}");

                    if (CachedActionBarPCView.m_SpellsGroup == null)
                    {
                        Main.LogDebug($"[GameUIManager] Found ActionBarPCView instance, but it does not contain m_SpellsGroup. This might be a mode-specific action bar not intended for QuickCast. Discarding.");
                        CachedActionBarPCView = null; 
                        return false;
                    }
                    Main.LogDebug($"[GameUIManager] Found ActionBarPCView instance with m_SpellsGroup. Caching it.");
                }
                else
                {
                    Main.LogDebug("[GameUIManager] 通过 FindObjectOfType 未能找到 ActionBarPCView 实例。UI可能尚未完全加载或不存在于当前场景。");
                    return false;
                }
            }
            return CachedActionBarPCView != null;
        }

        public static bool ShouldClearCachedViewForGameMode(GameModeType gameMode)
        {
            // This method is now primarily for game modes other than MainMenu/LoadingScreen,
            // as those are checked directly in EnsureCachedActionBarView using their specific properties.
            // It's still useful for Main.OnUpdate to decide if it should clear the cache upon mode change.

            if (RootUIContext.Instance != null && RootUIContext.Instance.IsMainMenu)
            {
                return true; 
            }
            if (LoadingProcess.Instance != null && LoadingProcess.Instance.IsLoadingScreenActive)
            {
                return true; 
            }
            
            return gameMode == GameModeType.GlobalMap ||
                   gameMode == GameModeType.Kingdom ||
                   gameMode == GameModeType.Dialog ||
                   gameMode == GameModeType.Cutscene ||
                   gameMode == GameModeType.CutsceneGlobalMap;
        }

        public static bool IsSpellbookInterfaceActive()
        {
            if (CachedActionBarPCView == null && !EnsureCachedActionBarView())
            {
                return false;
            }
            if (CachedActionBarPCView == null || CachedActionBarPCView.m_SpellsGroup == null) return false;

            try
            {
                var spellsGroupView = CachedActionBarPCView.m_SpellsGroup;
                bool isActive = spellsGroupView != null && spellsGroupView.gameObject.activeInHierarchy;

                if (_previousSpellbookActiveAndInteractableState == null || _previousSpellbookActiveAndInteractableState.Value != isActive)
                {
                    Main.LogDebug($"[GameUIManager IsSpellbookInterfaceActive] 法术书UI是否可见：{isActive}");
                    _previousSpellbookActiveAndInteractableState = isActive;
                }
                return isActive;
            }
            catch (System.Exception ex)
            {
                Main.Log($"检查 IsSpellbookInterfaceActive 时出错：{ex.Message}");
                _previousSpellbookActiveAndInteractableState = false;
                return false;
            }
        }

        public static AbilityData GetHoveredAbilityInSpellBookUIActions()
        {
            if (CachedActionBarPCView == null && !EnsureCachedActionBarView())
            {
                return null;
            }

            var actionBarVM = Game.Instance?.RootUiContext?.InGameVM?.StaticPartVM?.ActionBarVM;
            var hoveredVM = QuickCastPatches.CurrentlyHoveredSpellbookSlotVM;

            if (actionBarVM == null || hoveredVM == null) return null;

            if (hoveredVM.SpellLevel == actionBarVM.CurrentSpellLevel.Value)
            {
                if (hoveredVM.MechanicActionBarSlot is MechanicActionBarSlotSpell gameSpellSlot)
                {
                    return gameSpellSlot.Spell;
                }
            }
            return null;
        }

        public static void ClearCachedActionBarView()
        {
            CachedActionBarPCView = null;
        }
    }
} 