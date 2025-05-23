using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection; // 为 FieldInfo 添加
using System.Text;
using System.Threading.Tasks;
using UnityEngine; // Input 类需要
using UnityModManagerNet;
using Kingmaker.PubSubSystem; // 为 EventBus 和 ILogMessageUIHandler 添加
using Kingmaker; // 为 Game 添加
using Kingmaker.UI.MVVM._VM.ActionBar; // 为 ActionBarVM, SelectorType 添加
using Kingmaker.UI.MVVM._VM.InGame; // 为 InGameVM, InGameStaticPartVM 添加
using Kingmaker.GameModes; // 为 GameModeType 添加
using Kingmaker.UnitLogic; // 用于访问 Spellbook
using Kingmaker.EntitySystem.Entities; // 为 UnitEntityData 添加
using Kingmaker.UI.MVVM._PCView.ActionBar; // 为 ActionBarPCView 添加
using Kingmaker.UnitLogic.Abilities; // 用于 AbilityData
using Kingmaker.UI.UnitSettings; // 为 MechanicActionBarSlotSpell 添加
using Owlcat.Runtime.UI.Controls.Button; // 为 OwlcatMultiButton 添加
using Kingmaker.UI.MVVM; // 为 ViewBase 添加
using UniRx; // 修正 UniRx 命名空间
using Kingmaker.UI.MVVM._VM.ServiceWindows;
using Kingmaker.UI.FullScreenUITypes;

namespace QuickCast
{
    public static class Main // 根据 UMM 指南设为静态类作为入口点
    {
        #region Mod状态与核心实例
        public static bool IsEnabled { get; private set; } // Mod是否启用
        public static UnityModManager.ModEntry ModEntry { get; private set; } // UMM的Mod入口实例，用于日志等
        public static Settings Settings { get; private set; } // Mod的设置实例
        internal static ActionBarManager _actionBarManager; // 行动栏管理器实例
        #endregion

        #region 游戏对象与反射缓存
        // ActionBarPCView 的缓存实例
        public static ActionBarPCView CachedActionBarPCView => GameUIManager.CachedActionBarPCView;
        // m_SpellsGroup 的缓存 FieldInfo，以避免重复反射
        // private static FieldInfo _spellsGroupFieldInfo => GameUIManager._spellsGroupFieldInfo;
        
        // 用于跟踪法术书UI激活的先前状态，以优化日志输出
        private static bool? _previousSpellbookActiveAndInteractableState => GameUIManager._previousSpellbookActiveAndInteractableState;

        // 以下为用于在法术书界面检测鼠标悬停法术所需的反射字段缓存
        // private static FieldInfo _slotsListFieldInfo; // ActionBarGroupPCView.m_SlotsList - 移至确保缓存中处理或直接访问
        // private static FieldInfo _mainButtonFieldInfo; // ActionBarSlotPCView.m_MainButton -  移至确保缓存中处理或直接访问
        // private static PropertyInfo _viewViewModelPropertyInfo; // ViewBase<T>.ViewModel - 移至确保缓存中处理或直接访问
        // private static FieldInfo _baseSlotPCViewFieldInfo; // ActionBarBaseSlotPCView.m_SlotPCView - 移至确保缓存中处理或直接访问

        // 通过补丁更新的、当前鼠标悬停的法术书槽位VM的引用
        public static ActionBarSlotVM CurrentlyHoveredSpellbookSlotVM => QuickCastPatches.CurrentlyHoveredSpellbookSlotVM;
        #endregion

        #region UMM Mod入口与生命周期回调
        // Mod加载入口点
        public static bool Load(UnityModManager.ModEntry modEntry)
        {
            ModEntry = modEntry; // 保存ModEntry实例
            IsEnabled = true;    // Mod默认启用

            // 1. 首先加载设置
            Settings = UnityModManager.ModSettings.Load<Settings>(modEntry);
            if (Settings == null) 
            {
                Settings = new Settings(); 
                Log("警告：无法加载设置，已创建新实例。");
            }

            // 2. 然后基于已加载的设置（或其他逻辑）初始化本地化管理器
            LocalizationManager.Initialize(ModEntry);

            // 3. 最后初始化依赖本地化的UI组件的标签
            SettingsUIManager.InitializeSettingLabels();

            modEntry.OnUpdate = OnUpdate;     
            modEntry.OnToggle = OnToggle;     
            modEntry.OnGUI = OnGUI;           
            modEntry.OnSaveGUI = OnSaveGUI;   

            var harmony = new Harmony(modEntry.Info.Id);
            harmony.PatchAll(Assembly.GetExecutingAssembly()); 
            LogDebug("已应用Harmony补丁。");

            _actionBarManager = new ActionBarManager(
                ModEntry,
                GameUIManager.GetCachedActionBarPCView, 
                GameUIManager.GetSpellsGroupView
            );
            EventBus.Subscribe(_actionBarManager); // Subscribe ActionBarManager to EventBus
            LogDebug("ActionBarManager 已订阅 EventBus。");

            BindingDataManager.LoadBindings(ModEntry);
            LogDebug("已尝试加载快捷施法角色绑定。");

            Log("QuickCast Mod Load 方法完成。");
            return true; 
        }

