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
using TMPro;
using Kingmaker.UI.Common;
using Kingmaker.Blueprints.Root.Strings;

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
        // 改为：Key: Character UniqueId (string), Value: Dictionary<int spellLevel, Dictionary<int logicalSlotIndex, string spellBlueprintGuid>>
        private static Dictionary<string, Dictionary<int, Dictionary<int, string>>> PerCharacterQuickCastSpellIds = new Dictionary<string, Dictionary<int, Dictionary<int, string>>>();

        private readonly MechanicActionBarSlotEmpty _emptySlotPlaceholder;

        private readonly UnityModManager.ModEntry _modEntry;
        private readonly Func<ActionBarPCView> _getActionBarPCView;
        private readonly Func<ActionBarGroupPCView> _getSpellsGroupView;

        // 用于追踪主行动栏上最近被绑定的槽位的新静态字段
        public static readonly HashSet<int> RecentlyBoundSlotHashes = new HashSet<int>();
        public static int FrameOfLastBindingRefresh = -1;

        // 新增: 用于修改法术书组标题的反射缓存和原始标题存储
        private string _originalSpellGroupName = null;
        #endregion

        #region 构造函数与初始化
        public ActionBarManager(UnityModManager.ModEntry modEntry, Func<ActionBarPCView> getActionBarPCView, Func<ActionBarGroupPCView> getSpellsGroupView)
        {
            _modEntry = modEntry;
            _getActionBarPCView = getActionBarPCView;
            _getSpellsGroupView = getSpellsGroupView;

            _mainBarOverriddenSlotsOriginalContent = new Dictionary<int, MechanicActionBarSlot>();
            _emptySlotPlaceholder = new MechanicActionBarSlotEmpty();

            // _mainBarManagedSlotIndices = new List<int> { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 }; // 不再需要初始化此字段
            // 如果需要管理主快捷栏上更多的槽位（例如对应按键7,8,9,0,-,=的槽位6-11），请扩展此列表。

            // 初始化 PerCharacterQuickCastSpellIds - 目前为空。
            // 在实际场景中，这将从存档文件或UMM设置中加载。
            if (PerCharacterQuickCastSpellIds == null)
            {
                PerCharacterQuickCastSpellIds = new Dictionary<string, Dictionary<int, Dictionary<int, string>>>();
            }
            // TODO: 如果需要，可以在开发过程中向PerCharacterQuickCastSpellIds添加测试数据
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

        // 标准日志，始终输出
        private void Log(string message) => _modEntry?.Logger.Log(message);
        // 详细调试日志，仅在设置启用时输出
        private void LogDebug(string message)
        {
            if (Main.Settings.EnableVerboseLogging)
            {
                _modEntry?.Logger.Log("[DEBUG] " + message);
            }
        }

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
                Log("[ABM] 无法恢复行动栏：ActionBarVM 或 Slots 不可用。"); // 保留为关键日志
                _mainBarOverriddenSlotsOriginalContent.Clear(); // 清除以防止陈旧数据问题
                _isInternalBarUpdate = false; // 清除标志
                return;
            }

            LogDebug($"[ABM] 正在恢复主行动栏显示。有 {_mainBarOverriddenSlotsOriginalContent.Count} 个槽位需要恢复。");
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
                        LogDebug($"[ABM] 已恢复槽位 {slotIndex}。");
                    }
                    else
                    {
                        Log($"[ABM] 警告：恢复期间索引 {slotIndex} 处的 SlotVM 为空。"); // 保留为警告
                    }
                }
                else
                {
                    Log($"[ABM] 警告：恢复期间槽位索引 {slotIndex} 无效。"); // 保留为警告
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
                Log("[ABM] 无法刷新行动栏：ViewModel、Slots 或 Unit 不可用。"); // 保留为关键日志
                _isInternalBarUpdate = false; // 清除标志
                return;
            }

            if (Time.frameCount != FrameOfLastBindingRefresh) {
                RecentlyBoundSlotHashes.Clear();
            }
            FrameOfLastBindingRefresh = Time.frameCount;

            LogDebug($"[ABM] 正在为快捷施法页面 {_activeQuickCastPage} 刷新主行动栏。");

            // 获取当前角色的绑定数据
            var currentCharacterSpellIdBindings = BindingDataManager.GetCurrentCharacterBindings(false); // 使用 BindingDataManager
            Dictionary<int, Tuple<string, int, int, int, int>> currentLevelSpellIdBindings = null;

            if (currentCharacterSpellIdBindings != null && currentCharacterSpellIdBindings.TryGetValue(_activeQuickCastPage, out var bindingsForLevel))
            {
                currentLevelSpellIdBindings = bindingsForLevel;
                LogDebug($"[ABM] 已找到角色 {Game.Instance?.SelectionCharacter?.CurrentSelectedCharacter?.CharacterName} 等级 {_activeQuickCastPage} 的绑定数据。");
            }
            else
            {
                LogDebug($"[ABM] 未找到角色 {Game.Instance?.SelectionCharacter?.CurrentSelectedCharacter?.CharacterName} 等级 {_activeQuickCastPage} 的绑定数据，将显示空格子。");
            }

            // 遍历 _mainBarManagedSlotIndices 以了解要更新哪些UI槽位。
            for (int logicalSlotIndexToRefresh = 0; logicalSlotIndexToRefresh < Math.Min(actionBarVM.Slots.Count, Main.Settings.BindKeysForLogicalSlots.Length); logicalSlotIndexToRefresh++)
            {
                // 如果此槽位的绑定键未在Mod设置中配置，则QuickCast不管理此槽位，跳过刷新
                if (Main.Settings.BindKeysForLogicalSlots[logicalSlotIndexToRefresh] == KeyCode.None)
                {
                    LogDebug($"[ABM Refresh] 槽位 {logicalSlotIndexToRefresh} 的绑定键未配置，QuickCast不管理此槽位，跳过刷新。");
                    continue;
                }

                if (logicalSlotIndexToRefresh >= 0 && logicalSlotIndexToRefresh < actionBarVM.Slots.Count) // 冗余检查，因为循环条件已包含Math.Min
                {
                    ActionBarSlotVM slotVM = actionBarVM.Slots[logicalSlotIndexToRefresh];
                    if (slotVM != null)
                    {
                        AbilityData boundAbility = null;
                        // 尝试从 currentLevelBindings 中获取当前 logicalSlotIndex 的绑定法术
                        if (currentLevelSpellIdBindings != null && currentLevelSpellIdBindings.TryGetValue(logicalSlotIndexToRefresh, out var spellInfo))
                        {
                            string spellGuid = spellInfo.Item1;
                            int metamagicMask = spellInfo.Item2;
                            int targetHeightenLevel = spellInfo.Item3;
                            int decorationColor = spellInfo.Item4;
                            int decorationBorder = spellInfo.Item5;

                            boundAbility = BindingDataManager.GetBoundAbilityData(currentUnit, spellGuid, metamagicMask, targetHeightenLevel, decorationColor, decorationBorder);

                            if (boundAbility != null)
                            {
                                var newSpellSlot = new QuickCastMechanicActionBarSlotSpell(boundAbility, currentUnit);
                                slotVM.SetMechanicSlot(newSpellSlot);
                                RecentlyBoundSlotHashes.Add(newSpellSlot.GetHashCode());
                                LogDebug($"[ABM] 已刷新UI槽位索引 {logicalSlotIndexToRefresh} 为法术：{boundAbility.Name}。哈希值：{newSpellSlot.GetHashCode()}。");

                                string iconName = slotVM.Icon.Value != null ? slotVM.Icon.Value.name : "null";
                                string spellName = slotVM.MechanicActionBarSlot?.GetTitle() ?? "null";
                                LogDebug($"[ABM] 为UI槽位索引 {logicalSlotIndexToRefresh} 设置 SetMechanicSlot 后：VM报告图标为 {iconName}，法术为 {spellName}");
                            }
                            else
                            {
                                slotVM.SetMechanicSlot(_emptySlotPlaceholder);
                                LogDebug($"[ABM] 已刷新UI槽位索引 {logicalSlotIndexToRefresh} 并清空（在等级 {_activeQuickCastPage} 上，逻辑槽位 {logicalSlotIndexToRefresh} 没有绑定）。");
                            }
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
                Log("[ABM] BindSpellToLogicalSlot：spellData 为空...无法绑定。"); // 保留为关键日志
                return;
            }
            if (logicalSlotIndex < 0 || logicalSlotIndex >= 12) 
            {
                Log($"[ABM] BindSpellToLogicalSlot：logicalSlotIndex {logicalSlotIndex} 超出范围。无法绑定。"); // 保留为关键日志
                return;
            }

            // 获取或创建当前角色的绑定数据
            var currentCharacterBindings = BindingDataManager.GetCurrentCharacterBindings(true); // 使用 BindingDataManager
            if (currentCharacterBindings == null)
            {
                Log("[ABM BindSpellToLogicalSlot：无法获取或创建角色绑定。可能是因为没有选中角色。绑定失败。");
                return;
            }

            if (!currentCharacterBindings.ContainsKey(spellLevel))
            {
                currentCharacterBindings[spellLevel] = new Dictionary<int, Tuple<string, int, int, int, int>>();
            }
            int metamagicMask = (spellData.MetamagicData != null) ? (int)spellData.MetamagicData.MetamagicMask : 0;
            int targetHeightenLevel = 0; // Default to 0 (not heightened or heighten info not applicable)
            int decorationColor = spellData.DecorationColorNumber;
            int decorationBorder = spellData.DecorationBorderNumber;

            if (spellData.MetamagicData != null)
            {
                // If Metamagic.Heighten is applied, store the specific level it's heightened to.
                if ((spellData.MetamagicData.MetamagicMask & Metamagic.Heighten) == Metamagic.Heighten)
                {
                    targetHeightenLevel = spellData.MetamagicData.HeightenLevel;
                }
            }

            currentCharacterBindings[spellLevel][logicalSlotIndex] = Tuple.Create(spellData.Blueprint.AssetGuidThreadSafe, metamagicMask, targetHeightenLevel, decorationColor, decorationBorder);
            LogDebug($"[ABM] 法术GUID '{spellData.Blueprint.AssetGuidThreadSafe}' (超魔: {metamagicMask}, 升阶目标: {targetHeightenLevel}) ({spellData.Name}) 已绑定到角色 {Game.Instance?.SelectionCharacter?.CurrentSelectedCharacter?.CharacterName} 的快捷施法等级 {spellLevel}，逻辑槽位 {logicalSlotIndex}。");

            LogAllBindingsForLevel(spellLevel, currentCharacterBindings[spellLevel]);

            if (_isQuickCastModeActive && _activeQuickCastPage == spellLevel)
            {
                RefreshMainActionBarForCurrentQuickCastPage();
            }

            // 播放绑定音效
            if (Kingmaker.UI.UISoundController.Instance != null)
            {
                Kingmaker.UI.UISoundController.Instance.Play(Kingmaker.UI.UISoundType.ActionBarSlotClick);
                LogDebug($"[ABM BindSpell] 已为法术 '{spellData.Name}' 播放 ActionBarSlotClick 音效。");
            }
            else
            {
                LogDebug("[ABM BindSpell] UISoundController.Instance 为空，无法为绑定播放音效。");
            }
        }

        private void LogAllBindingsForLevel(int spellLevel, Dictionary<int, Tuple<string, int, int, int, int>> levelBindings)
        {
            if (levelBindings == null || levelBindings.Count == 0)
            {
                LogDebug($"[ABM BindDetails] 等级 {spellLevel} 没有绑定。");
                return;
            }
            LogDebug($"[ABM BindDetails] 等级 {spellLevel} 的当前绑定：");
            foreach (var kvp in levelBindings)
            {
                var spellInfo = kvp.Value;
                AbilityData tempAbility = BindingDataManager.GetBoundAbilityData(Game.Instance?.SelectionCharacter?.CurrentSelectedCharacter, spellInfo.Item1, spellInfo.Item2, spellInfo.Item3, spellInfo.Item4, spellInfo.Item5); // Use the new GetBoundAbilityData
                string spellName = tempAbility?.Name ?? "Unknown Spell (Failed to reconstruct)";
                LogDebug($"  逻辑槽位：{kvp.Key}, 法术GUID：{spellInfo.Item1}, 超魔：{spellInfo.Item2}, 升阶目标：{spellInfo.Item3}, 法术名：{spellName}");
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
                EventBus.RaiseEvent<ILogMessageUIHandler>(h => h.HandleLogMessage("无法激活快捷施法：未选择角色。")); // 用户消息
                return false;
            }

            var spellbook = selectedUnit.Descriptor?.Spellbooks?.FirstOrDefault();
            if (spellbook == null)
            {
                string noSpellbookMessage = $"角色 {selectedUnit.CharacterName} 没有法术书，无法激活快捷施法。";
                EventBus.RaiseEvent<ILogMessageUIHandler>(h => h.HandleLogMessage(noSpellbookMessage)); // 用户消息
                return false;
            }

            if (spellLevel > spellbook.MaxSpellLevel)
            {
                string spellLevelHighMessage = $"角色 {selectedUnit.CharacterName} 最高只能施放 {spellbook.MaxSpellLevel}环法术，无法打开 {spellLevel}环快捷施法。";
                EventBus.RaiseEvent<ILogMessageUIHandler>(h => h.HandleLogMessage(spellLevelHighMessage)); // 用户消息
                return false;
            }

            // Main.Log($"[ABM TryActivate] Mode: {(forSpellBook ? "Spellbook" : "QuickCast")}, Level: {spellLevel}"); // 这个可以移到Main.cs
            LogDebug($"[ABM] 尝试激活快捷施法页面：{spellLevel}");
            var game = Game.Instance;
            var actionBarVM = game?.RootUiContext?.InGameVM?.StaticPartVM?.ActionBarVM;
            var spellsGroupInstance = _getSpellsGroupView?.Invoke();

            if (spellsGroupInstance == null)
            {
                Log("[ABM TryActivate] 错误：spellsGroupInstance (来自 m_SpellsGroup) 为空。"); // 关键错误
                RestoreMainActionBarDisplay(); _activeQuickCastPage = -1; _isQuickCastModeActive = false; return false;
            }
            if (actionBarVM == null)
            {
                Log("[ABM TryActivate] 错误：ActionBarVM 为空。"); // 关键错误
                RestoreMainActionBarDisplay(); _activeQuickCastPage = -1; _isQuickCastModeActive = false; return false;
            }

            var currentUnit = actionBarVM.SelectedUnit.Value;
            if (currentUnit == null)
            {
                Log("[ABM] 未选择单位。"); // 重要状态
                if (_isQuickCastModeActive) TryDeactivateQuickCastMode(true);
                else { _activeQuickCastPage = -1; _isQuickCastModeActive = false; }
                return false;
            }

            // 在激活新页面之前，获取当前角色的绑定数据
            var currentCharacterSpellIdBindings = BindingDataManager.GetCurrentCharacterBindings(false); // 使用 BindingDataManager

            // 对获取到的绑定进行验证和清理
            if (currentCharacterSpellIdBindings != null) 
            {
                LogDebug($"[ABM TryActivate] Validating existing bindings for unit {currentUnit.CharacterName} before activating page {spellLevel}.");
                BindingDataManager.ValidateAndCleanupBindings(currentUnit, currentCharacterSpellIdBindings); // 使用 BindingDataManager
                // 重新获取绑定，因为 ValidateAndCleanupBindings 可能会修改它
                currentCharacterSpellIdBindings = BindingDataManager.GetCurrentCharacterBindings(false); // 使用 BindingDataManager
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
                LogDebug("[ABM TryActivate] 尝试在覆盖快捷施法页面之前保存当前行动栏状态。");
                for (int slotIndexToSave = 0; slotIndexToSave < Math.Min(currentActionBarVMForSaving.Slots.Count, Main.Settings.BindKeysForLogicalSlots.Length); slotIndexToSave++)
                {
                    if (Main.Settings.BindKeysForLogicalSlots[slotIndexToSave] == KeyCode.None)
                    {
                        LogDebug($"[ABM TryActivate] 槽位 {slotIndexToSave} 的绑定键未在Mod设置中配置，QuickCast将不会管理此槽位，不保存其原始内容。");
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
                                Log($"[ABM TryActivate 警告] 槽位 {slotIndexToSave} 在可能的恢复后仍包含QC法术。不保存为原始内容。"); // 警告
                            }
                            else
                            {
                                _mainBarOverriddenSlotsOriginalContent[slotIndexToSave] = slotVM.MechanicActionBarSlot;
                                LogDebug($"[ABM TryActivate] 已保存槽位索引 {slotIndexToSave} 的原始内容：{slotVM.MechanicActionBarSlot?.GetType().Name}");
                            }
                        }
                        else
                        {
                            Log($"[ABM TryActivate] 无法保存槽位索引 {slotIndexToSave} 的原始内容：SlotVM 为空。"); // 警告
                        }
                    }
                    else
                    {
                        Log($"[ABM TryActivate] 无法保存槽位索引 {slotIndexToSave} 的原始内容：索引超出 {currentActionBarVMForSaving.Slots.Count} 个槽位的范围。"); // 警告
                    }
                }
            }
            else
            {
                Log("[ABM TryActivate] 无法保存原始行动栏状态：currentActionBarVMForSaving 为空。"); // 关键
            }

            try
            {
                var actionBarVMToSetLevel = game?.RootUiContext?.InGameVM?.StaticPartVM?.ActionBarVM;

                if (spellsGroupInstance == null) { Log("[ABM TryActivate] 错误：spellsGroupInstance为空(重复检查)"); RestoreMainActionBarDisplay(); _activeQuickCastPage = -1; _isQuickCastModeActive = false; return false; } //关键
                if (actionBarVMToSetLevel == null) { Log("[ABM TryActivate] 错误：actionBarVMToSetLevel为空(重复检查)"); RestoreMainActionBarDisplay(); _activeQuickCastPage = -1; _isQuickCastModeActive = false; return false; } //关键
                
                LogDebug("[ABM TryActivate] 尝试直接调用 m_SpellsGroup.SetVisible(true, true)。");
                spellsGroupInstance.SetVisible(true, true); 
                LogDebug("[ABM TryActivate] 已调用 m_SpellsGroup.SetVisible(true, true)。");

                // 在使法术组可见后，在 ViewModel 中设置法术等级
                // 因为 SetVisible 可能会触发其他组隐藏，并且如果在之前完成，可能会重置法术等级。
                if (game.CurrentMode == GameModeType.Default ||
                    game.CurrentMode == GameModeType.TacticalCombat ||
                    game.CurrentMode == GameModeType.Pause)
                {
                    LogDebug($"[ABM TryActivate] 正在将 CurrentSpellLevel.Value 设置为 {spellLevel}。先前的值：{actionBarVMToSetLevel.CurrentSpellLevel.Value}");
                    actionBarVMToSetLevel.CurrentSpellLevel.Value = spellLevel;
                    LogDebug($"[ABM TryActivate] 法术等级已通过 VM 设置为 {spellLevel}。新值：{actionBarVMToSetLevel.CurrentSpellLevel.Value}");
                }
                else
                {
                    LogDebug($"[ABM TryActivate] 游戏不处于适合设置法术等级的模式 ({game.CurrentMode})。等待Update()中的同步");
                }

                _isQuickCastModeActive = true;
                _activeQuickCastPage = spellLevel;

                RefreshMainActionBarForCurrentQuickCastPage(); // 调用统一的刷新逻辑

                string pageNameDisplay;
                if (_activeQuickCastPage == 0) pageNameDisplay = LocalizationManager.GetString("SpellLevel0_Display");
                else if (_activeQuickCastPage == 10) pageNameDisplay = LocalizationManager.GetString("SpellLevel10_Display");
                else pageNameDisplay = LocalizationManager.GetString("SpellLevelN_DisplayFormat", _activeQuickCastPage);
                
                string message = LocalizationManager.GetString("QuickCastActiveFormat", pageNameDisplay);
                EventBus.RaiseEvent<ILogMessageUIHandler>(h => h.HandleLogMessage(message)); // 用户消息
                LogDebug($"[ABM] 内部：快捷施法页面 {pageNameDisplay} 已成功激活。");

                // 应用法术书大小调整逻辑 (仅当快捷施法激活时)
                if (spellsGroupInstance != null && spellbook != null) 
                {
                    AdaptQuickCastSpellbookUISize(spellsGroupInstance, spellbook, _activeQuickCastPage);

                    // 新增：修改法术书组标题
                    try
                    {
                        if (spellsGroupInstance.m_GroupNameLabel != null)
                        {
                            var groupNameLabel = spellsGroupInstance.m_GroupNameLabel;
                            if (groupNameLabel != null)
                            {
                                if (_originalSpellGroupName == null) // 首次激活时保存原始标题
                                {
                                    _originalSpellGroupName = groupNameLabel.text;
                                }
                                string newTitle = LocalizationManager.GetString("QuickCastSpellbookTitleFormat", pageNameDisplay);
                                groupNameLabel.text = newTitle;
                                LogDebug($"[ABM] Spellbook group title set to: {newTitle}");
                            }
                        }
                    }
                    catch (Exception ex_title)
                    {
                        Log($"[ABM TryActivate] Error setting spellbook group title: {ex_title.Message}");
                    }
                }

                // 播放激活音效
                if (Kingmaker.UI.UISoundController.Instance != null)
                {
                    Kingmaker.UI.UISoundController.Instance.Play(Kingmaker.UI.UISoundType.SpellbookOpen);
                    LogDebug("[ABM TryActivate] 已播放 SpellbookOpen 音效。");
                }
                else
                {
                    LogDebug("[ABM TryActivate] UISoundController.Instance 为空，无法播放激活音效。");
                }

                return true;
            }
            catch (Exception ex)
            {
                Log($"[ABM] TryActivateQuickCastMode 期间出错：{ex.ToString()}"); // 关键错误
                RestoreMainActionBarDisplay(); 
                _activeQuickCastPage = -1; _isQuickCastModeActive = false;
                return false;
            }
        }

        // 新增：调整快捷施法激活时法术书UI大小的方法
        private void AdaptQuickCastSpellbookUISize(ActionBarGroupPCView spellsGroupInstance, Spellbook spellbook, int spellLevel)
        {
            if (spellsGroupInstance == null || spellsGroupInstance.m_SlotsList == null)
            {
                LogDebug("[ABM AdaptSpellbook] spellsGroupInstance or m_SlotsList is null. Cannot adapt spellbook size.");
                return;
            }

            try
            {
                var knownSpells = spellbook.GetKnownSpells(spellLevel);
                int actualSpellCount = knownSpells?.Count ?? 0;
                
                var spellSlotsPCList = spellsGroupInstance.m_SlotsList;
                if (spellSlotsPCList != null)
                {
                    int numberOfRowsNeeded = (actualSpellCount == 0) ? 1 : Mathf.CeilToInt((float)actualSpellCount / 5.0f);
                    int totalSlotsToMakeVisibleInSpellbook = numberOfRowsNeeded * 5;
                    
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
                }
                else
                {
                    Log("[ABM AdaptSpellbook] m_SlotsList was null after direct access."); 
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
            if (spellsGroupInstance == null || spellsGroupInstance.m_SlotsList == null)
            {
                LogDebug("[ABM RestoreSpellbookUI] spellsGroupInstance or m_SlotsList is null. Cannot restore spellbook UI size.");
                return;
            }

            try
            {
                var spellSlotsPCList = spellsGroupInstance.m_SlotsList;
                if (spellSlotsPCList != null)
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
                        LogDebug("[ABM RestoreSpellbookUI] 已尝试恢复法术书UI所有槽位为激活并强制重新布局。");
                    }
                }
                else
                {
                    Log("[ABM RestoreSpellbookUI] m_SlotsList was null after direct access."); 
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
            LogDebug($"[ABM] 尝试停用快捷施法模式。先前是否激活：{_isQuickCastModeActive}，强制：{force}");
            if (!_isQuickCastModeActive && !force)
            {
                LogDebug($"[ABM] 未激活且未强制，因此不执行停用。");
                return;
            }

            RestoreMainActionBarDisplay();

            var spellsGroupInstance = _getSpellsGroupView?.Invoke();

            if (spellsGroupInstance != null)
            {
                RestoreSpellbookUISizeToDefault(spellsGroupInstance);
                try
                {
                    if (spellsGroupInstance.m_GroupNameLabel != null && _originalSpellGroupName != null)
                    {
                        var groupNameLabel = spellsGroupInstance.m_GroupNameLabel;
                        if (groupNameLabel != null && groupNameLabel.text != _originalSpellGroupName) 
                        {
                            groupNameLabel.text = _originalSpellGroupName;
                            LogDebug($"[ABM TryDeactivate] Spellbook group title restored to: {_originalSpellGroupName}");
                            _originalSpellGroupName = null; 
                        }
                    }
                }
                catch (Exception ex_title_restore)
                {
                    Log($"[ABM TryDeactivate] Error restoring spellbook group title: {ex_title_restore.Message}");
                }
            }
            else
            {
                LogDebug("[ABM TryDeactivate] 无法获取 spellsGroupInstance 以恢复UI大小。");
            }

            if (spellsGroupInstance != null)
            {
                try
                {
                    LogDebug("[ABM] 正在对 m_SpellsGroup 调用 SetVisible(false, true)。");
                    spellsGroupInstance.SetVisible(false, true);
                }
                catch (Exception ex)
                {
                    Log($"[ABM] 隐藏 m_SpellsGroup 时出错：{ex.ToString()}"); 
                }
            }
            else
            {
                LogDebug("[ABM] 警告：spellsGroupInstance (m_SpellsGroup) 不可用于隐藏法术书UI部分。");
                var actionBarVM = Game.Instance?.RootUiContext?.InGameVM?.StaticPartVM?.ActionBarVM;
                if (actionBarVM != null)
                {
                    actionBarVM.UpdateGroupState(ActionBarGroupType.Spell, false);
                    LogDebug("[ABM] 回退：直接在 VM 上调用 UpdateGroupState(Spell, false)。");
                }
            }

            bool wasActuallyActive = _isQuickCastModeActive; // 捕获重置前的状态
            _isQuickCastModeActive = false;
            _activeQuickCastPage = -1;

            if (wasActuallyActive || force) { 
                string message = LocalizationManager.GetString("QuickCastReturnedToMain");
                EventBus.RaiseEvent<ILogMessageUIHandler>(h => h.HandleLogMessage(message)); // 用户消息
                
                // 播放停用音效
                if (Kingmaker.UI.UISoundController.Instance != null)
                {
                    Kingmaker.UI.UISoundController.Instance.Play(Kingmaker.UI.UISoundType.SpellbookClose);
                    LogDebug("[ABM TryDeactivate] 已播放 SpellbookClose 音效。");
                }
                else
                {
                    LogDebug("[ABM TryDeactivate] UISoundController.Instance 为空，无法播放停用音效。");
                }
            }
        }

        // 辅助方法：获取当前选中角色的绑定数据字典 (GUIDs)
        // 如果不存在该角色的绑定数据且 createIfMissing 为 true，则为其创建一个新的空字典
        // private Dictionary<int, Dictionary<int, string>> GetCurrentCharacterBindings(bool createIfMissing = false)
        // {
        // ... (original code removed)
        // }
        #endregion

        #region Event Handlers (IGameModeHandler, ISelectionManagerUIHandler)
        // Handler for IGameModeHandler
        public void OnGameModeStart(GameModeType gameMode)
        {
            LogDebug($"[ABM OnGameModeStart] Entering game mode: {gameMode}. QuickCast active: {_isQuickCastModeActive}, Page to restore: {_pageToRestoreAfterDialog}");
            _isStateTransitionInProgress = false; // Reset at the beginning of any mode start

            // Check if the current game mode is one where QuickCast should be temporarily suspended.
            if (gameMode == GameModeType.Dialog || 
                gameMode == GameModeType.FullScreenUi ||
                gameMode == GameModeType.Cutscene ||
                gameMode == GameModeType.CutsceneGlobalMap)
            {
                if (_isQuickCastModeActive)
                {
                    _pageToRestoreAfterDialog = _activeQuickCastPage;
                    LogDebug($"[ABM OnGameModeStart] Mode {gameMode}: QuickCast was active on page {_activeQuickCastPage}. Storing for restoration.");
                    // Deactivation will be handled by Update if _isStateTransitionInProgress is true
                }
                else
                {
                    _pageToRestoreAfterDialog = -1;
                    LogDebug($"[ABM OnGameModeStart] Mode {gameMode}: QuickCast was not active.");
                }
                _isStateTransitionInProgress = true; // Mark that we are in a special UI state
                return; 
            }
            // For other game modes, if a deferred restore was queued, try to execute it.
            // This handles cases where we exit a prohibitive mode and then quickly enter another non-prohibitive one.
            else if (_deferredRestoreQueued && _pageToRestoreDeferred != -1)
            {
                LogDebug($"[ABM OnGameModeStart] Mode {gameMode}: Executing deferred restore to page {_pageToRestoreDeferred}.");
                TryActivateQuickCastMode(_pageToRestoreDeferred, false);
                _deferredRestoreQueued = false;
                _pageToRestoreDeferred = -1;
            }
        }

        public void OnGameModeStop(GameModeType gameMode)
        {
            LogDebug($"[ABM OnGameModeStop] Exiting game mode: {gameMode}. Page to restore: {_pageToRestoreAfterDialog}, Deferred queued: {_deferredRestoreQueued}, Deferred page: {_pageToRestoreDeferred}");

            // When exiting modes where QuickCast was suspended.
            if (gameMode == GameModeType.Dialog || 
                gameMode == GameModeType.FullScreenUi ||
                gameMode == GameModeType.Cutscene ||
                gameMode == GameModeType.CutsceneGlobalMap)
            {
                _isStateTransitionInProgress = false; // Exiting the special UI state

                if (_pageToRestoreAfterDialog != -1)
                {
                    // Instead of deferring, try to restore immediately.
                    // The Update loop's _isStateTransitionInProgress check will no longer block activation.
                    LogDebug($"[ABM OnGameModeStop] Mode {gameMode}: Attempting to restore QuickCast to page {_pageToRestoreAfterDialog}.");
                    TryActivateQuickCastMode(_pageToRestoreAfterDialog, false); 
                }
                else
                {
                    LogDebug($"[ABM OnGameModeStop] Mode {gameMode}: No QuickCast page to restore.");
                }
                _pageToRestoreAfterDialog = -1; // Clear the restore request
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
                    Log("[ABM HandleSwitchSelectionUnitInGroup] QuickCast is active. Deactivating due to character switch."); // 保留，重要
                    TryDeactivateQuickCastMode(true); 
                    _deferredRestoreQueued = false;
                    _pageToRestoreDeferred = -1;
                }
                else
                {
                    LogDebug("[ABM HandleSwitchSelectionUnitInGroup] Character switch detected. QuickCast was not active. No specific QC deactivation needed.");
                }
            }
            finally
            {
                if (!_deferredRestoreQueued) {
                     _isStateTransitionInProgress = false;
                     LogDebug("[ABM HandleSwitchSelectionUnitInGroup] Cleared _isStateTransitionInProgress.");
                }
                else {
                    LogDebug("[ABM HandleSwitchSelectionUnitInGroup] _isStateTransitionInProgress NOT cleared by HandleSwitch; _deferredRestoreQueued is pending for Update().");
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
                    Log($"[ABM Update] Deferred dialog restore for page {_pageToRestoreDeferred} cancelled: Invalid unit selection (Count: {selectedUnits?.Count ?? -1})."); // 保留，重要
                    if (_isQuickCastModeActive) TryDeactivateQuickCastMode(true); 
                }
                else
                {
                    var bindingsToValidate = BindingDataManager.GetCurrentCharacterBindings(false); // 使用 BindingDataManager
                    if (bindingsToValidate != null)
                    {
                        LogDebug($"[ABM Update] Validating bindings for {currentSelectedCharacter.CharacterName} before deferred restore.");
                        BindingDataManager.ValidateAndCleanupBindings(currentSelectedCharacter, bindingsToValidate); // 使用 BindingDataManager
                    }

                    var spellbook = currentSelectedCharacter.Descriptor?.Spellbooks?.FirstOrDefault();
                    if (spellbook == null || _pageToRestoreDeferred > spellbook.MaxSpellLevel)
                    {
                        Log($"[ABM Update] Deferred dialog restore for page {_pageToRestoreDeferred} cancelled: Unit {currentSelectedCharacter.CharacterName} cannot use this spell level."); // 保留，重要
                        if (_isQuickCastModeActive) TryDeactivateQuickCastMode(true); 
                    }
                    else
                    {
                        LogDebug($"[ABM Update] Executing deferred dialog restore for page: {_pageToRestoreDeferred} for unit {currentSelectedCharacter.CharacterName}. This will attempt to activate QuickCast.");
                        TryActivateQuickCastMode(_pageToRestoreDeferred, false);
                    }
                }
                _deferredRestoreQueued = false;
                _pageToRestoreDeferred = -1;
                dialogRestoreRanThisFrame = true;
            }

            if (dialogRestoreRanThisFrame) 
            {
                if (_isQuickCastModeActive) 
                {
                    LogDebug($"[ABM Update] Dialog restore action completed. Performing final focused spellbook level sync to page {_activeQuickCastPage}.");
                    ForceSyncSpellbookDisplayToPage(_activeQuickCastPage); 
                }
                
                if (_isStateTransitionInProgress) 
                {
                    LogDebug($"[ABM Update] Clearing _isStateTransitionInProgress after deferred dialog restore. QC Active: {_isQuickCastModeActive}.");
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
                        LogDebug($"[ABM Update - Idle Sync] Spellbook level mismatch (VM: {actionBarVM.CurrentSpellLevel.Value}, Expected: {_activeQuickCastPage}). Forcing sync via ForceSyncSpellbookDisplayToPage.");
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
                // LogDebug($"[ABM UnbindSpell] QuickCast mode not active or no page selected. Ignoring unbind for slot {logicalSlotIndex}.");
                return;
            }

            if (logicalSlotIndex < 0 || logicalSlotIndex >= 12) 
            {
                Log($"[ABM UnbindSpell] Invalid logical slot index {logicalSlotIndex} for unbinding."); // 关键
                return;
            }

            var currentCharacterBindings = BindingDataManager.GetCurrentCharacterBindings(false); // false: don't create if missing
            if (currentCharacterBindings != null && 
                currentCharacterBindings.TryGetValue(_activeQuickCastPage, out var bindingsForLevel))
            {
                if (bindingsForLevel.ContainsKey(logicalSlotIndex))
                {
                    Tuple<string, int, int, int, int> spellInfoToRemove = bindingsForLevel[logicalSlotIndex];
                    string spellName = BindingDataManager.GetBoundAbilityData(Game.Instance?.SelectionCharacter?.CurrentSelectedCharacter, spellInfoToRemove.Item1, spellInfoToRemove.Item2, spellInfoToRemove.Item3, spellInfoToRemove.Item4, spellInfoToRemove.Item5)?.Name ?? $"Unknown Spell (GUID: {spellInfoToRemove.Item1}, Meta: {spellInfoToRemove.Item2}, Target Heighten Level: {spellInfoToRemove.Item3}, Decoration Color: {spellInfoToRemove.Item4}, Decoration Border: {spellInfoToRemove.Item5})";
                    
                    bindingsForLevel.Remove(logicalSlotIndex);
                    LogDebug($"[ABM UnbindSpell] Spell '{spellName}' unbound from character {Game.Instance?.SelectionCharacter?.CurrentSelectedCharacter?.CharacterName}, QuickCast page {_activeQuickCastPage}, logical slot {logicalSlotIndex} due to UI clear."); // 保留，重要
                    
                    RefreshMainActionBarForCurrentQuickCastPage();
                    // 解绑后保存绑定信息
                    BindingDataManager.SaveBindings(Main.ModEntry); // 使用 BindingDataManager
                }
                // else: LogDebug($"[ABM UnbindSpell] No spell was bound to logical slot {logicalSlotIndex} on page {_activeQuickCastPage}. Nothing to unbind.");
            }
            // else: LogDebug($"[ABM UnbindSpell] No bindings found for character or page {_activeQuickCastPage}. Nothing to unbind for slot {logicalSlotIndex}.");
        }
        #endregion

        #region 新增：强制同步法术书显示环阶
        private void ForceSyncSpellbookDisplayToPage(int targetLevel)
        {
            if (!_isQuickCastModeActive || targetLevel == -1) return;

            var game = Game.Instance;
            var actionBarVM = game?.RootUiContext?.InGameVM?.StaticPartVM?.ActionBarVM;
            var spellsGroupInstance = _getSpellsGroupView?.Invoke();

            if (actionBarVM == null || spellsGroupInstance == null)
            {
                LogDebug($"[ABM ForceSyncSpellbookDisplay] Prerequisite VMs/Fields not available. Cannot sync spellbook to level {targetLevel}.");
                return;
            }

            var currentUnit = actionBarVM.SelectedUnit.Value;
            if (currentUnit == null)
            {
                LogDebug($"[ABM ForceSyncSpellbookDisplay] No unit selected. Cannot sync spellbook.");
                return;
            }
            
            var spellbook = currentUnit.Descriptor?.Spellbooks?.FirstOrDefault();
            if (spellbook == null || targetLevel > spellbook.MaxSpellLevel)
            {
                //This logic needs to be adjusted because spell level can be 0 for cantrips, and 10 for mythic spells, which are valid but can be > MaxSpellLevel for some spellbooks.
                //However, the quick cast page should still be accessible if it's 0 or 10, regardless of the spellbook's max conventional spell level.
                //We will only block if targetLevel is NOT 0 or 10 AND it's greater than MaxSpellLevel.
                if (!(targetLevel == 0 || targetLevel == 10) && (spellbook == null || targetLevel > spellbook.MaxSpellLevel))
                {
                    LogDebug($"[ABM ForceSyncSpellbookDisplay] Unit {currentUnit.CharacterName} cannot use spell level {targetLevel} (Max: {spellbook?.MaxSpellLevel ?? -1}). Aborting sync.");
                return;
                }
            }

            try
            {
                if (spellsGroupInstance != null)
                {
                    if (!spellsGroupInstance.VisibleState)
                    {
                        if (currentUnit.Descriptor?.Spellbooks?.Any() == true) {
                            spellsGroupInstance.SetVisible(true, true);
                            LogDebug($"[ABM ForceSyncSpellbookDisplay] Made m_SpellsGroup visible for level {targetLevel}.");
                        }
                    }

                    if (actionBarVM.CurrentSpellLevel.Value != targetLevel)
                    {
                        LogDebug($"[ABM ForceSyncSpellbookDisplay] Spellbook level mismatch (VM: {actionBarVM.CurrentSpellLevel.Value}, Expected: {targetLevel}). Forcing sync for unit {currentUnit.CharacterName}.");
                        actionBarVM.CurrentSpellLevel.Value = targetLevel;
                    }
                }
                else
                {
                    LogDebug("[ABM ForceSyncSpellbookDisplay] m_SpellsGroup instance is null, cannot sync level.");
                }
            }
            catch (Exception ex)
            {
                Log($"[ABM ForceSyncSpellbookDisplay] Error during spellbook level sync: {ex.ToString()}"); // 关键错误
            }
        }
        #endregion

        // 新增：辅助方法，用于从法术蓝图GUID获取AbilityData - 已移至 BindingDataManager
        // private AbilityData GetAbilityDataFromSpellGuid(UnitEntityData unit, string spellGuid)
        // {
        // ... (original code removed)
        // }

        #region Data Persistence (Save/Load Bindings) - All methods moved to BindingDataManager
        // private const string BindingsFileName = "QuickCastCharacterBindings.json";

        // public static void SaveBindings(UnityModManager.ModEntry modEntry)
        // {
        // ... (original code removed)
        // }

        // public static void LoadBindings(UnityModManager.ModEntry modEntry)
        // {
        // ... (original code removed)
        // }
        #endregion

        #region Binding Validation - Method moved to BindingDataManager
        // private bool ValidateAndCleanupBindings(UnitEntityData unit, Dictionary<int, Dictionary<int, string>> characterSpellIdBindings)
        // {
        // ... (original code removed)
        // }
        #endregion
    }
} 