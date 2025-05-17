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
using UnityEngine.UI;

namespace QuickCast
{
    public class ActionBarManager : IGameModeHandler, ISelectionManagerUIHandler
    {
        #region 核心状态与字段
        private int _activeQuickCastPage = -1;
        private bool _isQuickCastModeActive = false;
        private int _pageToRestoreAfterDialog = -1;

        private bool _deferredRestoreQueued = false; // New field for deferred restore
        private int _pageToRestoreDeferred = -1;   // New field for deferred restore

        private bool _isInternalBarUpdate = false; // 新增：用于防止内部更新触发解绑
        private bool _isStateTransitionInProgress = false; // 新增：用于处理对话、角色切换等状态转换

        // 用于施法后自动返回的标记
        internal static Tuple<UnitEntityData, BlueprintAbility> SpellCastToAutoReturn { get; set; } = null;

        // 存储被Mod覆盖的主行动栏槽位的原始内容
        private readonly Dictionary<int, MechanicActionBarSlot> _mainBarOverriddenSlotsOriginalContent;
        // 新字段：存储Mod管理的主行动栏槽位索引列表
        // private readonly List<int> _mainBarManagedSlotIndices; // 不再需要此字段

        // 原来的静态绑定字典，现在改为按角色ID存储
        // private static Dictionary<int, Dictionary<int, AbilityData>> QuickCastBindings = new Dictionary<int, Dictionary<int, AbilityData>>();
        // 改为：Key: Character UniqueId (string), Value: Dictionary<int spellLevel, Dictionary<int logicalSlotIndex, AbilityData spell>>
        private static Dictionary<string, Dictionary<int, Dictionary<int, AbilityData>>> PerCharacterQuickCastBindings = new Dictionary<string, Dictionary<int, Dictionary<int, AbilityData>>>();

        private readonly MechanicActionBarSlotEmpty _emptySlotPlaceholder;

        private readonly UnityModManager.ModEntry _modEntry;
        private readonly Func<ActionBarPCView> _getActionBarPCView;
        private readonly Func<FieldInfo> _getSpellsGroupFieldInfo;

        // 新增：用于缓存 ActionBarGroupPCView.m_SlotsList 的 FieldInfo
        private FieldInfo _slotsListFieldInfo_Cached = null;

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

            // _mainBarManagedSlotIndices = new List<int> { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 }; // 不再需要初始化此字段
            // 如果需要管理主快捷栏上更多的槽位（例如对应按键7,8,9,0,-,=的槽位6-11），请扩展此列表。