        // Mod启用/禁用时的回调
        private static bool OnToggle(UnityModManager.ModEntry modEntry, bool value)
        {
            IsEnabled = value; 
            Log($"QuickCast Mod {(IsEnabled ? "已启用" : "已禁用")}");
            if (!IsEnabled)
            {
                if (_actionBarManager != null)
                {
                    if (_actionBarManager.IsQuickCastModeActive)
                    {
                        _actionBarManager.TryDeactivateQuickCastMode(true);
                    }
                    // Unsubscribe when mod is disabled
                    EventBus.Unsubscribe(_actionBarManager); 
                    LogDebug("ActionBarManager 已从 EventBus 取消订阅 (Mod 禁用).");
                    BindingDataManager.SaveBindings(ModEntry);
                    LogDebug("尝试在Mod禁用时保存绑定信息。");
                }
                if(GameUIManager._previousSpellbookActiveAndInteractableState.HasValue) GameUIManager._previousSpellbookActiveAndInteractableState = null; 
            }
            else
            {
                // Subscribe when mod is enabled, if _actionBarManager exists
                if (_actionBarManager != null)
                {
                    EventBus.Subscribe(_actionBarManager);
                    LogDebug("ActionBarManager 已重新订阅 EventBus (Mod 启用).");
                }
                // Potentially re-initialize or re-cache things if needed upon re-enabling
            }
            return true; 
        }

