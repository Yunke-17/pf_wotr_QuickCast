using System;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UI.UnitSettings; // 用于 MechanicActionBarSlotSpell 和基础 MechanicActionBarSlot
using Kingmaker.UnitLogic.Abilities; // 用于 AbilityData
using Kingmaker.UnitLogic.Commands.Base; // 用于 UnitCommand
using Kingmaker.UnitLogic.Commands; // 用于 UnitUseAbility
using UnityEngine; // 用于 Sprite, Color
using Kingmaker.UI.Common; // 用于 UIUtility
using Kingmaker.Blueprints.Classes.Spells; // 如果重写需要，用于 SpellSchool
using Kingmaker.Blueprints; // 用于 BlueprintAbility
using Kingmaker.UI.MVVM._VM.Tooltip.Templates; // 用于 TooltipTemplateAbility
using Owlcat.Runtime.UI.Tooltips; // 用于 TooltipBaseTemplate
using Kingmaker; // 用于 Game.Instance
using Kingmaker.UnitLogic.Abilities.Blueprints; // 用于 AbilityTargetAnchor

namespace QuickCast
{
    /// <summary>
    /// 代表一个由QuickCast Mod管理并放置在主行动栏上的法术槽位。
    /// 此类继承自游戏原生的 <see cref="MechanicActionBarSlotSpell"/>，
    /// 并重写了必要的方法以提供正确的法术数据、图标和行为。
    /// 当玩家与此槽位交互时（例如点击施法），将使用这里提供的法术数据。
    /// </summary>
    public class QuickCastMechanicActionBarSlotSpell : MechanicActionBarSlotSpell
    {
        #region 核心字段与构造函数
        private readonly AbilityData m_Spell; // 私有字段，用于保存法术

        /// <summary>
        /// 构造函数，创建一个新的QuickCast法术槽位实例。
        /// </summary>
        /// <param name="spell">要在此槽位中表示的法术数据。</param>
        /// <param name="owner">施法者单位。</param>
        public QuickCastMechanicActionBarSlotSpell(AbilityData spell, UnitEntityData owner) : base()
        {
            this.m_Spell = spell;
            this.Unit = owner; // 设置基类 MechanicActionBarSlot 中的 Unit 属性
        }
        #endregion

        #region 重写基类属性与方法 (核心功能)
        /// <summary>
        /// 获取此槽位代表的法术数据。
        /// </summary>
        public override AbilityData Spell => m_Spell;

        /// <summary>
        /// （内部）检查在回合制模式下是否可以使用此法术。
        /// </summary>
        public override bool CanUseIfTurnBasedInternal()
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
        #endregion

        #region 重写基类属性与方法 (UI显示)
        public override Sprite GetIcon()
        {
            Main.LogDebug($"[QCMSlotSpell GetIcon] 法术：{this.Spell?.Name}, 图标是否为空：{this.Spell?.Icon == null}");
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

        public override int GetLevel()
        {
            if (this.Spell == null || this.Spell.Spellbook == null)
            {
                Main.LogDebug($"[QCMSlotSpell GetLevel] Spell or Spell.Spellbook is null. Spell name: {this.Spell?.Name}. Fallback to Spell.SpellLevel or -1.");
                return this.Spell?.SpellLevel ?? -1; 
            }

            int finalLevel = this.Spell.Spellbook.GetSpellLevel(this.Spell);
            Main.LogDebug($"[QCMSlotSpell GetLevel] Spell: {this.Spell.Name}, Calculated Final Level: {finalLevel} (Base: {this.Spell.SpellLevel}, MetamagicCost: {this.Spell.MetamagicData?.SpellLevelCost ?? 0}, Heighten: {this.Spell.MetamagicData?.HeightenLevel ?? 0})");
            return finalLevel;
        }
        #endregion

        #region 重写基类属性与方法 (状态与行为)
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
            Main.LogDebug($"[QuickCastMechanicActionBarSlotSpell] OnClick triggered. Spell: {this.Spell?.Name}. Unit: {this.Unit?.CharacterName}");
            
            if (this.IsBad() || !this.IsPossibleActive(null))
            {
                Main.LogDebug("[QuickCastMechanicActionBarSlotSpell] Conditions not met for casting (IsBad or !IsPossibleActive). Playing sound and showing warning via base.OnClick if necessary.");
                base.OnClick(); 
                return;
            }
        
            // 如果启用了施法后自动返回，则标记此法术
            if (Main.Settings != null && Main.Settings.AutoReturnAfterCast && this.Unit != null && this.Spell != null)
            {
                ActionBarManager.SpellCastToAutoReturn = new Tuple<UnitEntityData, BlueprintAbility>(this.Unit, this.Spell.Blueprint);
                Main.LogDebug($"[QuickCastMechanicActionBarSlotSpell] Marked spell {this.Spell.Name} for unit {this.Unit.CharacterName} for auto-return.");
            }
            else
            {
                // 如果没有启用自动返回，或信息不全，确保清除任何旧标记
                ActionBarManager.SpellCastToAutoReturn = null;
            }

            base.OnClick(); 
            Main.LogDebug("[QuickCastMechanicActionBarSlotSpell] Called base.OnClick().");
        }

        public override TooltipBaseTemplate GetTooltipTemplate()
        {
            return this.Spell != null ? new TooltipTemplateAbility(this.Spell) : null;
        }
        #endregion
    }
} 