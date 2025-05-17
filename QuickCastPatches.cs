using System; // Added for Type and Tuple
using HarmonyLib;
using Kingmaker.UI.MVVM._VM.ActionBar;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.UI.UnitSettings;
using Kingmaker; // 用于 Game
using UnityEngine; // 用于 KeyCode, Input

namespace QuickCast
{
    /// <summary>
    /// 此文件包含Mod使用的所有Harmony补丁。
    /// </summary>
    public static class QuickCastPatches
    {
        #region 共享状态与工具
        // 静态字段，用于保存对法术书UI中当前悬停的槽位VM的引用
        // 注意：当法术书UI关闭或QC模式结束时，需要适当地清除此字段。
        public static ActionBarSlotVM CurrentlyHoveredSpellbookSlotVM = null;
        #endregion

        #region ActionBarSlotVM 补丁 (法术书悬停检测与点击控制)
        [HarmonyPatch(typeof(ActionBarSlotVM), nameof(ActionBarSlotVM.OnHover))]
        static class ActionBarSlotVM_OnHover_Patch
        {
            static void Postfix(ActionBarSlotVM __instance, bool state)
            {
                // 仅当 QuickCast 模式激活且法术书UI可能可见时才监视悬停状态
                if (Main.IsEnabled && Main._actionBarManager != null && Main._actionBarManager.IsQuickCastModeActive && GameUIManager.IsSpellbookInterfaceActive())
                {
                    // 检查此槽位VM是否可能来自法术书组（例如，具有有效的SpellLevel）
                    // 或者，检查其 MechanicActionBarSlot 是否为法术类型。
                    // 检查 SpellLevel >= 0 是一个简单的启发式方法。
                    if (__instance.SpellLevel >= 0 && __instance.MechanicActionBarSlot is MechanicActionBarSlotSpell)
                    {
                        if (state) // 鼠标进入
                        {
                            CurrentlyHoveredSpellbookSlotVM = __instance;
                            // Main.Log($"[Patch Hover] Enter: Slot Lvl {__instance.SpellLevel}, Spell: {(__instance.MechanicActionBarSlot as MechanicActionBarSlotSpell)?.Spell?.Name}");
                        }
                        else // 鼠标移出
                        {
                            // 如果鼠标正在移出当前存储的悬停VM，则清除它。
                            if (CurrentlyHoveredSpellbookSlotVM == __instance)
                            {
                                CurrentlyHoveredSpellbookSlotVM = null;
                                // Main.Log($"[Patch Hover] Exit: Slot Lvl {__instance.SpellLevel}");
                            }
                        }
                    }
                    // 如果它不是法术槽，或者状态为 false 但它不是存储的那个，则不执行任何操作。
                }
                else
                {
                    // 如果QC模式未激活或法术书未激活，确保清除悬停引用。
                    if (CurrentlyHoveredSpellbookSlotVM != null)
                    {
                        // Main.Log("[Patch Hover] Clearing hover reference due to inactive state.");
                        CurrentlyHoveredSpellbookSlotVM = null;
                    }
                }
            }
        }