            // 初始化 QuickCastBindings - 目前为空。
            // 在实际场景中，这将从存档文件或UMM设置中加载。
            if (PerCharacterQuickCastBindings == null)
            {
                PerCharacterQuickCastBindings = new Dictionary<string, Dictionary<int, Dictionary<int, AbilityData>>>();
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
        public bool IsInternalBarUpdateInProgress => _isInternalBarUpdate; // 新增 getter
        public bool IsStateTransitionInProgress => _isStateTransitionInProgress; // 新增 getter
        #endregion

        #region 核心行动栏管理逻辑
        // 由于施法现在由游戏处理，此方法不再被活动代码调用。
        // public IReadOnlyDictionary<KeyCode, int> GetCastKeyToSlotMapping() => _castKeyToMainBarSlotIndexMapping;

        private void RestoreMainActionBarDisplay()
        {
            _isInternalBarUpdate = true; // 设置标志
            var actionBarVM = Game.Instance?.RootUiContext?.InGameVM?.StaticPartVM?.ActionBarVM;
            if (actionBarVM == null || actionBarVM.Slots == null)
            {
                Log("[ABM] 无法恢复行动栏：ActionBarVM 或 Slots 不可用。");
                _mainBarOverriddenSlotsOriginalContent.Clear(); // 清除以防止陈旧数据问题
                _isInternalBarUpdate = false; // 清除标志
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
            _isInternalBarUpdate = false; // 清除标志
        }

        /// <summary>
        /// 根据当前激活的快捷施法页面的绑定，刷新主行动栏的显示内容。
        /// 此方法会遍历Mod管理的槽位，并用绑定的法术或空占位符填充它们。
        /// </summary>
        private void RefreshMainActionBarForCurrentQuickCastPage()
        {
            if (!_isQuickCastModeActive || _activeQuickCastPage == -1) return;

            _isInternalBarUpdate = true; // 设置标志

            var actionBarVM = Game.Instance?.RootUiContext?.InGameVM?.StaticPartVM?.ActionBarVM;
            var currentUnit = actionBarVM?.SelectedUnit.Value;

            if (actionBarVM == null || actionBarVM.Slots == null || currentUnit == null)
            {
                Log("[ABM] 无法刷新行动栏：ViewModel、Slots 或 Unit 不可用。");
                _isInternalBarUpdate = false; // 清除标志
                return;
            }

            if (Time.frameCount != FrameOfLastBindingRefresh) {
                RecentlyBoundSlotHashes.Clear();
            }
            FrameOfLastBindingRefresh = Time.frameCount;

            Log($"[ABM] 正在为快捷施法页面 {_activeQuickCastPage} 刷新主行动栏。");

            // 获取当前角色的绑定数据
            var currentCharacterBindings = GetCurrentCharacterBindings(false); // false: 不需要创建新条目，如果不存在则表示没有绑定
            Dictionary<int, AbilityData> currentLevelBindings = null;

            if (currentCharacterBindings != null && currentCharacterBindings.TryGetValue(_activeQuickCastPage, out var bindingsForLevel))
            {
                currentLevelBindings = bindingsForLevel;
                Log($"[ABM] 已找到角色 {Game.Instance?.SelectionCharacter?.CurrentSelectedCharacter?.CharacterName} 等级 {_activeQuickCastPage} 的绑定数据。");
            }
            else
            {
                Log($"[ABM] 未找到角色 {Game.Instance?.SelectionCharacter?.CurrentSelectedCharacter?.CharacterName} 等级 {_activeQuickCastPage} 的绑定数据，将显示空格子。");
            }

            // 遍历 _mainBarManagedSlotIndices 以了解要更新哪些UI槽位。
            for (int logicalSlotIndexToRefresh = 0; logicalSlotIndexToRefresh < Math.Min(actionBarVM.Slots.Count, Main.Settings.BindKeysForLogicalSlots.Length); logicalSlotIndexToRefresh++)
            {
                // 如果此槽位的绑定键未在Mod设置中配置，则QuickCast不管理此槽位，跳过刷新
                if (Main.Settings.BindKeysForLogicalSlots[logicalSlotIndexToRefresh] == KeyCode.None)
                {
                    Log($"[ABM Refresh] 槽位 {logicalSlotIndexToRefresh} 的绑定键未配置，QuickCast不管理此槽位，跳过刷新。");
                    continue;
                }

                if (logicalSlotIndexToRefresh >= 0 && logicalSlotIndexToRefresh < actionBarVM.Slots.Count) // 冗余检查，因为循环条件已包含Math.Min
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
            _isInternalBarUpdate = false; // 清除标志
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
                Log("[ABM] BindSpellToLogicalSlot：spellData 为空...无法绑定。");
                return;
            }
            if (logicalSlotIndex < 0 || logicalSlotIndex >= 12) 
            {
                Log($"[ABM] BindSpellToLogicalSlot：logicalSlotIndex {logicalSlotIndex} 超出范围。无法绑定。");
                return;
            }

            // 获取或创建当前角色的绑定数据
            var currentCharacterBindings = GetCurrentCharacterBindings(true); // true: 如果角色没有绑定，则创建
            if (currentCharacterBindings == null)
            {
                Log("[ABM] BindSpellToLogicalSlot：无法获取或创建角色绑定。可能是因为没有选中角色。绑定失败。");
                return;
            }

            if (!currentCharacterBindings.ContainsKey(spellLevel))
            {
                currentCharacterBindings[spellLevel] = new Dictionary<int, AbilityData>();
            }
            currentCharacterBindings[spellLevel][logicalSlotIndex] = spellData;
            Log($"[ABM] 法术 '{spellData.Name}' 已绑定到角色 {Game.Instance?.SelectionCharacter?.CurrentSelectedCharacter?.CharacterName} 的快捷施法等级 {spellLevel}，逻辑槽位 {logicalSlotIndex}。");

            Log($"[ABM BindDetails] 等级 {spellLevel} 的当前绑定：");
            if (GetCurrentCharacterBindings(false) != null)
            {
                foreach (var kvp in GetCurrentCharacterBindings(false)[spellLevel])
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

            // 播放绑定音效
            if (Kingmaker.UI.UISoundController.Instance != null)
            {
                Kingmaker.UI.UISoundController.Instance.Play(Kingmaker.UI.UISoundType.ActionBarSlotClick);
                Log($"[ABM BindSpell] 已为法术 '{spellData.Name}' 播放 ActionBarSlotClick 音效。");
            }
            else
            {
                Log("[ABM BindSpell] UISoundController.Instance 为空，无法为绑定播放音效。");
            }
        }

        /// <summary>
        /// 尝试激活指定法术等级的快捷施法页面。
        /// 这会保存当前主行动栏的内容，然后根据该页面的绑定刷新行动栏，并尝试打开和定位法术书UI。
        /// </summary>
        /// <param name="targetPageLevel">要激活的快捷施法页面等级 (0-10)。</param>
        public bool TryActivateQuickCastMode(int spellLevel, bool forSpellBook)
        {
            var selectedUnit = Game.Instance?.SelectionCharacter?.CurrentSelectedCharacter;
            if (selectedUnit == null)
            {
                EventBus.RaiseEvent<ILogMessageUIHandler>(h => h.HandleLogMessage("无法激活快捷施法：未选择角色。"));
                return false;
            }

            var spellbook = selectedUnit.Descriptor?.Spellbooks?.FirstOrDefault();
            if (spellbook == null)
            {
                string noSpellbookMessage = $"角色 {selectedUnit.CharacterName} 没有法术书，无法激活快捷施法。";
                EventBus.RaiseEvent<ILogMessageUIHandler>(h => h.HandleLogMessage(noSpellbookMessage));
                return false;
            }

            if (spellLevel > spellbook.MaxSpellLevel)
            {
                string spellLevelHighMessage = $"角色 {selectedUnit.CharacterName} 最高只能施放 {spellbook.MaxSpellLevel}环法术，无法打开 {spellLevel}环快捷施法。";
                EventBus.RaiseEvent<ILogMessageUIHandler>(h => h.HandleLogMessage(spellLevelHighMessage));
                return false;
            }

            Main.Log($"[ABM TryActivate] Mode: {(forSpellBook ? "Spellbook" : "QuickCast")}, Level: {spellLevel}");
            Log($"[ABM] 尝试激活快捷施法页面：{spellLevel}");
            var game = Game.Instance;
            var actionBarVM = game?.RootUiContext?.InGameVM?.StaticPartVM?.ActionBarVM;
            var cachedView = _getActionBarPCView();
            var spellsFieldInfo = _getSpellsGroupFieldInfo(); // 这已在Main中缓存并传递给ABM构造函数

            if (cachedView == null)
            {
                Log("[ABM TryActivate] 错误：缓存的 ActionBarPCView 为空。");
                RestoreMainActionBarDisplay(); _activeQuickCastPage = -1; _isQuickCastModeActive = false; return false;
            }
            if (spellsFieldInfo == null)
            {
                Log("[ABM TryActivate] 错误：spellsGroupFieldInfo (对应 m_SpellsGroup) 为空。");
                RestoreMainActionBarDisplay(); _activeQuickCastPage = -1; _isQuickCastModeActive = false; return false;
            }
            if (actionBarVM == null)
            {
                Log("[ABM TryActivate] 错误：ActionBarVM 为空。");
                RestoreMainActionBarDisplay(); _activeQuickCastPage = -1; _isQuickCastModeActive = false; return false;
            }

            var currentUnit = actionBarVM.SelectedUnit.Value;
            if (currentUnit == null)
            {
                Log("[ABM] 未选择单位。");
                if (_isQuickCastModeActive) TryDeactivateQuickCastMode(true);
                else { _activeQuickCastPage = -1; _isQuickCastModeActive = false; }
                return false;
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
                for (int slotIndexToSave = 0; slotIndexToSave < Math.Min(currentActionBarVMForSaving.Slots.Count, Main.Settings.BindKeysForLogicalSlots.Length); slotIndexToSave++)
                {
                    if (Main.Settings.BindKeysForLogicalSlots[slotIndexToSave] == KeyCode.None)
                    {
                        // 如果此槽位的绑定键未设置，则不保存其原始内容，QuickCast 不会管理此槽位
                        Log($"[ABM TryActivate] 槽位 {slotIndexToSave} 的绑定键未在Mod设置中配置，QuickCast将不会管理此槽位，不保存其原始内容。");
                        continue;
                    }

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
                    RestoreMainActionBarDisplay(); _activeQuickCastPage = -1; _isQuickCastModeActive = false; return false;
                }
                if (spellsFieldInfo == null)
                {
                    Log("[ABM TryActivate] 错误：spellsGroupFieldInfo (对应 m_SpellsGroup) 为空。");
                    RestoreMainActionBarDisplay(); _activeQuickCastPage = -1; _isQuickCastModeActive = false; return false;
                }
                if (actionBarVMToSetLevel == null)
                {
                    Log("[ABM TryActivate] 错误：用于设置法术等级的 ActionBarVM 为空。");
                    RestoreMainActionBarDisplay(); _activeQuickCastPage = -1; _isQuickCastModeActive = false; return false;
                }

                var spellsGroupInstance = spellsFieldInfo.GetValue(cachedView) as ActionBarGroupPCView;
                if (spellsGroupInstance == null)
                {
                    Log("[ABM TryActivate] 错误：通过反射从 ActionBarPCView 获取 m_SpellsGroup 实例失败。");
                    RestoreMainActionBarDisplay(); _activeQuickCastPage = -1; _isQuickCastModeActive = false; return false;
                }

                // 尝试获取和缓存 m_SlotsList FieldInfo (如果尚未缓存)
                if (_slotsListFieldInfo_Cached == null)
                {
                    try
                    {
                        _slotsListFieldInfo_Cached = typeof(ActionBarGroupPCView).GetField("m_SlotsList", BindingFlags.NonPublic | BindingFlags.Instance);
                        if (_slotsListFieldInfo_Cached != null)
                        {
                            Log("[ABM TryActivate] 成功获取并缓存了 m_SlotsList 的 FieldInfo。");
                        }
                        else
                        {
                            Log("[ABM TryActivate] 错误：未能获取 m_SlotsList 的 FieldInfo。调整法术书大小的功能将不可用。");
                        }
                    }
                    catch (Exception ex_slotsListField)
                    {
                        Log($"[ABM TryActivate] 获取 m_SlotsList FieldInfo 时出错: {ex_slotsListField.Message}。调整法术书大小的功能将不可用。");
                        _slotsListFieldInfo_Cached = null; // 确保出错时仍为null
                    }
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
                    Log($"[ABM TryActivate] 正在将 CurrentSpellLevel.Value 设置为 {spellLevel}。先前的值：{actionBarVMToSetLevel.CurrentSpellLevel.Value}");
                    actionBarVMToSetLevel.CurrentSpellLevel.Value = spellLevel;
                    Log($"[ABM TryActivate] 法术等级已通过 VM 设置为 {spellLevel}。新值：{actionBarVMToSetLevel.CurrentSpellLevel.Value}");
                }
                else
                {
                    Log($"[ABM TryActivate] 游戏不处于适合设置法术等级的模式 ({game.CurrentMode})。");
                }

                _isQuickCastModeActive = true;
                _activeQuickCastPage = spellLevel;

                RefreshMainActionBarForCurrentQuickCastPage(); // 调用统一的刷新逻辑

                string pageNameDisplay = _activeQuickCastPage == 0 ? "戏法" : $"{_activeQuickCastPage}环";
                if (_activeQuickCastPage == 10) pageNameDisplay = "10环(神话)";
                string message = $"快捷施法: {pageNameDisplay} 已激活";
                EventBus.RaiseEvent<ILogMessageUIHandler>(h => h.HandleLogMessage(message));
                Log($"[ABM] 内部：快捷施法页面 {pageNameDisplay} 已成功激活。");

                // 应用法术书大小调整逻辑 (仅当快捷施法激活时)
                if (spellsGroupInstance != null && spellbook != null) // spellbook is already checked earlier, but good for clarity
                {
                    AdaptQuickCastSpellbookUISize(spellsGroupInstance, spellbook, _activeQuickCastPage);
                }

                // 播放激活音效
                if (Kingmaker.UI.UISoundController.Instance != null)
                {
                    Kingmaker.UI.UISoundController.Instance.Play(Kingmaker.UI.UISoundType.SpellbookOpen);
                    Log("[ABM TryActivate] 已播放 SpellbookOpen 音效。");
                }
                else
                {
                    Log("[ABM TryActivate] UISoundController.Instance 为空，无法播放激活音效。");
                }

                return true;
            }
            catch (Exception ex)
            {
                Log($"[ABM] TryActivateQuickCastMode 期间出错：{ex.ToString()}");
                RestoreMainActionBarDisplay(); // 出错时恢复显示
                // 尝试在出错时恢复法术书UI大小
                var cachedViewForError = _getActionBarPCView();
                var spellsFieldInfoForError = _getSpellsGroupFieldInfo();
                if (cachedViewForError != null && spellsFieldInfoForError != null) {
                    var spellsGroupInstanceForError = spellsFieldInfoForError.GetValue(cachedViewForError) as ActionBarGroupPCView;
                    if (spellsGroupInstanceForError != null) {
                        RestoreSpellbookUISizeToDefault(spellsGroupInstanceForError);
                    }
                }
                _activeQuickCastPage = -1; _isQuickCastModeActive = false;
                return false;
            }
        }

        // 新增：调整快捷施法激活时法术书UI大小的方法
        private void AdaptQuickCastSpellbookUISize(ActionBarGroupPCView spellsGroupInstance, Spellbook spellbook, int spellLevel)
        {
            if (_slotsListFieldInfo_Cached == null) // spellsGroupInstance and spellbook checked by caller
            {
                Log("[ABM AdaptSpellbook] m_SlotsList FieldInfo 尚未缓存，无法调整法术书大小。");
                return;
            }

            try
            {
                var knownSpells = spellbook.GetKnownSpells(spellLevel);
                int actualSpellCount = knownSpells?.Count ?? 0;
                // Log($"[ABM AdaptSpellbook] 快捷施法激活，环阶 {spellLevel} 法术数量: {actualSpellCount}"); // 减少日志

                var slotsListValue = _slotsListFieldInfo_Cached.GetValue(spellsGroupInstance);
                if (slotsListValue is List<ActionBarBaseSlotPCView> spellSlotsPCList)
                {
                    int numberOfRowsNeeded = (actualSpellCount == 0) ? 1 : Mathf.CeilToInt((float)actualSpellCount / 5.0f);
                    int totalSlotsToMakeVisibleInSpellbook = numberOfRowsNeeded * 5;
                    
                    // Log($"[ABM AdaptSpellbook] 法术书调整：需要行数 {numberOfRowsNeeded}, 总可见槽位 {totalSlotsToMakeVisibleInSpellbook}。当前m_SlotsList大小: {spellSlotsPCList.Count}"); // 减少日志

                    for (int i = 0; i < spellSlotsPCList.Count; i++)
                    {
                        if (spellSlotsPCList[i] != null && spellSlotsPCList[i].gameObject != null)
                        {
                            bool shouldBeActive = i < totalSlotsToMakeVisibleInSpellbook;
                            if (spellSlotsPCList[i].gameObject.activeSelf != shouldBeActive)
                            {
                                spellSlotsPCList[i].gameObject.SetActive(shouldBeActive);
                            }
                        }
                    }
                    LayoutRebuilder.ForceRebuildLayoutImmediate(spellsGroupInstance.transform as RectTransform);
                    // Log($"[ABM AdaptSpellbook] 已调整快捷施法状态下的法术书UI大小并强制重新布局。"); // 减少日志
                }
                else
                {
                    Log("[ABM AdaptSpellbook] m_SlotsList 的值不是 List<ActionBarBaseSlotPCView>。");
                }
            }
            catch (Exception ex_resize)
            {
                Log($"[ABM AdaptSpellbook] 调整法术书大小时出错: {ex_resize.ToString()}");
            }
        }

        // 新增：恢复法术书UI大小到默认状态的方法
        private void RestoreSpellbookUISizeToDefault(ActionBarGroupPCView spellsGroupInstance)
        {
            if (_slotsListFieldInfo_Cached == null || spellsGroupInstance == null)
            {
                Log("[ABM RestoreSpellbookUI] m_SlotsList FieldInfo 未缓存或 spellsGroupInstance 为空，无法恢复法术书UI大小。");
                return;
            }

            try
            {
                var slotsListValue = _slotsListFieldInfo_Cached.GetValue(spellsGroupInstance);
                if (slotsListValue is List<ActionBarBaseSlotPCView> spellSlotsPCList)
                {
                    bool changed = false;
                    for (int i = 0; i < spellSlotsPCList.Count; i++)
                    {
                        if (spellSlotsPCList[i] != null && spellSlotsPCList[i].gameObject != null && !spellSlotsPCList[i].gameObject.activeSelf)
                        {
                            spellSlotsPCList[i].gameObject.SetActive(true);
                            changed = true;
                        }
                    }

                    if (changed)
                    {
                        LayoutRebuilder.ForceRebuildLayoutImmediate(spellsGroupInstance.transform as RectTransform);
                        Log("[ABM RestoreSpellbookUI] 已尝试恢复法术书UI所有槽位为激活并强制重新布局。");
                    }
                    // else Log("[ABM RestoreSpellbookUI] 所有法术书槽位均已激活，无需恢复。"); // 减少日志
                }
                else
                {
                    Log("[ABM RestoreSpellbookUI] m_SlotsList 的值不是 List<ActionBarBaseSlotPCView>。");
                }
            }
            catch (Exception ex_restore)
            {
                Log($"[ABM RestoreSpellbookUI] 恢复法术书大小时出错: {ex_restore.ToString()}");
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

            // 在隐藏法术书组之前，尝试恢复其UI大小
            if (cachedView != null && spellsFieldInfo != null)
            {
                var spellsGroupInstanceForRestore = spellsFieldInfo.GetValue(cachedView) as ActionBarGroupPCView;
                if (spellsGroupInstanceForRestore != null)
                {
                    RestoreSpellbookUISizeToDefault(spellsGroupInstanceForRestore);
                }
                else
                {
                    Log("[ABM TryDeactivate] 无法获取 spellsGroupInstance 以恢复UI大小。");
                }
            }
            else
            {
                Log("[ABM TryDeactivate] cachedView 或 spellsFieldInfo 为空，无法恢复法术书UI大小。");
            }

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

                // 播放停用音效
                if (Kingmaker.UI.UISoundController.Instance != null)
                {
                    Kingmaker.UI.UISoundController.Instance.Play(Kingmaker.UI.UISoundType.SpellbookClose);
                    Log("[ABM TryDeactivate] 已播放 SpellbookClose 音效。");
                }
                else
                {
                    Log("[ABM TryDeactivate] UISoundController.Instance 为空，无法播放停用音效。");
                }
            }
        }

        // 辅助方法：获取当前选中角色的绑定数据字典
        // 如果不存在该角色的绑定数据且 createIfMissing 为 true，则为其创建一个新的空字典
        private Dictionary<int, Dictionary<int, AbilityData>> GetCurrentCharacterBindings(bool createIfMissing = false)
        {
            var selectedUnit = Game.Instance?.SelectionCharacter?.CurrentSelectedCharacter;
            if (selectedUnit == null || selectedUnit.UniqueId == null)
            {
                Log("[ABM GetCurrentCharacterBindings] Error: No selected unit or unit UniqueId is null.");
                return null; // 或者返回一个临时的空字典，取决于后续逻辑如何处理
            }

            string charId = selectedUnit.UniqueId;
            if (!PerCharacterQuickCastBindings.TryGetValue(charId, out var bindings))
            {
                if (createIfMissing)
                {
                    Log($"[ABM GetCurrentCharacterBindings] No bindings found for character {selectedUnit.CharacterName} ({charId}). Creating new binding set.");
                    bindings = new Dictionary<int, Dictionary<int, AbilityData>>();
                    PerCharacterQuickCastBindings[charId] = bindings;
                }
                else
                {
                    Log($"[ABM GetCurrentCharacterBindings] No bindings found for character {selectedUnit.CharacterName} ({charId}). Not creating new set.");
                    return null; // 或者返回一个临时的空字典
                }
            }
            return bindings;
        }
        #endregion

        #region Event Handlers (IGameModeHandler, ISelectionManagerUIHandler)
        // Handler for IGameModeHandler
        public void OnGameModeStart(GameModeType gameMode)
        {
            _isStateTransitionInProgress = true; // Set state transition flag at the beginning of any game mode change
            Log($"[ABM OnGameModeStart] Game mode START: {gameMode}. QC Active: {_isQuickCastModeActive}, PageToRestoreDialog: {_pageToRestoreAfterDialog}");

            if (gameMode == GameModeType.Dialog || gameMode == GameModeType.FullScreenUi)
            {
                if (_isQuickCastModeActive)
                {
                    Log($"[ABM OnGameModeStart] {gameMode} mode started. Saving current QuickCast page: {_activeQuickCastPage} for potential restoration.");
                    // We use _pageToRestoreAfterDialog for both dialogs and full-screen UI for simplicity,
                    // as they are unlikely to be nested in a way that requires separate tracking.
                    _pageToRestoreAfterDialog = _activeQuickCastPage;
                    // We don't deactivate QuickCast here immediately.
                    // The idea is to restore it if possible when the mode stops.
                    // However, the underlying UI might reset. The transition flag should protect unbinding.
                }
                else
                {
                    // If QC is not active, ensure no previous page is lingering for restoration from these modes.
                     _pageToRestoreAfterDialog = -1;
                }
            }
            else if (_pageToRestoreAfterDialog != -1 &&
                     (gameMode == GameModeType.Default || gameMode == GameModeType.TacticalCombat || gameMode == GameModeType.GlobalMap))
            {
                // This block now specifically handles restoration *after* Dialog or FullScreenUI has *ended*
                // and we are returning to a "normal" game mode.
                Log($"[ABM OnGameModeStart] Normal game mode {gameMode} started. Queuing deferred restoration of QuickCast page: {_pageToRestoreAfterDialog} (from previous Dialog/FullScreenUI).");
                _deferredRestoreQueued = true;
                _pageToRestoreDeferred = _pageToRestoreAfterDialog;
                _pageToRestoreAfterDialog = -1; // Reset the immediate flag once queued
            }
            else
            {
                // If no specific handling for this gameMode start, and not restoring, ensure transition flag is cleared if no deferred actions.
                if (!_deferredRestoreQueued) { // Check _deferredRestoreQueued as well
                    _isStateTransitionInProgress = false;
                    Log($"[ABM OnGameModeStart] Game mode {gameMode} started. No specific QC action or pending restore. Cleared _isStateTransitionInProgress.");
                } else {
                    Log($"[ABM OnGameModeStart] Game mode {gameMode} started. No specific QC action, but _deferredRestoreQueued is true. _isStateTransitionInProgress remains.");
                }
            }
        }

        public void OnGameModeStop(GameModeType gameMode)
        {
            Log($"[ABM OnGameModeStop] Game mode STOP: {gameMode}. QC Active: {_isQuickCastModeActive}, PageToRestoreDialog: {_pageToRestoreAfterDialog}, DeferredRestoreQueued: {_deferredRestoreQueued}, PageToRestoreDeferred: {_pageToRestoreDeferred}");
            // When Dialog or FullScreenUi stops, OnGameModeStart for Default/TacticalCombat etc. should fire
            // and handle the queuing of _pageToRestoreAfterDialog. So, no direct action needed here for those specific stops
            // to trigger restoration. The critical part is that _pageToRestoreAfterDialog was set correctly in their OnGameModeStart.

            // However, we need to manage _isStateTransitionInProgress.
            // If a game mode stops, and no deferred action has been queued by its corresponding OnGameModeStart
            // (or by a character switch that might have happened during that mode),
            // then the transition might be considered over.
            if (!_deferredRestoreQueued) {
                _isStateTransitionInProgress = false;
                Log($"[ABM OnGameModeStop] Game mode {gameMode} stopped. No deferred restore was queued by a subsequent OnGameModeStart. Cleared _isStateTransitionInProgress.");
            } else {
                Log($"[ABM OnGameModeStop] Game mode {gameMode} stopped. _deferredRestoreQueued is true. _isStateTransitionInProgress remains for Update() to handle.");
            }
        }

        // Handler for ISelectionManagerUIHandler
        public void HandleSwitchSelectionUnitInGroup()
        {
            _isStateTransitionInProgress = true;
            try
            {
                if (_isQuickCastModeActive)
                {
                    Log("[ABM HandleSwitchSelectionUnitInGroup] QuickCast is active. Deactivating due to character switch.");
                    TryDeactivateQuickCastMode(true); // This will set _isQuickCastModeActive = false and _activeQuickCastPage = -1
                    
                    // Cancel any pending deferred operations specific to maintaining QC state across transitions
                    _deferredRestoreQueued = false;
                    _pageToRestoreDeferred = -1;
                }
                else
                {
                    Log("[ABM HandleSwitchSelectionUnitInGroup] Character switch detected. QuickCast was not active. No specific QC deactivation needed.");
                    // If _deferredRestoreQueued was true (e.g. from a dialog ending when QC was off),
                    // the Update() method will handle it with the new character context.
                }
            }
            finally
            {
                // This transition is considered handled by this method in terms of QC state.
                // If _deferredRestoreQueued was true and QC was NOT active, Update() will eventually clear _isStateTransitionInProgress after processing it.
                // If QC WAS active, it's now deactivated, and no QC-specific deferred actions are pending from this handler.
                // If _deferredRestoreQueued becomes true from another source after this but before Update clears it, that's a separate concern.
                // For clarity, ensure it's cleared if no dialog restore is pending for Update() to handle.
                if (!_deferredRestoreQueued) {
                     _isStateTransitionInProgress = false;
                     Log("[ABM HandleSwitchSelectionUnitInGroup] Cleared _isStateTransitionInProgress.");
                }
                else {
                    Log("[ABM HandleSwitchSelectionUnitInGroup] _isStateTransitionInProgress NOT cleared by HandleSwitch; _deferredRestoreQueued is pending for Update().");
                }
            }
        }
        #endregion

        #region Update Logic
        /// <summary>
        /// Called every frame by Main.OnUpdate to handle deferred actions.
        /// </summary>
        public void Update()
        {
            bool dialogRestoreRanThisFrame = false;

            if (_deferredRestoreQueued)
            {
                var selectedUnits = Game.Instance?.SelectionCharacter?.SelectedUnits;
                var currentSelectedCharacter = Game.Instance?.SelectionCharacter?.CurrentSelectedCharacter;

                if (selectedUnits == null || currentSelectedCharacter == null || selectedUnits.Count != 1)
                {
                    Log($"[ABM Update] Deferred dialog restore for page {_pageToRestoreDeferred} cancelled: Invalid unit selection (Count: {selectedUnits?.Count ?? -1}).");
                    if (_isQuickCastModeActive) TryDeactivateQuickCastMode(true); // Should not be active if switch deactivates
                }
                else
                {
                var spellbook = currentSelectedCharacter.Descriptor?.Spellbooks?.FirstOrDefault();
                if (spellbook == null || _pageToRestoreDeferred > spellbook.MaxSpellLevel)
                {
                        Log($"[ABM Update] Deferred dialog restore for page {_pageToRestoreDeferred} cancelled: Unit {currentSelectedCharacter.CharacterName} cannot use this spell level.");
                        if (_isQuickCastModeActive) TryDeactivateQuickCastMode(true); // Should not be active
                    }
                    else
                    {
                        Log($"[ABM Update] Executing deferred dialog restore for page: {_pageToRestoreDeferred} for unit {currentSelectedCharacter.CharacterName}. This will attempt to activate QuickCast.");
                TryActivateQuickCastMode(_pageToRestoreDeferred, false);
                    }
                }
                _deferredRestoreQueued = false;
                _pageToRestoreDeferred = -1;
                dialogRestoreRanThisFrame = true;
            }

            if (dialogRestoreRanThisFrame) // Only based on dialog restore now
            {
                if (_isQuickCastModeActive) // If TryActivateQuickCastMode above was successful
                {
                    Log($"[ABM Update] Dialog restore action completed. Performing final focused spellbook level sync to page {_activeQuickCastPage}.");
                    ForceSyncSpellbookDisplayToPage(_activeQuickCastPage); 
                }
                
                if (_isStateTransitionInProgress) 
                {
                    Log($"[ABM Update] Clearing _isStateTransitionInProgress after deferred dialog restore. QC Active: {_isQuickCastModeActive}.");
                    _isStateTransitionInProgress = false;
                }
            }
            else if (_isQuickCastModeActive && !_isInternalBarUpdate && !_isStateTransitionInProgress && Game.Instance.CurrentMode != GameModeType.FullScreenUi) 
            {
                var actionBarVM = Game.Instance?.RootUiContext?.InGameVM?.StaticPartVM?.ActionBarVM;
                if (actionBarVM != null && actionBarVM.SelectedUnit.Value != null && 
                    actionBarVM.CurrentSpellLevel.Value != _activeQuickCastPage)
                {
                    var spellbook = actionBarVM.SelectedUnit.Value.Descriptor?.Spellbooks?.FirstOrDefault();
                    if (spellbook != null && _activeQuickCastPage <= spellbook.MaxSpellLevel)
                    {
                        Log($"[ABM Update - Idle Sync] Spellbook level mismatch (VM: {actionBarVM.CurrentSpellLevel.Value}, Expected: {_activeQuickCastPage}). Forcing sync via ForceSyncSpellbookDisplayToPage.");
                        ForceSyncSpellbookDisplayToPage(_activeQuickCastPage);
                    }
                }
            }
        }
        #endregion

        #region 新增：处理拖拽清除逻辑
        /// <summary>
        /// 当检测到UI上某个逻辑槽位被清空时（例如通过拖拽清除），
        /// 如果当前QuickCast页面激活，则解除该槽位的绑定。
        /// </summary>
        /// <param name="logicalSlotIndex">被清空的逻辑槽位索引。</param>
        public void UnbindSpellFromLogicalSlotIfActive(int logicalSlotIndex)
        {
            if (!_isQuickCastModeActive || _activeQuickCastPage == -1)
            {
                // Log($"[ABM UnbindSpell] QuickCast mode not active or no page selected. Ignoring unbind for slot {logicalSlotIndex}.\");
                return;
            }

            if (logicalSlotIndex < 0 || logicalSlotIndex >= 12) // Assuming 12 managed slots
            {
                Log($"[ABM UnbindSpell] Invalid logical slot index {logicalSlotIndex} for unbinding.");
                return;
            }

            var currentCharacterBindings = GetCurrentCharacterBindings(false); // false: don't create if missing
            if (currentCharacterBindings != null && 
                currentCharacterBindings.TryGetValue(_activeQuickCastPage, out var bindingsForLevel))
            {
                if (bindingsForLevel.ContainsKey(logicalSlotIndex))
                {
                    string spellName = bindingsForLevel[logicalSlotIndex]?.Name ?? "Unknown Spell";
                    bindingsForLevel.Remove(logicalSlotIndex);
                    Log($"[ABM UnbindSpell] Spell '{spellName}' unbound from character {Game.Instance?.SelectionCharacter?.CurrentSelectedCharacter?.CharacterName}, QuickCast page {_activeQuickCastPage}, logical slot {logicalSlotIndex} due to UI clear.");
                    
                    RefreshMainActionBarForCurrentQuickCastPage();
                }
                // else: Log($"[ABM UnbindSpell] No spell was bound to logical slot {logicalSlotIndex} on page {_activeQuickCastPage}. Nothing to unbind.");
            }
            // else: Log($"[ABM UnbindSpell] No bindings found for character or page {_activeQuickCastPage}. Nothing to unbind for slot {logicalSlotIndex}.");
        }
        #endregion

        #region 新增：强制同步法术书显示环阶
        private void ForceSyncSpellbookDisplayToPage(int targetLevel)
        {
            if (!_isQuickCastModeActive || targetLevel == -1) return;

            var game = Game.Instance;
            var actionBarVM = game?.RootUiContext?.InGameVM?.StaticPartVM?.ActionBarVM;
            var cachedView = _getActionBarPCView(); 
            var spellsFieldInfo = _getSpellsGroupFieldInfo(); 

            if (actionBarVM == null || cachedView == null || spellsFieldInfo == null)
            {
                Log($"[ABM ForceSyncSpellbookDisplay] Prerequisite VMs/Fields not available. Cannot sync spellbook to level {targetLevel}.");
                return;
            }

            var currentUnit = actionBarVM.SelectedUnit.Value;
            if (currentUnit == null)
            {
                Log($"[ABM ForceSyncSpellbookDisplay] No unit selected. Cannot sync spellbook.");
                return;
            }
            
            var spellbook = currentUnit.Descriptor?.Spellbooks?.FirstOrDefault();
            if (spellbook == null || targetLevel > spellbook.MaxSpellLevel)
            {
                Log($"[ABM ForceSyncSpellbookDisplay] Unit {currentUnit.CharacterName} cannot use spell level {targetLevel}. Aborting sync.");
                var spellsGroup = spellsFieldInfo.GetValue(cachedView) as ActionBarGroupPCView;
                if (spellsGroup != null && spellsGroup.VisibleState) {
                    // spellsGroup.SetVisible(false, true); // Potentially hide if level is invalid
                }
                return;
            }

            try
            {
                var spellsGroupInstance = spellsFieldInfo.GetValue(cachedView) as ActionBarGroupPCView;
                if (spellsGroupInstance != null)
                {
                    if (!spellsGroupInstance.VisibleState)
                    {
                        if (currentUnit.Descriptor?.Spellbooks?.Any() == true) {
                            spellsGroupInstance.SetVisible(true, true);
                            Log($"[ABM ForceSyncSpellbookDisplay] Made m_SpellsGroup visible for level {targetLevel}.");
                        }
                    }

                    if (actionBarVM.CurrentSpellLevel.Value != targetLevel)
                    {
                        Log($"[ABM ForceSyncSpellbookDisplay] Spellbook level mismatch (VM: {actionBarVM.CurrentSpellLevel.Value}, Expected: {targetLevel}). Forcing sync for unit {currentUnit.CharacterName}.");
                        actionBarVM.CurrentSpellLevel.Value = targetLevel;
                    }
                }
                else
                {
                    Log("[ABM ForceSyncSpellbookDisplay] m_SpellsGroup instance is null, cannot sync level.");
                }
            }
            catch (Exception ex)
            {
                Log($"[ABM ForceSyncSpellbookDisplay] Error during spellbook level sync: {ex.ToString()}");
            }
        }
        #endregion
    }
} 