        // 每帧更新的回调
        private static void OnUpdate(UnityModManager.ModEntry modEntry, float dt)
        {
            // 首先，检查是否应该完全跳过处理（例如，Mod未启用或游戏未完全加载）
            if (!IsEnabled || Game.Instance == null || Game.Instance.RootUiContext == null)
            {
                // 如果游戏实例不存在或UI上下文不存在（可能在非常早期的加载阶段），则不应尝试访问游戏模式
                // 但如果GameUIManager.CachedActionBarPCView存在，且游戏实例不存在了（比如退回主菜单的过程），则可以清除
                if (GameUIManager.GetCachedActionBarPCView() != null && Game.Instance == null) 
                {
                    LogDebug("[Main OnUpdate] Game.Instance is null, clearing CachedActionBarPCView.");
                    GameUIManager.ClearCachedActionBarView(); // 使用新的公共方法
                }
                return; 
            }

            var rootUiContext = Game.Instance.RootUiContext;
            GameModeType currentMode = Game.Instance.CurrentMode;
            FullScreenUIType currentFullScreenUIType = FullScreenUIType.Unknown;

            if (currentMode == GameModeType.FullScreenUi)
            {
                // Try to get current FullScreenUIType. This is a bit of a guess without direct access or a clear public API.
                // Game.Instance.Vendor.IsTrading is a good specific check.
                // For others, we rely on RootUIContext properties or GameModeType.
                if (Game.Instance.Vendor != null && Game.Instance.Vendor.IsTrading)
                {
                    currentFullScreenUIType = FullScreenUIType.Vendor;
                }
                // else if (rootUiContext.CurrentServiceWindow == ServiceWindowsType.CharacterInfo) // Example, if needed
                // {
                //     currentFullScreenUIType = FullScreenUIType.CharacterScreen;
                // }
                // else if (rootUiContext.CurrentServiceWindow == ServiceWindowsType.Spellbook)
                // {
                //     currentFullScreenUIType = FullScreenUIType.SpellBook;
                // }
                // More specific types can be added if there's a reliable way to get them.
            }

            bool shouldSkipQuickCastLogic = false;
            if (rootUiContext.AdditionalBlockEscMenu || 
                rootUiContext.SaveLoadIsShown ||
                rootUiContext.ModalMessageWindowShown ||
                rootUiContext.IsBugReportOpen ||
                (rootUiContext.CurrentServiceWindow != ServiceWindowsType.None && 
                 rootUiContext.CurrentServiceWindow != ServiceWindowsType.Spellbook && 
                 rootUiContext.CurrentServiceWindow != ServiceWindowsType.CharacterInfo) || // Allow Spellbook and CharacterInfo for binding/viewing
                currentMode == GameModeType.Cutscene ||
                currentMode == GameModeType.CutsceneGlobalMap ||
                currentMode == GameModeType.Dialog ||
                currentMode == GameModeType.GlobalMap ||
                currentMode == GameModeType.Kingdom ||
                currentMode == GameModeType.Rest ||
                (currentMode == GameModeType.FullScreenUi && 
                    // We want to allow QC logic if it's CharacterScreen or SpellBook full screen UI
                    // For other FullScreenUI types (like Vendor), skip QC logic.
                    !(currentFullScreenUIType == FullScreenUIType.CharacterScreen || currentFullScreenUIType == FullScreenUIType.SpellBook || currentFullScreenUIType == FullScreenUIType.Unknown) && // If Unknown, it's safer to skip unless explicitly allowed
                    (Game.Instance.Vendor != null && Game.Instance.Vendor.IsTrading) // Double check for vendor specifically
                )
            )
            {
                shouldSkipQuickCastLogic = true;
                // if (currentMode == GameModeType.FullScreenUi) {
                //     LogDebug($"[Main OnUpdate] FullScreenUI detected. Type: {currentFullScreenUIType}. Skipping QC Logic: {shouldSkipQuickCastLogic}");
                // }
            }
            
            // If a mode dictates clearing the cache, and it's not already null, clear it.
            // Also, ensure shouldSkipQuickCastLogic is true in this case.
            if (GameUIManager.ShouldClearCachedViewForGameMode(currentMode))
            {
                if (GameUIManager.GetCachedActionBarPCView() != null)
                {
                    LogDebug($"[Main OnUpdate] Game is in {currentMode} mode (should clear cache). Clearing CachedActionBarPCView.");
                    GameUIManager.ClearCachedActionBarView(); // Use new public method
                }
                shouldSkipQuickCastLogic = true; 
            }

            if (shouldSkipQuickCastLogic)
            {
                // LogDebug($"[Main OnUpdate] Skipping EnsureCachedActionBarView and InputManager.HandleInput due to UI state or game mode. Current Mode: {currentMode}, FullScreenUIType: {currentFullScreenUIType}, ServiceWindow: {rootUiContext.CurrentServiceWindow}");
            }
            else // Only attempt to get/use ActionBarPCView if not in a skipping state
            {
                if (GameUIManager.GetCachedActionBarPCView() == null)
                {
                    // LogDebug($"[Main OnUpdate] Attempting to ensure cached action bar view. Current Mode: {currentMode}");
                    GameUIManager.EnsureCachedActionBarView(); 
                    if (GameUIManager.GetCachedActionBarPCView() == null)
                    {
                        // LogDebug($"[Main OnUpdate] CachedActionBarPCView still null after Ensure. Current Mode: {currentMode}. ActionBarManager.Update will still run.");
                        if (_actionBarManager != null) _actionBarManager.Update(); // Still call ABM update for its internal logic if QC logic is skipped due to no view
                        return; // Return early if view is not available
                    }
                }
                // LogDebug($"[Main OnUpdate] Proceeding with InputManager.HandleInput. Current Mode: {currentMode}");
                InputManager.HandleInput();
            }
            
            if (_actionBarManager != null)
            {
                _actionBarManager.Update();
            }

            ActionBarManager.ClearRecentlyBoundSlotsIfNewFrame();
        }

        // UMM设置界面的绘制回调
        private static void OnGUI(UnityModManager.ModEntry modEntry)
        {
            if (!IsEnabled) return; 
            if (Settings == null) { Log("错误：设置实例为空，无法绘制GUI。"); return; }

            SettingsUIManager.DrawSettingsGUI(modEntry);
        }

        private static void OnSaveGUI(UnityModManager.ModEntry modEntry)
        {
            Settings.Save(modEntry); 
            BindingDataManager.SaveBindings(modEntry); // 保存Mod设置时也保存绑定信息
            LogDebug("已在OnSaveGUI中尝试保存快捷施法角色绑定。");
        }
        #endregion

        #region 日志工具
        public static void Log(string message)
        {
            ModEntry?.Logger.Log(message); 
        }

        public static void LogDebug(string message)
        {
            if (Settings != null && Settings.EnableVerboseLogging)
            {
                ModEntry?.Logger.Log("[DEBUG] " + message);
            }
        }
        #endregion
    }
}
