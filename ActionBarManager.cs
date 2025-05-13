using System;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityModManagerNet;
using Kingmaker;
using Kingmaker.PubSubSystem;
using Kingmaker.UI.MVVM._PCView.ActionBar;
using Kingmaker.UI.MVVM._VM.ActionBar;
using Kingmaker.GameModes;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic;
using System.Collections.Generic;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.UI.UnitSettings;
using Kingmaker.UnitLogic.Abilities.Blueprints;
using Kingmaker.UnitLogic.Commands;

namespace QuickCast
{
    public class ActionBarManager
    {
        #region 核心状态与字段
        private int _activeQuickCastPage = -1;
        private bool _isQuickCastModeActive = false;

        // 存储被Mod覆盖的主行动栏槽位的原始内容
        private readonly Dictionary<int, MechanicActionBarSlot> _mainBarOverriddenSlotsOriginalContent;
        // 新字段：存储Mod管理的主行动栏槽位索引列表
        private readonly List<int> _mainBarManagedSlotIndices;

        // 存储Mod的法术绑定: 法术等级 -> (逻辑槽位索引 -> 法术数据)
        // TODO: 数据持久化 - 当前数据仅在运行时存在。应从UMM设置或存档中加载。
        public static Dictionary<int, Dictionary<int, AbilityData>> QuickCastBindings { get; set; }

        private readonly MechanicActionBarSlotEmpty _emptySlotPlaceholder;

        private readonly UnityModManager.ModEntry _modEntry;
        private readonly Func<ActionBarPCView> _getActionBarPCView;
        private readonly Func<FieldInfo> _getSpellsGroupFieldInfo;

        // 用于追踪主行动栏上最近被绑定的槽位的新静态字段
        public static readonly HashSet<int> RecentlyBoundSlotHashes = new HashSet<int>();
        public static int FrameOfLastBindingRefresh = -1;
        #endregion

        #region 构造函数与初始化
        public ActionBarManager(UnityModManager.ModEntry modEntry, Func<ActionBarPCView> getActionBarPCView, Func<FieldInfo> getSpellsGroupFieldInfo)
        {
            _modEntry = modEntry;
            _getActionBarPCView = getActionBarPCView;
            _getSpellsGroupFieldInfo = getSpellsGroupFieldInfo;

            _mainBarOverriddenSlotsOriginalContent = new Dictionary<int, MechanicActionBarSlot>();
            _emptySlotPlaceholder = new MechanicActionBarSlotEmpty();

            _mainBarManagedSlotIndices = new List<int> { 0, 1, 2, 3, 4, 5 };
            // 如果需要管理主快捷栏上更多的槽位（例如对应按键7,8,9,0,-,=的槽位6-11），请扩展此列表。

            // 初始化 QuickCastBindings - 目前为空。
            // 在实际场景中，这将从存档文件或UMM设置中加载。
            if (QuickCastBindings == null)
            {
                QuickCastBindings = new Dictionary<int, Dictionary<int, AbilityData>>();
            }
            // TODO: 如果需要，可以在开发过程中向QuickCastBindings添加测试数据
            // 新结构的示例:
            // if (!QuickCastBindings.ContainsKey(1)) QuickCastBindings[1] = new Dictionary<int, AbilityData>();
            // QuickCastBindings[1][0] = someDummyAbilityDataForLevel1Slot0; // 槽位0对应Alpha1
        }
        #endregion

        #region 公共属性与工具方法
        /// <summary>
        /// 如果是新的一帧，则清除用于防止在同一帧内重复处理行动栏点击的跟踪哈希集合。
        /// </summary>
        public static void ClearRecentlyBoundSlotsIfNewFrame()
        {
            if (Time.frameCount != FrameOfLastBindingRefresh)
            {
                RecentlyBoundSlotHashes.Clear();
                // 当刷新实际发生时，FrameOfLastBindingRefresh 会被更新
            }
        }

        private void Log(string message) => _modEntry?.Logger.Log(message);

