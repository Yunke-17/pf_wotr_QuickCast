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
        private static FieldInfo _spellsGroupFieldInfo => GameUIManager._spellsGroupFieldInfo;
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

            // 首先初始化 LocalizationManager，这样后续的日志和设置加载可以使用本地化字符串
            LocalizationManager.Initialize(ModEntry);

            Settings = UnityModManager.ModSettings.Load<Settings>(modEntry);
            if (Settings == null) 
            {
                Settings = new Settings(); 
                Log("警告：无法加载设置，已创建新实例。");
            }

            SettingsUIManager.InitializeSettingLabels();

            modEntry.OnUpdate = OnUpdate;     
            modEntry.OnToggle = OnToggle;     
            modEntry.OnGUI = OnGUI;           
            modEntry.OnSaveGUI = OnSaveGUI;   

            var harmony = new Harmony(modEntry.Info.Id);
            harmony.PatchAll(Assembly.GetExecutingAssembly()); 
            Log("已应用Harmony补丁。");

            _actionBarManager = new ActionBarManager(
                ModEntry,
                GameUIManager.GetCachedActionBarPCView, 
                GameUIManager.GetSpellsGroupFieldInfo 
            );
            EventBus.Subscribe(_actionBarManager); // Subscribe ActionBarManager to EventBus
            Log("ActionBarManager 已订阅 EventBus。");

            BindingDataManager.LoadBindings(ModEntry);
            Log("已尝试加载快捷施法角色绑定。");

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
                    Log("ActionBarManager 已从 EventBus 取消订阅 (Mod 禁用).");
                    BindingDataManager.SaveBindings(ModEntry);
                    Log("尝试在Mod禁用时保存绑定信息。");
                }
                if(GameUIManager._previousSpellbookActiveAndInteractableState.HasValue) GameUIManager._previousSpellbookActiveAndInteractableState = null; 
            }
            else
            {
                // Subscribe when mod is enabled, if _actionBarManager exists
                if (_actionBarManager != null)
                {
                    EventBus.Subscribe(_actionBarManager);
                    Log("ActionBarManager 已重新订阅 EventBus (Mod 启用).");
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
                    Log("[Main OnUpdate] Game.Instance 为空 (可能已退回主菜单)，清除 GameUIManager.CachedActionBarPCView。");
                    GameUIManager.CachedActionBarPCView = null; // 直接通过GameUIManager访问和设置
                    GameUIManager._spellsGroupFieldInfo = null; // 同上
                }
                return; 
            }

            // 接下来，检查是否处于明确应该清除缓存的游戏模式
            GameModeType currentMode = Game.Instance.CurrentMode;
            if (GameUIManager.ShouldClearCachedViewForGameMode(currentMode)) // 使用新的辅助方法
            {
                if (GameUIManager.GetCachedActionBarPCView() != null)
                {
                    Log($"[Main OnUpdate] 游戏处于 {currentMode} 模式 (判断为应清除缓存)，清除 GameUIManager.CachedActionBarPCView。");
                    GameUIManager.CachedActionBarPCView = null;  // 直接通过GameUIManager访问和设置
                    GameUIManager._spellsGroupFieldInfo = null; // 同上
                }
                return; // 在这些模式下不执行后续逻辑
            }

            // 检查是否处于不应执行Mod核心逻辑的游戏模式（None）
            if (currentMode == GameModeType.None)
            {
                // GameModeType.None 是一个比较模糊的状态，可能在场景切换的瞬间出现。
                // 通常不建议在此状态下执行太多操作，但也不一定需要清除 ActionBarPCView，
                // 因为可能只是短暂过渡。如果确实需要，可以取消下面的注释。
                // Log("[Main OnUpdate] 游戏处于 GameModeType.None 模式，暂时跳过 QuickCast 逻辑。");
                return; // 如果在None模式下不应执行任何操作
            }
            
            // 到这里，我们应该处于一个可以运行Mod逻辑的游戏模式 (Default, TacticalCombat, Dialog, FullScreenUi (in-game), etc.)
            // FullScreenUi (游戏内全屏，如角色面板) 不应清除 CachedActionBarPCView，
            // 因为关闭后需要它来恢复UI。

            if (GameUIManager.GetCachedActionBarPCView() == null)
            {
                GameUIManager.EnsureCachedActionBarView(); 
                if (GameUIManager.GetCachedActionBarPCView() == null) // 如果尝试获取后仍然为空，则此帧无法继续
                {
                     return;
                }
            }
           
            if (_actionBarManager == null) return;

            _actionBarManager.Update(); // Call ActionBarManager's Update method

            ActionBarManager.ClearRecentlyBoundSlotsIfNewFrame();
            InputManager.HandleInput();
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
            Log("已在OnSaveGUI中尝试保存快捷施法角色绑定。");
        }
        #endregion

        #region 日志工具
        public static void Log(string message)
        {
            ModEntry?.Logger.Log(message); 
        }
        #endregion
    }
}