        // ActionBarSlotVM.OnMainClick 的新补丁
        [HarmonyPatch(typeof(ActionBarSlotVM), nameof(ActionBarSlotVM.OnMainClick))]
        static class ActionBarSlotVM_OnMainClick_Prefix_Patch
        {
            static bool Prefix(ActionBarSlotVM __instance)
            {
                // 检查槽位内容是否是我们的自定义 QuickCast 法术槽
                if (__instance.MechanicActionBarSlot is QuickCastMechanicActionBarSlotSpell qcSpellSlot)
                {
                    bool isRecentlyBoundByMod = ActionBarManager.RecentlyBoundSlotHashes.Contains(qcSpellSlot.GetHashCode()) &&
                                                Time.frameCount == ActionBarManager.FrameOfLastBindingRefresh;

                    Main.Log($"[Patch VMClickPrefix] ActionBarSlotVM.OnMainClick 为带有QC法术的槽位触发：{qcSpellSlot.Spell?.Name}。哈希值：{qcSpellSlot.GetHashCode()}。是否最近绑定：{isRecentlyBoundByMod} (帧：{Time.frameCount}, 上次绑定帧：{ActionBarManager.FrameOfLastBindingRefresh})");

                    if (isRecentlyBoundByMod)
                    {
                        Main.Log($"[Patch VMClickPrefix] 禁止 QC 法术 {qcSpellSlot.Spell?.Name} (哈希值：{qcSpellSlot.GetHashCode()}) 的 ActionBarSlotVM.OnMainClick，因为它在此帧被 QuickCast 最近绑定。测试");
                        return false; // 禁止原始 OnMainClick
                    }
                    Main.Log($"[Patch VMClickPrefix] 继续执行 QC 法术 {qcSpellSlot.Spell?.Name} (哈希值：{qcSpellSlot.GetHashCode()}) 的 ActionBarSlotVM.OnMainClick。");
                }
                else
                {
                    Main.Log($"[Patch VMClickPrefix] ActionBarSlotVM.OnMainClick 为带有非QC内容类型的槽位触发：{__instance.MechanicActionBarSlot?.GetType().Name}。继续执行。");
                }
                return true; // 对于其他槽位类型或如果不是最近绑定的，则继续执行原始 OnMainClick
            }
        }
        #endregion

        #region MechanicActionBarSlotSpell 补丁 (点击控制)
        // 为 MechanicActionBarSlotSpell.OnClick 添加的前缀补丁
        [HarmonyPatch(typeof(MechanicActionBarSlotSpell), nameof(MechanicActionBarSlotSpell.OnClick))]
        static class MechanicActionBarSlotSpell_OnClick_Prefix_Patch
        {
            static bool Prefix(MechanicActionBarSlotSpell __instance)
            {
                bool isRecentlyBoundByMod = ActionBarManager.RecentlyBoundSlotHashes.Contains(__instance.GetHashCode()) &&
                                            Time.frameCount == ActionBarManager.FrameOfLastBindingRefresh;

                Main.Log($"[Patch MechClickPrefix] 为法术触发：{__instance.Spell?.Name}。哈希值：{__instance.GetHashCode()}。是否最近绑定：{isRecentlyBoundByMod} (帧：{Time.frameCount}, 上次绑定帧：{ActionBarManager.FrameOfLastBindingRefresh})");

                if (Main.CurrentlyHoveredSpellbookSlotVM != null && Main.CurrentlyHoveredSpellbookSlotVM.MechanicActionBarSlot == __instance)
                {
                    Main.Log($"[Patch MechClickPrefix] 点击的实例是当前悬停的法术书槽位：{__instance.Spell?.Name}。这很可能是在法术书中点击。");
                }

                if (isRecentlyBoundByMod)
                {
                    Main.Log($"[Patch MechClickPrefix] 禁止 {__instance.Spell?.Name} (哈希值：{__instance.GetHashCode()}) 的 OnClick，因为它在此帧被 QuickCast 最近绑定。");
                    return false;
                }
                Main.Log($"[Patch MechClickPrefix] 继续执行 {__instance.Spell?.Name} (哈希值：{__instance.GetHashCode()}) 的原始 OnClick。");
                return true;
            }
        }
        #endregion