        public bool IsQuickCastModeActive => _isQuickCastModeActive;
        public int ActiveQuickCastPage => _activeQuickCastPage;
        #endregion

        #region 核心行动栏管理逻辑
        // 由于施法现在由游戏处理，此方法不再被活动代码调用。
        // public IReadOnlyDictionary<KeyCode, int> GetCastKeyToSlotMapping() => _castKeyToMainBarSlotIndexMapping;

        private void RestoreMainActionBarDisplay()
        {
            var actionBarVM = Game.Instance?.RootUiContext?.InGameVM?.StaticPartVM?.ActionBarVM;
            if (actionBarVM == null || actionBarVM.Slots == null)
            {
                Log("[ABM] 无法恢复行动栏：ActionBarVM 或 Slots 不可用。");
                _mainBarOverriddenSlotsOriginalContent.Clear(); // 清除以防止陈旧数据问题
                return;
            }

            Log($"[ABM] 正在恢复主行动栏显示。有 {_mainBarOverriddenSlotsOriginalContent.Count} 个槽位需要恢复。");
            foreach (var entry in _mainBarOverriddenSlotsOriginalContent)
            {
                int slotIndex = entry.Key;
                MechanicActionBarSlot originalContent = entry.Value;

                if (slotIndex >= 0 && slotIndex < actionBarVM.Slots.Count)
                {
                    ActionBarSlotVM slotVM = actionBarVM.Slots[slotIndex];
                    if (slotVM != null)
                    {
                        slotVM.SetMechanicSlot(originalContent);
                        Log($"[ABM] 已恢复槽位 {slotIndex}。");
                    }
                    else
                    {
                        Log($"[ABM] 警告：恢复期间索引 {slotIndex} 处的 SlotVM 为空。");
                    }
                }
                else
                {
                    Log($"[ABM] 警告：恢复期间槽位索引 {slotIndex} 无效。");
                }
            }
            _mainBarOverriddenSlotsOriginalContent.Clear();
        }

