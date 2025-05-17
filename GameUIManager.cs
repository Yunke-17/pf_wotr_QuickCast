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
        internal static FieldInfo _spellsGroupFieldInfo; // internal for Main to init ActionBarManager
        internal static bool? _previousSpellbookActiveAndInteractableState = null; // internal for InputManager to reset

        public static FieldInfo GetSpellsGroupFieldInfo() => _spellsGroupFieldInfo;
        public static ActionBarPCView GetCachedActionBarPCView() => CachedActionBarPCView; // For ActionBarManager constructor


        public static bool EnsureCachedActionBarView()
        {
            if (CachedActionBarPCView == null)
            {
                Main.Log("[GameUIManager] CachedActionBarPCView 为空，尝试通过 FindObjectOfType 查找...");
                CachedActionBarPCView = UnityEngine.Object.FindObjectOfType<ActionBarPCView>();
                if (CachedActionBarPCView != null)
                {
                    Main.Log($"[GameUIManager] 成功通过 FindObjectOfType 找到并缓存了 ActionBarPCView: {CachedActionBarPCView.gameObject.name}");
                    var currentCharacter = Game.Instance?.SelectionCharacter?.CurrentSelectedCharacter;
                    if (currentCharacter != null && _spellsGroupFieldInfo == null)
                    {
                        try
                        {
                            _spellsGroupFieldInfo = typeof(ActionBarPCView).GetField("m_SpellsGroup", BindingFlags.NonPublic | BindingFlags.Instance);
                            if (_spellsGroupFieldInfo != null)
                            {
                                Main.Log("[GameUIManager] 成功获取 m_SpellsGroup 的 FieldInfo。");
                            }
                            else
                            {
                                Main.Log("[GameUIManager] 错误：未能获取 m_SpellsGroup 的 FieldInfo。");
                            }
                        }
                        catch (System.Exception ex)
                        {
                            Main.Log($"[GameUIManager] 尝试获取 m_SpellsGroup 的 FieldInfo 时出错: {ex.Message}");
                        }
                    }
                    else if (currentCharacter == null)
                    {
                        Main.Log("[GameUIManager EnsureCachedActionBarView]: 当前未选择角色，暂不尝试获取 m_SpellsGroup FieldInfo。");
                    }
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
            if (CachedActionBarPCView == null || _spellsGroupFieldInfo == null) return false;

            try
            {
                var spellsGroupView = _spellsGroupFieldInfo.GetValue(CachedActionBarPCView) as ActionBarGroupPCView;
                bool isActive = spellsGroupView != null && spellsGroupView.gameObject.activeInHierarchy;

                if (_previousSpellbookActiveAndInteractableState == null || _previousSpellbookActiveAndInteractableState.Value != isActive)
                {
                    Main.Log($"[GameUIManager IsSpellbookInterfaceActive] 法术书UI是否可见：{isActive}");
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