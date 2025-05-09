using Kingmaker.EntitySystem.Entities;
using Kingmaker.UI.UnitSettings; // For MechanicActionBarSlotSpell and base MechanicActionBarSlot
using Kingmaker.UnitLogic.Abilities; // For AbilityData
using Kingmaker.UnitLogic.Commands.Base; // For UnitCommand
using Kingmaker.UnitLogic.Commands; // For UnitUseAbility
using UnityEngine; // For Sprite, Color
using Kingmaker.UI.Common; // For UIUtility
using Kingmaker.Blueprints.Classes.Spells; // For SpellSchool (if needed by overrides)
using Kingmaker.Blueprints; // For BlueprintAbility
using Kingmaker.UI.MVVM._VM.Tooltip.Templates; // For TooltipTemplateAbility
using Owlcat.Runtime.UI.Tooltips; // For TooltipBaseTemplate
using Kingmaker; // For Game.Instance
using Kingmaker.UnitLogic.Abilities.Blueprints; // For AbilityTargetAnchor

namespace QuickCast
{
    public class QuickCastMechanicActionBarSlotSpell : MechanicActionBarSlotSpell
    {
        private readonly AbilityData m_Spell; // Private field to hold the spell

        public QuickCastMechanicActionBarSlotSpell(AbilityData spell, UnitEntityData owner) : base()
        {
            this.m_Spell = spell;
            this.Unit = owner; // Set the Unit property from the base class MechanicActionBarSlot
        }

        public override AbilityData Spell => m_Spell;

        protected override bool CanUseIfTurnBasedInternal()
        {
            if (this.Spell == null) return false;
            bool requireFullRoundAction = this.Spell.RequireFullRoundAction;
            UnitCommand.CommandType runtimeActionType = this.Spell.RuntimeActionType;
            return base.CanUseByActionType(requireFullRoundAction, runtimeActionType);
        }

        public override int GetResource()
        {
            return this.Spell?.GetAvailableForCastCount() ?? 0;
        }

        public override object GetContentData()
        {
            return this.Spell;
        }

        public override Sprite GetIcon()
        {
            Main.Log($"[QCMSlotSpell GetIcon] Spell: {this.Spell?.Name}, Icon is null: {this.Spell?.Icon == null}");
            AbilityData spell = this.Spell;
            if (((spell != null) ? spell.MagicHackData : null) != null)
            {
                AbilityData spell2 = this.Spell;
                return spell2?.MagicHackData.Spell2.Icon;
            }
            else
            {
                AbilityData spell3 = this.Spell;
                return spell3?.Icon;
            }
        }

        public override Sprite GetDecorationSprite()
        {
            return UIUtility.GetDecorationBorderByIndex(this.Spell?.DecorationBorderNumber ?? -1);
        }

        public override Color GetDecorationColor()
        {
            return UIUtility.GetDecorationColorByIndex(this.Spell?.DecorationColorNumber ?? -1);
        }

        public override string GetTitle()
        {
            return this.Spell?.Name ?? "";
        }

        public override string GetDescription()
        {
            return this.Spell?.ShortenedDescription ?? "";
        }

        public override bool IsCasting()
        {
            if (this.IsBad()) return false; 

            UnitUseAbility unitUseAbility = this.Unit?.Commands.Standard as UnitUseAbility;
            if (unitUseAbility != null)
            {
                BlueprintAbility blueprint = unitUseAbility.Ability.Blueprint;
                AbilityData spell = this.Spell;
                return blueprint == ((spell != null) ? spell.Blueprint : null);
            }
            return false;
        }

        public override bool IsBad()
        {
            return this.Spell == null || this.Spell.Blueprint == null || this.Spell.Caster == null || this.Spell.Blueprint.Hidden || this.Unit == null || this.Spell.SpellLevel < 0;
        }

        public override void OnClick()
        {
            Main.Log("[QCMSlotSpell] OnClick triggered!");
            base.OnClick(); 
            if (!this.IsPossibleActive(null) || this.Spell == null || this.Unit == null)
            {
                return;
            }

            if (this.Spell.TargetAnchor != AbilityTargetAnchor.Owner)
            {
                Game.Instance.SelectedAbilityHandler.SetAbility(this.Spell);
            }
            else
            {
                this.Unit.Commands.Run(UnitUseAbility.CreateCastCommand(this.Spell, this.Unit));
            }
        }

        public override TooltipBaseTemplate GetTooltipTemplate()
        {
            return this.Spell != null ? new TooltipTemplateAbility(this.Spell) : null;
        }
    }
} 