        /// <summary>
        /// 根据当前激活的快捷施法页面的绑定，刷新主行动栏的显示内容。
        /// 此方法会遍历Mod管理的槽位，并用绑定的法术或空占位符填充它们。
        /// </summary>
        private void RefreshMainActionBarForCurrentQuickCastPage()
        {
            if (!_isQuickCastModeActive || _activeQuickCastPage == -1) return;

            var actionBarVM = Game.Instance?.RootUiContext?.InGameVM?.StaticPartVM?.ActionBarVM;
            var currentUnit = actionBarVM?.SelectedUnit.Value;

            if (actionBarVM == null || actionBarVM.Slots == null || currentUnit == null)
            {
                Log("[ABM] 无法刷新行动栏：ViewModel、Slots 或 Unit 不可用。");
                return;
            }

            if (Time.frameCount != FrameOfLastBindingRefresh) {
                RecentlyBoundSlotHashes.Clear();
            }
            FrameOfLastBindingRefresh = Time.frameCount;

            Log($"[ABM] 正在为快捷施法页面 {_activeQuickCastPage} 刷新主行动栏。");
            Dictionary<int, AbilityData> currentLevelBindings = null;
            if (QuickCastBindings.TryGetValue(_activeQuickCastPage, out var bindingsForLevel))
            {
                currentLevelBindings = bindingsForLevel;
                Log($"[ABM RefreshDetails] 为活动页面 {_activeQuickCastPage} 找到 {bindingsForLevel.Count} 个绑定。");
                foreach (var kvp in bindingsForLevel)
                {
                    // kvp.Key 现在是 logicalSlotIndex, kvp.Value 是 AbilityData
                    Log($"[ABM RefreshDetails]   绑定：槽位索引 {kvp.Key}, 法术：{kvp.Value?.Name ?? "NULL"}");
                }
            }
            else
            {
                Log($"[ABM RefreshDetails] 未找到活动页面 {_activeQuickCastPage} 的绑定字典。");
            }

            // 遍历 _mainBarManagedSlotIndices 以了解要更新哪些UI槽位。
            foreach (int logicalSlotIndexToRefresh in _mainBarManagedSlotIndices) // 新循环
            {
                if (logicalSlotIndexToRefresh >= 0 && logicalSlotIndexToRefresh < actionBarVM.Slots.Count)
                {
                    ActionBarSlotVM slotVM = actionBarVM.Slots[logicalSlotIndexToRefresh];
                    if (slotVM != null)
                    {
                        AbilityData boundAbility = null;
                        // 尝试从 currentLevelBindings 中获取当前 logicalSlotIndex 的绑定法术
                        currentLevelBindings?.TryGetValue(logicalSlotIndexToRefresh, out boundAbility);

                        if (boundAbility != null)
                        {
                            var newSpellSlot = new QuickCastMechanicActionBarSlotSpell(boundAbility, currentUnit);
                            slotVM.SetMechanicSlot(newSpellSlot);
                            RecentlyBoundSlotHashes.Add(newSpellSlot.GetHashCode());
                            Log($"[ABM] 已刷新UI槽位索引 {logicalSlotIndexToRefresh} 为法术：{boundAbility.Name}。哈希值：{newSpellSlot.GetHashCode()}。");

                            string iconName = slotVM.Icon.Value != null ? slotVM.Icon.Value.name : "null";
                            string spellName = slotVM.MechanicActionBarSlot?.GetTitle() ?? "null";
                            Log($"[ABM] 为UI槽位索引 {logicalSlotIndexToRefresh} 设置 SetMechanicSlot 后：VM报告图标为 {iconName}，法术为 {spellName}");
                        }
                        else
                        {
                            slotVM.SetMechanicSlot(_emptySlotPlaceholder);
                            Log($"[ABM] 已刷新UI槽位索引 {logicalSlotIndexToRefresh} 并清空（在等级 {_activeQuickCastPage} 上，逻辑槽位 {logicalSlotIndexToRefresh} 没有绑定）。");
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 将指定的法术数据绑定到给定法术等级页面的特定逻辑槽位上。
        /// 如果快捷施法模式已激活且当前页面与绑定页面相同，则会刷新行动栏。
        /// </summary>
        /// <param name="spellLevel">法术等级页面 (0-10)。</param>
        /// <param name="logicalSlotIndex">要绑定到的逻辑槽位索引 (例如，0对应主行动栏第一个受管辖的格子)。</param>
        /// <param name="spellData">要绑定的法术数据。</param>
        public void BindSpellToLogicalSlot(int spellLevel, int logicalSlotIndex, AbilityData spellData)
        {
            if (spellData == null)
            {
                Log($"[ABM] BindSpellToLogicalSlot：spellData 为空，等级 {spellLevel}，槽位索引 {logicalSlotIndex}。无法绑定。");
                return;
            }

            // 验证 logicalSlotIndex (例如，如果我们支持12个槽位，则为0-11)
            // 此检查取决于 _mainBarManagedSlotIndices 覆盖多少槽位或一个固定的最大值。
            // 目前，假设在典型的行动栏范围内是有效的。
            if (logicalSlotIndex < 0 || logicalSlotIndex >= 12) // 目前假设最多12个槽位
            {
                Log($"[ABM] BindSpellToLogicalSlot：logicalSlotIndex {logicalSlotIndex} 超出范围。无法绑定。");
                return;
            }

            if (!QuickCastBindings.ContainsKey(spellLevel))
            {
                QuickCastBindings[spellLevel] = new Dictionary<int, AbilityData>();
            }
            QuickCastBindings[spellLevel][logicalSlotIndex] = spellData;
            Log($"[ABM] 法术 '{spellData.Name}' (等级：{spellData.SpellLevel}, 来自UI等级：{spellLevel}) 已绑定到 LogicalSlotIndex {logicalSlotIndex}。");

            Log($"[ABM BindDetails] 等级 {spellLevel} 的当前绑定：");
            if (QuickCastBindings.TryGetValue(spellLevel, out var bindings))
            {
                foreach (var kvp in bindings)
                {
                    Log($"[ABM BindDetails]   逻辑槽位：{kvp.Key}, 法术：{kvp.Value?.Name ?? "NULL"}");
                }
            }
            else
            {
                Log("[ABM BindDetails]   更新后未找到此等级的绑定。");
            }

            if (_isQuickCastModeActive && _activeQuickCastPage == spellLevel)
            {
                RefreshMainActionBarForCurrentQuickCastPage();
            }
        }

        /// <summary>
        /// 尝试激活指定法术等级的快捷施法页面。
        /// 这会保存当前主行动栏的内容，然后根据该页面的绑定刷新行动栏，并尝试打开和定位法术书UI。
        /// </summary>
        /// <param name="targetPageLevel">要激活的快捷施法页面等级 (0-10)。</param>
        public void TryActivateQuickCastPage(int targetPageLevel)
        {
            Log($"[ABM] 尝试激活快捷施法页面：{targetPageLevel}");
            var game = Game.Instance;
            var actionBarVM = game?.RootUiContext?.InGameVM?.StaticPartVM?.ActionBarVM;
            var cachedView = _getActionBarPCView();
            var spellsFieldInfo = _getSpellsGroupFieldInfo(); // 这已在Main中缓存并传递给ABM构造函数

            if (cachedView == null)
            {
                Log("[ABM TryActivate] 错误：缓存的 ActionBarPCView 为空。");
                RestoreMainActionBarDisplay(); _activeQuickCastPage = -1; _isQuickCastModeActive = false; return;
            }
            if (spellsFieldInfo == null)
            {
                Log("[ABM TryActivate] 错误：spellsGroupFieldInfo (对应 m_SpellsGroup) 为空。");
                RestoreMainActionBarDisplay(); _activeQuickCastPage = -1; _isQuickCastModeActive = false; return;
            }
            if (actionBarVM == null)
            {
                Log("[ABM TryActivate] 错误：ActionBarVM 为空。");
                RestoreMainActionBarDisplay(); _activeQuickCastPage = -1; _isQuickCastModeActive = false; return;
            }

            var currentUnit = actionBarVM.SelectedUnit.Value;
            if (currentUnit == null)
            {
                Log("[ABM] 未选择单位。");
                if (_isQuickCastModeActive) TryDeactivateQuickCastMode(true);
                else { _activeQuickCastPage = -1; _isQuickCastModeActive = false; }
                return;
            }

            // 如果已处于快捷施法模式，则恢复先前的状态
            if (_isQuickCastModeActive)
            {
                RestoreMainActionBarDisplay();
            }
            _mainBarOverriddenSlotsOriginalContent.Clear(); // 在为新页面填充内容之前，确保它是干净的

            // 保存 QuickCast 将覆盖的行动栏槽位的原始内容。
            // 这必须在任何恢复（如果切换页面）之后和 RefreshMainActionBarForCurrentQuickCastPage 之前完成。
            var currentActionBarVMForSaving = game?.RootUiContext?.InGameVM?.StaticPartVM?.ActionBarVM;
            if (currentActionBarVMForSaving != null)
            {
                Log("[ABM TryActivate] 尝试在覆盖快捷施法页面之前保存当前行动栏状态。");
                foreach (int slotIndexToSave in _mainBarManagedSlotIndices) // 新循环
                {
                    if (slotIndexToSave >= 0 && slotIndexToSave < currentActionBarVMForSaving.Slots.Count)
                    {
                        ActionBarSlotVM slotVM = currentActionBarVMForSaving.Slots[slotIndexToSave];
                        if (slotVM != null)
                        {
                            // 重要：我们需要确保我们没有保存 QuickCastMechanicActionBarSlotSpell，
                            // 如果由于某种原因，先前的 QuickCast 状态未完全清除。
                            // RestoreMainActionBarDisplay 应该处理这个问题，但作为安全措施：
                            if (slotVM.MechanicActionBarSlot is QuickCastMechanicActionBarSlotSpell)
                            {
                                Log($"[ABM TryActivate 警告] 槽位 {slotIndexToSave} 在可能的恢复后仍包含QC法术。这可能表明存在问题。不将其保存为原始内容。");
                                // 可选地，尝试获取更"原始"的状态或让它被覆盖。
                                // 目前，我们只记录日志，它将被视为是空的或具有已被恢复的非QC内容。
                            }
                            else
                            {
                                _mainBarOverriddenSlotsOriginalContent[slotIndexToSave] = slotVM.MechanicActionBarSlot;
                                Log($"[ABM TryActivate] 已保存槽位索引 {slotIndexToSave} 的原始内容：{slotVM.MechanicActionBarSlot?.GetType().Name}");
                            }
                        }
                        else
                        {
                            Log($"[ABM TryActivate] 无法保存槽位索引 {slotIndexToSave} 的原始内容：SlotVM 为空。");
                        }
                    }
                    else
                    {
                        Log($"[ABM TryActivate] 无法保存槽位索引 {slotIndexToSave} 的原始内容：索引超出 {currentActionBarVMForSaving.Slots.Count} 个槽位的范围。");
                    }
                }
            }
            else
            {
                Log("[ABM TryActivate] 无法保存原始行动栏状态：currentActionBarVMForSaving 为空。");
            }

            try
            {
                var actionBarVMToSetLevel = game?.RootUiContext?.InGameVM?.StaticPartVM?.ActionBarVM;

                if (cachedView == null)
                {
                    Log("[ABM TryActivate] 错误：缓存的 ActionBarPCView 为空。");
                    RestoreMainActionBarDisplay(); _activeQuickCastPage = -1; _isQuickCastModeActive = false; return;
                }
                if (spellsFieldInfo == null)
                {
                    Log("[ABM TryActivate] 错误：spellsGroupFieldInfo (对应 m_SpellsGroup) 为空。");
                    RestoreMainActionBarDisplay(); _activeQuickCastPage = -1; _isQuickCastModeActive = false; return;
                }
                if (actionBarVMToSetLevel == null)
                {
                    Log("[ABM TryActivate] 错误：用于设置法术等级的 ActionBarVM 为空。");
                    RestoreMainActionBarDisplay(); _activeQuickCastPage = -1; _isQuickCastModeActive = false; return;
                }

                var spellsGroupInstance = spellsFieldInfo.GetValue(cachedView) as ActionBarGroupPCView;
                if (spellsGroupInstance == null)
                {
                    Log("[ABM TryActivate] 错误：通过反射从 ActionBarPCView 获取 m_SpellsGroup 实例失败。");
                    RestoreMainActionBarDisplay(); _activeQuickCastPage = -1; _isQuickCastModeActive = false; return;
                }

                Log("[ABM TryActivate] 尝试直接调用 m_SpellsGroup.SetVisible(true, true)。");
                spellsGroupInstance.SetVisible(true, true); // 强制可见性
                Log("[ABM TryActivate] 已调用 m_SpellsGroup.SetVisible(true, true)。");

                // 在使法术组可见后，在 ViewModel 中设置法术等级
                // 因为 SetVisible 可能会触发其他组隐藏，并且如果在之前完成，可能会重置法术等级。
                if (game.CurrentMode == GameModeType.Default ||
                    game.CurrentMode == GameModeType.TacticalCombat ||
                    game.CurrentMode == GameModeType.Pause)
                {
                    Log($"[ABM TryActivate] 正在将 CurrentSpellLevel.Value 设置为 {targetPageLevel}。先前的值：{actionBarVMToSetLevel.CurrentSpellLevel.Value}");
                    actionBarVMToSetLevel.CurrentSpellLevel.Value = targetPageLevel;
                    Log($"[ABM TryActivate] 法术等级已通过 VM 设置为 {targetPageLevel}。新值：{actionBarVMToSetLevel.CurrentSpellLevel.Value}");
                }
                else
                {
                    Log($"[ABM TryActivate] 游戏不处于适合设置法术等级的模式 ({game.CurrentMode})。");
                }

                _isQuickCastModeActive = true;
                _activeQuickCastPage = targetPageLevel;

                RefreshMainActionBarForCurrentQuickCastPage(); // 调用统一的刷新逻辑

                string pageNameDisplay = _activeQuickCastPage == 0 ? "戏法" : $"{_activeQuickCastPage}环";
                if (_activeQuickCastPage == 10) pageNameDisplay = "10环(神话)";
                string message = $"快捷施法: {pageNameDisplay} 已激活";
                EventBus.RaiseEvent<ILogMessageUIHandler>(h => h.HandleLogMessage(message));
                Log($"[ABM] 内部：快捷施法页面 {pageNameDisplay} 已成功激活。");
            }
            catch (Exception ex)
            {
                Log($"[ABM] TryActivateQuickCastPage 期间出错：{ex.ToString()}");
                RestoreMainActionBarDisplay(); // 出错时恢复显示
                _activeQuickCastPage = -1; _isQuickCastModeActive = false;
            }
        }

        /// <summary>
        /// 尝试停用当前的快捷施法模式。
        /// 这会恢复主行动栏到进入快捷施法模式之前的状态，并关闭由Mod打开的法术书UI（如果适用）。
        /// </summary>
        /// <param name="force">如果为true，则无论当前是否处于快捷施法模式都会尝试停用（例如，在Mod禁用时）。</param>
        public void TryDeactivateQuickCastMode(bool force = false)
        {
            Log($"[ABM] 尝试停用快捷施法模式。先前是否激活：{_isQuickCastModeActive}，强制：{force}");
            if (!_isQuickCastModeActive && !force)
            {
                Log($"[ABM] 未激活且未强制，因此不执行停用。");
                return;
            }

            RestoreMainActionBarDisplay();

            var cachedView = _getActionBarPCView();
            var spellsFieldInfo = _getSpellsGroupFieldInfo(); // 这已在Main中缓存并传递给ABM构造函数

            if (cachedView != null && spellsFieldInfo != null)
            {
                try
                {
                    var spellsGroupInstance = spellsFieldInfo.GetValue(cachedView) as ActionBarGroupPCView;
                    if (spellsGroupInstance != null)
                    {
                        Log("[ABM] 正在对 m_SpellsGroup 调用 SetVisible(false, true)。");
                        spellsGroupInstance.SetVisible(false, true);
                    }
                    else
                    {
                        Log("[ABM] 警告：停用期间 m_SpellsGroup 实例为空，无法隐藏法术书UI部分。");
                    }
                }
                catch (Exception ex)
                {
                    Log($"[ABM] 隐藏 m_SpellsGroup 时出错：{ex.ToString()}");
                }
            }
            else
            {
                Log("[ABM] 警告：ActionBarPCView 或 m_SpellsGroup FieldInfo 不可用于隐藏法术书UI部分。");
                // 如果视图/字段由于某种原因丢失，则尝试直接通知VM（已在现有代码中）
                var actionBarVM = Game.Instance?.RootUiContext?.InGameVM?.StaticPartVM?.ActionBarVM;
                if (actionBarVM != null)
                {
                    actionBarVM.UpdateGroupState(ActionBarGroupType.Spell, false);
                    Log("[ABM] 回退：直接在 VM 上调用 UpdateGroupState(Spell, false)。");
                }
            }

            string exitedPageNameDisplay = _activeQuickCastPage == 0 ? "戏法" : $"{_activeQuickCastPage}环";
            if (_activeQuickCastPage == 10) exitedPageNameDisplay = "10环(神话)";

            bool wasActuallyActive = _isQuickCastModeActive; // 捕获重置前的状态
            _isQuickCastModeActive = false;
            _activeQuickCastPage = -1;

            if (wasActuallyActive || force) { // 仅当它确实处于活动状态或被强制时才记录停用消息
                string message = $"快捷施法: 返回主快捷栏";
                EventBus.RaiseEvent<ILogMessageUIHandler>(h => h.HandleLogMessage(message));
                Log($"[ABM] 内部：从页面 {exitedPageNameDisplay} 返回到主快捷栏。");
            }
        }
        #endregion
    }
} 