        #region UnitUseAbility 补丁 (施法后自动返回)
        [HarmonyPatch(typeof(Kingmaker.UnitLogic.Commands.UnitUseAbility), nameof(Kingmaker.UnitLogic.Commands.UnitUseAbility.OnEnded), new Type[] { typeof(bool) })]
        static class UnitUseAbility_OnEnded_Patch
        {
            static void Postfix(Kingmaker.UnitLogic.Commands.UnitUseAbility __instance, bool raiseEvent)
            {
                if (Main.IsEnabled && Main.Settings != null && Main.Settings.AutoReturnAfterCast && 
                    ActionBarManager.SpellCastToAutoReturn != null && 
                    __instance.Result == Kingmaker.UnitLogic.Commands.Base.UnitCommand.ResultType.Success)
                {
                    var markedCaster = ActionBarManager.SpellCastToAutoReturn.Item1;
                    var markedAbilityBlueprint = ActionBarManager.SpellCastToAutoReturn.Item2;

                    if (__instance.Executor == markedCaster && __instance.Ability?.Blueprint == markedAbilityBlueprint)
                    {
                        Main.Log($"[UnitUseAbility_OnEnded_Patch] Spell {markedAbilityBlueprint.name} by {markedCaster.CharacterName} finished successfully. Triggering auto-return.");
                        Main._actionBarManager?.TryDeactivateQuickCastMode(); 
                    }
                    ActionBarManager.SpellCastToAutoReturn = null; 
                }
                else if (ActionBarManager.SpellCastToAutoReturn != null && __instance.Result != Kingmaker.UnitLogic.Commands.Base.UnitCommand.ResultType.Success)
                {
                    var markedCaster = ActionBarManager.SpellCastToAutoReturn.Item1;
                    var markedAbilityBlueprint = ActionBarManager.SpellCastToAutoReturn.Item2;
                    if (__instance.Executor == markedCaster && __instance.Ability?.Blueprint == markedAbilityBlueprint)
                    {
                        Main.Log($"[UnitUseAbility_OnEnded_Patch] Marked spell {markedAbilityBlueprint.name} by {markedCaster.CharacterName} did not succeed ({__instance.Result}). Clearing auto-return mark.");
                        ActionBarManager.SpellCastToAutoReturn = null;
                    }
                }
            }
        }
        #endregion

        #region ActionBarSlotVM.SetMechanicSlot 补丁 (检测拖拽清除)
        [HarmonyPatch(typeof(Kingmaker.UI.MVVM._VM.ActionBar.ActionBarSlotVM), "SetMechanicSlot")] 
        // 推定签名: public void SetMechanicSlot(MechanicActionBarSlot abs)
        static class ActionBarSlotVM_SetMechanicSlot_Postfix_Patch
        {
            static void Postfix(Kingmaker.UI.MVVM._VM.ActionBar.ActionBarSlotVM __instance, MechanicActionBarSlot abs)
            {
                if (Main.IsEnabled && Main._actionBarManager != null && 
                    Main._actionBarManager.IsQuickCastModeActive && 
                    !Main._actionBarManager.IsInternalBarUpdateInProgress && 
                    !Main._actionBarManager.IsStateTransitionInProgress)
                {
                    // 检查槽位是否被设置为空
                    bool isSlotEmptied = (abs == null || abs is MechanicActionBarSlotEmpty);

                    if (isSlotEmptied)
                    {
                        int slotIndex = __instance.Index; 
                        // 确保这个索引是我们Mod管理的槽位索引之一 (0-11)
                        // _mainBarManagedSlotIndices 是 private 的，但我们知道其范围是 0-11
                        if (slotIndex >= 0 && slotIndex < 12) 
                        {
                            // 进一步确认这个 ActionBarSlotVM 实例确实是主行动栏上的那一个
                            // 而不是法术书或其他地方的。可以通过检查其父级 ActionBarVM 是否为游戏当前的 ActionBarVM
                            var gameActionBarVM = Game.Instance?.RootUiContext?.InGameVM?.StaticPartVM?.ActionBarVM;
                            if (gameActionBarVM != null && gameActionBarVM.Slots.Contains(__instance))
                            {
                                Main.Log($"[Patch SetMechanicSlot Postfix] Slot {slotIndex} in QuickCast mode was emptied (new type: {abs?.GetType().Name ?? "null"}). Attempting to unbind.");
                                Main._actionBarManager.UnbindSpellFromLogicalSlotIfActive(slotIndex);
                            }
                            // else: Log("[Patch SetMechanicSlot Postfix] Slot {slotIndex} was emptied, but it doesn't seem to be part of the main action bar VM currently.");
                        }
                        // else: Log($"[Patch SetMechanicSlot Postfix] Slot {slotIndex} was emptied, but it's outside the managed range (0-11).");
                    }
                }
            }
        }
        #endregion
    }

