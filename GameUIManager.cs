using UnityEngine;
using Kingmaker;
using Kingmaker.UI.MVVM._PCView.ActionBar;
using Kingmaker.GameModes;
using System.Reflection; // For FieldInfo
using Kingmaker.UnitLogic.Abilities; // For AbilityData
using Kingmaker.UI.MVVM._VM.ActionBar; // For ActionBarSlotVM
using Kingmaker.UI.UnitSettings; // For MechanicActionBarSlotSpell

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
            if (CachedActionBarPCView == null)
            {
                Main.LogDebug("[GameUIManager] CachedActionBarPCView 为空，尝试通过 FindObjectOfType 查找...");
                CachedActionBarPCView = UnityEngine.Object.FindObjectOfType<ActionBarPCView>();
                if (CachedActionBarPCView != null)
                {
                    Main.LogDebug($"[GameUIManager] 成功通过 FindObjectOfType 找到并缓存了 ActionBarPCView: {CachedActionBarPCView.gameObject.name}");
                }
                else
                {
                    Main.Log("[GameUIManager] 通过 FindObjectOfType 未能找到 ActionBarPCView 实例。UI可能尚未完全加载或不存在于当前场景。");
                    return false;
                }
            }
            return CachedActionBarPCView != null;
        }

        public static bool ShouldClearCachedViewForGameMode(GameModeType gameMode)
        {
            string modeName = gameMode.ToString();
            return modeName == "MainMenu" || modeName == "LoadingScreen";
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
    }
} 