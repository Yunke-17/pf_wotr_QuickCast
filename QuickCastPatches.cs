using HarmonyLib;
using Kingmaker.UI.MVVM._VM.ActionBar;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.UI.UnitSettings;
using Kingmaker; // For Game
using UnityEngine; // For KeyCode, Input

namespace QuickCast
{
    public static class QuickCastPatches
    {
        // Static field to hold the reference to the currently hovered slot VM in the spellbook UI
        // NOTE: This needs to be cleared appropriately when the spellbook UI closes or QC mode ends.
        public static ActionBarSlotVM CurrentlyHoveredSpellbookSlotVM = null;

        [HarmonyPatch(typeof(ActionBarSlotVM), nameof(ActionBarSlotVM.OnHover))] 
        static class ActionBarSlotVM_OnHover_Patch
        {
            static void Postfix(ActionBarSlotVM __instance, bool state)
            {
                // Only monitor hover state when QuickCast mode is active and the spellbook UI is likely visible
                if (Main.IsEnabled && Main._actionBarManager != null && Main._actionBarManager.IsQuickCastModeActive && Main.IsSpellbookInterfaceActive())
                {
                    // Check if this slot VM is likely from the spellbook group (e.g., has a valid SpellLevel)
                    // Or alternatively, check if its MechanicActionBarSlot is a spell type.
                    // Checking SpellLevel >= 0 is a simple heuristic.
                    if (__instance.SpellLevel >= 0 && __instance.MechanicActionBarSlot is MechanicActionBarSlotSpell)
                    {
                        if (state) // Mouse Enter
                        {
                            CurrentlyHoveredSpellbookSlotVM = __instance;
                             // Main.Log($"[Patch Hover] Enter: Slot Lvl {__instance.SpellLevel}, Spell: {(__instance.MechanicActionBarSlot as MechanicActionBarSlotSpell)?.Spell?.Name}");
                        }
                        else // Mouse Exit
                        {
                            // If the mouse is exiting the currently stored hovered VM, clear it.
                            if (CurrentlyHoveredSpellbookSlotVM == __instance)
                            {
                                CurrentlyHoveredSpellbookSlotVM = null;
                                // Main.Log($"[Patch Hover] Exit: Slot Lvl {__instance.SpellLevel}");
                            }
                        }
                    }
                    // If it's not a spell slot, or state is false but it wasn't the stored one, do nothing.
                }
                else
                {
                    // If QC mode is not active or spellbook is not active, ensure the hover reference is cleared.
                    if (CurrentlyHoveredSpellbookSlotVM != null)
                    {
                       // Main.Log("[Patch Hover] Clearing hover reference due to inactive state.");
                       CurrentlyHoveredSpellbookSlotVM = null;
                    }
                }
            }
        }
        
        // Added Prefix patch for MechanicActionBarSlotSpell.OnClick
        [HarmonyPatch(typeof(MechanicActionBarSlotSpell), nameof(MechanicActionBarSlotSpell.OnClick))]
        static class MechanicActionBarSlotSpell_OnClick_Prefix_Patch
        {
            static bool Prefix(MechanicActionBarSlotSpell __instance) 
            {
                bool isRecentlyBoundByMod = ActionBarManager.RecentlyBoundSlotHashes.Contains(__instance.GetHashCode()) && 
                                            Time.frameCount == ActionBarManager.FrameOfLastBindingRefresh;

                Main.Log($"[Patch MechClickPrefix] Triggered for spell: {__instance.Spell?.Name}. Hash: {__instance.GetHashCode()}. IsRecentlyBound: {isRecentlyBoundByMod} (Frame: {Time.frameCount}, LastBindFrame: {ActionBarManager.FrameOfLastBindingRefresh})");

                if (Main.CurrentlyHoveredSpellbookSlotVM != null && Main.CurrentlyHoveredSpellbookSlotVM.MechanicActionBarSlot == __instance)
                {
                    Main.Log($"[Patch MechClickPrefix] Clicked instance is the currently hovered spellbook slot: {__instance.Spell?.Name}. This is likely a click IN THE SPELLBOOK.");
                }

                if (isRecentlyBoundByMod)
                {
                    Main.Log($"[Patch MechClickPrefix] Suppressing OnClick for {__instance.Spell?.Name} (Hash: {__instance.GetHashCode()}) because it was recently bound by QuickCast this frame.");
                    return false; 
                }
                Main.Log($"[Patch MechClickPrefix] Proceeding with original OnClick for {__instance.Spell?.Name} (Hash: {__instance.GetHashCode()}).");
                return true; 
            }
        }

        // NEW PATCH for ActionBarSlotVM.OnMainClick
        [HarmonyPatch(typeof(ActionBarSlotVM), nameof(ActionBarSlotVM.OnMainClick))]
        static class ActionBarSlotVM_OnMainClick_Prefix_Patch
        {
            static bool Prefix(ActionBarSlotVM __instance)
            {
                // Check if the slot content is our custom QuickCast spell slot
                if (__instance.MechanicActionBarSlot is QuickCastMechanicActionBarSlotSpell qcSpellSlot)
                {
                    bool isRecentlyBoundByMod = ActionBarManager.RecentlyBoundSlotHashes.Contains(qcSpellSlot.GetHashCode()) &&
                                                Time.frameCount == ActionBarManager.FrameOfLastBindingRefresh;
                    
                    Main.Log($"[Patch VMClickPrefix] ActionBarSlotVM.OnMainClick triggered for slot with QC Spell: {qcSpellSlot.Spell?.Name}. Hash: {qcSpellSlot.GetHashCode()}. IsRecentlyBound: {isRecentlyBoundByMod} (Frame: {Time.frameCount}, LastBindFrame: {ActionBarManager.FrameOfLastBindingRefresh})");

                    if (isRecentlyBoundByMod)
                    {
                        Main.Log($"[Patch VMClickPrefix] Suppressing ActionBarSlotVM.OnMainClick for QC Spell {qcSpellSlot.Spell?.Name} (Hash: {qcSpellSlot.GetHashCode()}) because it was recently bound by QuickCast this frame.");
                        return false; // Suppress original OnMainClick
                    }
                    Main.Log($"[Patch VMClickPrefix] Proceeding with ActionBarSlotVM.OnMainClick for QC Spell {qcSpellSlot.Spell?.Name} (Hash: {qcSpellSlot.GetHashCode()}).");
                }
                else
                {
                    Main.Log($"[Patch VMClickPrefix] ActionBarSlotVM.OnMainClick triggered for slot with non-QC content type: {__instance.MechanicActionBarSlot?.GetType().Name}. Proceeding.");
                }
                return true; // Proceed with original OnMainClick for other slot types or if not recently bound
            }
        }
    }
} 