    /*
    #region ActionBarPCView 补丁 (视图生命周期管理)
    // 这个补丁类的作用是捕获 ActionBarPCView 实例的创建和销毁，
    // 以便Mod可以缓存其实例，用于后续操作（如访问m_SpellsGroup）。
    public static class ActionBarPCView_Patches
    {
        // 当 ActionBarPCView.BindViewImplementation 执行后调用此方法
        [HarmonyPatch(typeof(Kingmaker.UI.MVVM._PCView.ActionBar.ActionBarPCView), "BindViewImplementation")] // 目标方法
        [HarmonyPrefix] // 添加一个前缀补丁用于调试
        public static void BindViewImplementation_Prefix_DEBUG(Kingmaker.UI.MVVM._PCView.ActionBar.ActionBarPCView __instance)
        {
            Main.Log("DEBUG: ActionBarPCView.BindViewImplementation --- PREFIX --- TRIGGERED");
            // 可以在这里设置一个断点或记录更多信息，如 __instance 是否为 null
            if (__instance == null)
            {
                Main.Log("DEBUG: ActionBarPCView.BindViewImplementation --- PREFIX --- __instance IS NULL!");
            }
            else
            {
                Main.Log($"DEBUG: ActionBarPCView.BindViewImplementation --- PREFIX --- __instance is valid: {__instance.gameObject.name}");
            }
        }

        [HarmonyPatch(typeof(Kingmaker.UI.MVVM._PCView.ActionBar.ActionBarPCView), "BindViewImplementation")] // 目标方法
        [HarmonyPostfix] // 在原方法执行后执行
        public static void BindViewImplementation_Postfix(Kingmaker.UI.MVVM._PCView.ActionBar.ActionBarPCView __instance) // __instance 是 ActionBarPCView 的实例
        {
            Main.Log("ActionBarPCView_BindViewImplementation_Postfix: ActionBarPCView 实例已捕获。");
            if (__instance == null)
            {
                 Main.Log("ActionBarPCView_BindViewImplementation_Postfix: __instance IS NULL! Cannot cache.");
            }
            else
            {
            Main.CachedActionBarPCView = __instance; // 缓存实例
                Main.Log($"ActionBarPCView_BindViewImplementation_Postfix: Cached __instance: {__instance.gameObject.name}");
            }
        }

        // 当 ActionBarPCView (或其基类 ActionBarBaseView) 的 DestroyViewImplementation 执行后调用此方法
        [HarmonyPatch(typeof(Kingmaker.UI.MVVM._PCView.ActionBar.ActionBarBaseView), "DestroyViewImplementation")]
        [HarmonyPrefix] // 添加一个前缀补丁用于调试
        public static void DestroyViewImplementation_Prefix_DEBUG()
        {
            Main.Log("DEBUG: ActionBarBaseView.DestroyViewImplementation --- PREFIX --- TRIGGERED");
        }

        [HarmonyPatch(typeof(Kingmaker.UI.MVVM._PCView.ActionBar.ActionBarBaseView), "DestroyViewImplementation")]
        [HarmonyPostfix]
        public static void DestroyViewImplementation_Postfix()
        {
            Main.Log("ActionBarPCView_DestroyViewImplementation_Postfix: ActionBarPCView 实例已清除。");
            if (Main.CachedActionBarPCView != null)
            {
                Main.Log($"ActionBarPCView_DestroyViewImplementation_Postfix: Clearing cached view: {Main.CachedActionBarPCView.gameObject.name}");
            }
            Main.CachedActionBarPCView = null; // 清除缓存
        }
    }
    #endregion
    */
} 