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

        #region UI状态与按键设置相关字段
        // 用于在UMM界面上设置按键时，追踪当前正在为哪个逻辑槽位设置绑定键
        private static int _currentlySettingBindKeyForSlot = -1; // -1 表示未设置任何键
        private static string[] _logicalSlotLabels; // 用于绑定键UI中的描述性标签

        // 用于在UMM界面上设置按键时，追踪当前正在为哪个法术等级设置页面激活键
        private static int _currentlySettingPageKeyForLevel = -1;
        private static string[] _pageActivationLevelLabels; // 用于页面激活键UI中的描述性标签

        // 用于在UMM界面上设置按键时，追踪是否正在设置返回键
        private static bool _isSettingReturnKey = false;
        #endregion

        #region 双击返回功能相关字段
        private static KeyCode _lastPageActivationKeyPressed = KeyCode.None; // 上次按下的页面激活键
        private static float _lastPageActivationKeyPressTime = 0f; // 上次按下页面激活键的时间
        private const float DoubleTapTimeThreshold = 0.3f; // 双击检测的时间阈值（秒）
        #endregion

        #region 游戏对象与反射缓存
        // ActionBarPCView 的缓存实例
        public static ActionBarPCView CachedActionBarPCView { get; internal set; }
        // m_SpellsGroup 的缓存 FieldInfo，以避免重复反射
        private static FieldInfo _spellsGroupFieldInfo;
        // 用于跟踪法术书UI激活的先前状态，以优化日志输出
        private static bool? _previousSpellbookActiveAndInteractableState = null;

        // 以下为用于在法术书界面检测鼠标悬停法术所需的反射字段缓存
        // private static FieldInfo _slotsListFieldInfo; // ActionBarGroupPCView.m_SlotsList - 移至确保缓存中处理或直接访问
        // private static FieldInfo _mainButtonFieldInfo; // ActionBarSlotPCView.m_MainButton -  移至确保缓存中处理或直接访问
        // private static PropertyInfo _viewViewModelPropertyInfo; // ViewBase<T>.ViewModel - 移至确保缓存中处理或直接访问
        // private static FieldInfo _baseSlotPCViewFieldInfo; // ActionBarBaseSlotPCView.m_SlotPCView - 移至确保缓存中处理或直接访问

        // 通过补丁更新的、当前鼠标悬停的法术书槽位VM的引用
        public static ActionBarSlotVM CurrentlyHoveredSpellbookSlotVM => QuickCastPatches.CurrentlyHoveredSpellbookSlotVM;
        #endregion

        #region 新方法：确保 ActionBarPCView 被缓存
        private static bool EnsureCachedActionBarView()
        {
            if (CachedActionBarPCView == null)
            {
                Log("[Main] CachedActionBarPCView 为空，尝试通过 FindObjectOfType 查找...");
                CachedActionBarPCView = UnityEngine.Object.FindObjectOfType<ActionBarPCView>();
                if (CachedActionBarPCView != null)
                {
                    Log($"[Main] 成功通过 FindObjectOfType 找到并缓存了 ActionBarPCView: {CachedActionBarPCView.gameObject.name}");
                    var currentCharacter = Game.Instance?.SelectionCharacter?.CurrentSelectedCharacter; // Changed here
                    if (currentCharacter != null && _spellsGroupFieldInfo == null) 
                    {
                        try
                        {
                            _spellsGroupFieldInfo = typeof(ActionBarPCView).GetField("m_SpellsGroup", BindingFlags.NonPublic | BindingFlags.Instance);
                            if (_spellsGroupFieldInfo != null)
                            {
                                Log("[Main] 成功获取 m_SpellsGroup 的 FieldInfo。");
                            }
                            else
                            {
                                Log("[Main] 错误：未能获取 m_SpellsGroup 的 FieldInfo。");
                            }
                        }
                        catch (Exception ex)
                        {
                            Log($"[Main] 尝试获取 m_SpellsGroup 的 FieldInfo 时出错: {ex.Message}");
                        }
                    }
                    else if (currentCharacter == null)
                    {
                        Log("[Main] EnsureCachedActionBarView: 当前未选择角色，暂不尝试获取 m_SpellsGroup FieldInfo。");
                    }
                }
                else
                {
                    Log("[Main] 通过 FindObjectOfType 未能找到 ActionBarPCView 实例。UI可能尚未完全加载或不存在于当前场景。");
                    return false;
                }
            }
            return CachedActionBarPCView != null;
        }
        #endregion

        #region 新增：判断游戏模式是否应清除缓存的辅助方法
        private static bool ShouldClearCachedViewForGameMode(GameModeType gameMode)
        {
            // 优先使用 ToString() 进行比较，以防 GameModeType 枚举的直接成员在此处不可用
            // 或未来发生变化。这是更稳健的做法，直到我们能确认枚举的确切定义。
            string modeName = gameMode.ToString();
            return modeName == "MainMenu" || modeName == "LoadingScreen";
        }
        #endregion

        #region UMM Mod入口与生命周期回调
        // Mod加载入口点
        public static bool Load(UnityModManager.ModEntry modEntry)
        {
            ModEntry = modEntry; // 保存ModEntry实例
            IsEnabled = true;    // Mod默认启用

            Settings = UnityModManager.ModSettings.Load<Settings>(modEntry);
            if (Settings == null) 
            {
                Settings = new Settings(); 
                Log("警告：无法加载设置，已创建新实例。");
            }

            InitializeSettingLabels();

            modEntry.OnUpdate = OnUpdate;     
            modEntry.OnToggle = OnToggle;     
            modEntry.OnGUI = OnGUI;           
            modEntry.OnSaveGUI = OnSaveGUI;   

            var harmony = new Harmony(modEntry.Info.Id);
            harmony.PatchAll(Assembly.GetExecutingAssembly()); 
            Log("已应用Harmony补丁。");

            // CacheReflectionInfo(); // 移除：m_SpellsGroupFieldInfo 的获取移至 EnsureCachedActionBarView
                                 // 其他反射字段如果不再需要或改为直接访问（由于publicized DLL），也可以在此移除其缓存调用

            _actionBarManager = new ActionBarManager(
                ModEntry,
                () => CachedActionBarPCView, 
                () => _spellsGroupFieldInfo 
            );
            EventBus.Subscribe(_actionBarManager); // Subscribe ActionBarManager to EventBus
            Log("ActionBarManager 已订阅 EventBus。");

            // Mod加载完成后，尝试加载角色绑定信息
            ActionBarManager.LoadBindings(ModEntry);
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
                    // Mod禁用时保存绑定信息
                    ActionBarManager.SaveBindings(ModEntry);
                    Log("尝试在Mod禁用时保存绑定信息。");
                }
                 _previousSpellbookActiveAndInteractableState = null; 
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
                // 但如果CachedActionBarPCView存在，且游戏实例不存在了（比如退回主菜单的过程），则可以清除
                if (CachedActionBarPCView != null && Game.Instance == null) 
                {
                    Log("[Main OnUpdate] Game.Instance 为空 (可能已退回主菜单)，清除 CachedActionBarPCView。");
                    CachedActionBarPCView = null;
                    _spellsGroupFieldInfo = null;
                }
                return; 
            }

            // 接下来，检查是否处于明确应该清除缓存的游戏模式
            GameModeType currentMode = Game.Instance.CurrentMode;
            if (ShouldClearCachedViewForGameMode(currentMode)) // 使用新的辅助方法
            {
                if (CachedActionBarPCView != null)
                {
                    Log($"[Main OnUpdate] 游戏处于 {currentMode} 模式 (判断为应清除缓存)，清除 CachedActionBarPCView。");
                    CachedActionBarPCView = null;
                    _spellsGroupFieldInfo = null; 
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

            if (CachedActionBarPCView == null)
            {
                EnsureCachedActionBarView(); 
                if (CachedActionBarPCView == null) // 如果尝试获取后仍然为空，则此帧无法继续
                {
                     return;
                }
            }
           
            if (_actionBarManager == null) return;

            _actionBarManager.Update(); // Call ActionBarManager's Update method

            ActionBarManager.ClearRecentlyBoundSlotsIfNewFrame();
            HandleInput();
        }

        // UMM设置界面的绘制回调
        private static void OnGUI(UnityModManager.ModEntry modEntry)
        {
            if (!IsEnabled) return; 
            if (Settings == null) { Log("错误：设置实例为空，无法绘制GUI。"); return; }

            // 在此添加Mod设置界面的说明文字
            GUILayout.Label("欢迎使用 QuickCast Mod 设置!", new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Bold });
            GUILayout.Space(5);
            GUILayout.Label("说明:");
            GUILayout.Label("  - 点击对应条目右侧的\"设置\"按钮后，按下键盘上的任意键来绑定。", GUILayout.ExpandWidth(false));
            GUILayout.Label("  - 在\"请按键...\"状态下，按【Escape】键可以取消当前设置操作。", GUILayout.ExpandWidth(false));
            GUILayout.Label("  - 在\"请按键...\"状态下，点击【鼠标右键】可以将该按键绑定清除 (设置为 None)。", GUILayout.ExpandWidth(false));
            GUILayout.Label("  - \"页面激活键\"需要与键盘上的【Ctrl】键组合使用。", GUILayout.ExpandWidth(false));
            GUILayout.Label("  - 如果出现\"按键冲突!\"提示，请确保在同一配置区域内没有重复的按键设置。", GUILayout.ExpandWidth(false));
            GUILayout.Space(15);

            DrawBindKeySettings();
            GUILayout.Space(10);

            DrawPageActivationKeySettings();
            GUILayout.Space(10);

            DrawReturnKeySettings();
            GUILayout.Space(20);

            DrawBehaviorSettings();
            GUILayout.Space(20);

            DrawResetToDefaultsButton();
            GUILayout.Space(20);

            HandleKeyCaptureForSettings();
        }

        private static void OnSaveGUI(UnityModManager.ModEntry modEntry)
        {
            Settings.Save(modEntry); 
            ActionBarManager.SaveBindings(modEntry); // 保存Mod设置时也保存绑定信息
            Log("已在OnSaveGUI中尝试保存快捷施法角色绑定。");
        }
        #endregion

        #region 初始化与准备工作
        private static void InitializeSettingLabels()
        {
            if (_logicalSlotLabels == null || _logicalSlotLabels.Length != Settings.BindKeysForLogicalSlots.Length)
            {
                _logicalSlotLabels = new string[Settings.BindKeysForLogicalSlots.Length];
                for (int i = 0; i < _logicalSlotLabels.Length; i++)
                {
                    _logicalSlotLabels[i] = $"逻辑槽位 {i + 1} 绑定键:";
                }
            }
            if (_pageActivationLevelLabels == null || _pageActivationLevelLabels.Length != Settings.PageActivation_Keys.Length)
            {
                _pageActivationLevelLabels = new string[Settings.PageActivation_Keys.Length];
                for (int i = 0; i < _pageActivationLevelLabels.Length; i++) 
                {
                    string levelDisplay = i.ToString();
                    if (i == 0) levelDisplay = "0 (戏法)";
                    else if (i == 10) levelDisplay = "10 (神话)";
                    _pageActivationLevelLabels[i] = $"激活 {levelDisplay}环 页 (Ctrl+):";
                }
            }
        }

        // 移除 CacheReflectionInfo()，因为其主要内容已移至 EnsureCachedActionBarView
        // 如果还有其他不依赖 ActionBarPCView 实例的反射缓存，可以保留或单独处理
        /*
        private static void CacheReflectionInfo()
        {
            try
            {
                // _spellsGroupFieldInfo 的获取已移至 EnsureCachedActionBarView

                // _slotsListFieldInfo = AccessTools.Field(typeof(ActionBarGroupPCView), "m_SlotsList");
                // _mainButtonFieldInfo = AccessTools.Field(typeof(ActionBarSlotPCView), "m_MainButton");
                // _viewViewModelPropertyInfo = typeof(ActionBarSlotPCView).BaseType.GetProperty("ViewModel", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                // _baseSlotPCViewFieldInfo = AccessTools.Field(typeof(ActionBarBaseSlotPCView), "m_SlotPCView");

                // if (_slotsListFieldInfo == null) Log("缓存 m_SlotsList FieldInfo 时出错。");
                // if (_mainButtonFieldInfo == null) Log("缓存 m_MainButton FieldInfo 时出错。");
                // if (_viewViewModelPropertyInfo == null) Log("缓存 ViewModel PropertyInfo 时出错（检查 ActionBarSlotPCView 的基类）。");
                // if (_baseSlotPCViewFieldInfo == null) Log("缓存 m_SlotPCView FieldInfo 时出错。");
            }
            catch (Exception ex)
            {
                Log($"缓存反射信息时出错：{ex.ToString()}");
            }
        }
        */
        #endregion

        #region 输入处理 (HandleInput)
        private static void HandleInput()
        {
            if (!IsEnabled || _actionBarManager == null) return;
            // EnsureCachedActionBarView() 已经在 OnUpdate 中调用，或者在具体操作前再次检查

            bool ctrlHeld = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            bool noModifiers = !ctrlHeld && !Input.GetKey(KeyCode.LeftShift) && !Input.GetKey(KeyCode.RightShift) && !Input.GetKey(KeyCode.LeftAlt) && !Input.GetKey(KeyCode.RightAlt);

            if (ctrlHeld)
            {
                ProcessPageActivationKeys();
                return; 
            }

            if (_actionBarManager.IsQuickCastModeActive)
            {
                if (IsSpellbookInterfaceActive() && ProcessBindingKeys())
                {
                    return; 
                }
            }

            if (noModifiers && ProcessReturnKey())
            {
                return; 
            }
        }

        private static void ProcessPageActivationKeys()
        {
            // 在尝试任何操作之前，确保视图已加载并缓存
            if (!EnsureCachedActionBarView() || CachedActionBarPCView == null) 
            {
                Log("[Main ProcessPageActivationKeys] ActionBarPCView 尚未成功缓存，跳过页面激活处理。");
                return;
            }

            for (int spellLevel = 0; spellLevel < Settings.PageActivation_Keys.Length; spellLevel++)
            {
                KeyCode pageKey = Settings.PageActivation_Keys[spellLevel];
                if (pageKey == KeyCode.None) continue;

                if (Input.GetKeyDown(pageKey))
                {
                    Log($"[QC Input SUCCESS] Ctrl + {pageKey} (对应法术等级 {spellLevel}) 已检测到！");
                    if (Settings.EnableDoubleTapToReturn &&
                        _actionBarManager.IsQuickCastModeActive &&
                        _actionBarManager.ActiveQuickCastPage == spellLevel &&
                        pageKey == _lastPageActivationKeyPressed &&
                        (Time.time - _lastPageActivationKeyPressTime) < DoubleTapTimeThreshold)
                    {
                        Log($"[QC Input] 在活动页面键 {pageKey} 上检测到双击。返回主快捷栏。");
                        _actionBarManager.TryDeactivateQuickCastMode();
                        _lastPageActivationKeyPressed = KeyCode.None; 
                    }
                    else 
                    {
                        Log($"[QC Input] 尝试调用 _actionBarManager.TryActivateQuickCastMode({spellLevel}, false)");
                        _actionBarManager.TryActivateQuickCastMode(spellLevel, false);
                        _lastPageActivationKeyPressed = pageKey; 
                        _lastPageActivationKeyPressTime = Time.time;
                    }
                    return; 
                }
            }
        }

        private static bool ProcessBindingKeys()
        {
            // 确保视图已缓存，因为 GetHoveredAbilityInSpellBookUIActions 可能间接依赖它
            if (CachedActionBarPCView == null && !EnsureCachedActionBarView()) {
                 Log("[Main ProcessBindingKeys] ActionBarPCView 尚未成功缓存，跳过绑定处理。");
                return false;
            }

            AbilityData hoveredAbility = GetHoveredAbilityInSpellBookUIActions();
            if (hoveredAbility == null) return false; 

            bool noModifiers = !Input.GetKey(KeyCode.LeftControl) && !Input.GetKey(KeyCode.RightControl) &&
                               !Input.GetKey(KeyCode.LeftShift) && !Input.GetKey(KeyCode.RightShift) &&
                               !Input.GetKey(KeyCode.LeftAlt) && !Input.GetKey(KeyCode.RightAlt);
            if (!noModifiers) return false; 

            for (int i = 0; i < Settings.BindKeysForLogicalSlots.Length; i++)
            {
                KeyCode configuredBindKey = Settings.BindKeysForLogicalSlots[i];
                if (configuredBindKey != KeyCode.None && Input.GetKeyDown(configuredBindKey))
                {
                    Log($"[QC Input] 尝试绑定：配置的绑定键 {configuredBindKey} (用于槽位 {i}) 已按下。悬停：{hoveredAbility.Name}。QC 页面：{_actionBarManager.ActiveQuickCastPage}");
                    _actionBarManager.BindSpellToLogicalSlot(_actionBarManager.ActiveQuickCastPage, i, hoveredAbility);
                    ActionBarManager.SaveBindings(ModEntry); // 绑定后立即保存
                    return true; 
                }
            }
            return false;
        }

        private static bool ProcessReturnKey()
        {
            if (_actionBarManager.IsQuickCastModeActive &&
                Settings.ReturnToMainKey != KeyCode.None &&
                Input.GetKeyDown(Settings.ReturnToMainKey))
            {
                Log($"[QC Input] 返回键 {Settings.ReturnToMainKey} 按下。停用快捷施法模式。");
                _actionBarManager.TryDeactivateQuickCastMode();
                _previousSpellbookActiveAndInteractableState = null; 
                _lastPageActivationKeyPressed = KeyCode.None; 
                return true;
            }
            return false;
        }
        #endregion

        #region UI与游戏状态辅助方法
        internal static bool IsSpellbookInterfaceActive()
        {
            // 确保视图已缓存，因为此方法依赖于 _spellsGroupFieldInfo 和 CachedActionBarPCView
            if (CachedActionBarPCView == null && !EnsureCachedActionBarView()) {
                // Log("[Main IsSpellbookInterfaceActive] ActionBarPCView 尚未成功缓存。"); // 减少重复日志
                return false;
            } 
            if (CachedActionBarPCView == null || _spellsGroupFieldInfo == null) return false;

            try
            {
                var spellsGroupView = _spellsGroupFieldInfo.GetValue(CachedActionBarPCView) as ActionBarGroupPCView;
                bool isActive = spellsGroupView != null && spellsGroupView.gameObject.activeInHierarchy;

                if (_previousSpellbookActiveAndInteractableState == null || _previousSpellbookActiveAndInteractableState.Value != isActive)
                {
                    Log($"[QC IsSpellbookInterfaceActive] 法术书UI是否可见：{isActive}");
                    _previousSpellbookActiveAndInteractableState = isActive;
                }
                return isActive;
            }
            catch (Exception ex)
            {
                Log($"检查 IsSpellbookInterfaceActive 时出错：{ex.Message}");
                _previousSpellbookActiveAndInteractableState = false; 
                return false;
            }
        }

        private static AbilityData GetHoveredAbilityInSpellBookUIActions()
        {
            // 确保视图已缓存
            if (CachedActionBarPCView == null && !EnsureCachedActionBarView()) {
                // Log("[Main GetHoveredAbilityInSpellBookUIActions] ActionBarPCView 尚未成功缓存。"); // 减少重复日志
                return null;
            }

            var actionBarVM = Game.Instance?.RootUiContext?.InGameVM?.StaticPartVM?.ActionBarVM;
            var hoveredVM = CurrentlyHoveredSpellbookSlotVM; 

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
        #endregion

        #region UMM设置界面绘制逻辑 (OnGUI Helpers)
        private static void DrawBindKeySettings()
        {
            GUILayout.Label("绑定键配置 (Bind Key Settings for Logical Slots):", GUILayout.ExpandWidth(false));
            GUILayout.Space(5);
            if (Settings.BindKeysForLogicalSlots != null && _logicalSlotLabels != null)
            {
                for (int i = 0; i < Settings.BindKeysForLogicalSlots.Length; i++)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(_logicalSlotLabels[i], GUILayout.Width(200));
                    string currentKeyDisplay = (_currentlySettingBindKeyForSlot == i) ? "请按键..." : Settings.BindKeysForLogicalSlots[i].ToString();
                    
                    // 冲突检测
                    string conflictWarning = "";
                    if (Settings.BindKeysForLogicalSlots[i] != KeyCode.None)
                    {
                        for (int j = 0; j < Settings.BindKeysForLogicalSlots.Length; j++)
                        {
                            if (i != j && Settings.BindKeysForLogicalSlots[j] == Settings.BindKeysForLogicalSlots[i])
                            {
                                conflictWarning = "  <color=red>按键冲突!</color>";
                                break;
                            }
                        }
                    }
                    GUILayout.Label(currentKeyDisplay + conflictWarning, GUILayout.Width(180)); // 调整宽度以容纳警告

                    if (GUILayout.Button("设置", GUILayout.Width(60)))
                    {
                        _isSettingReturnKey = false;
                        _currentlySettingPageKeyForLevel = -1;
                        _currentlySettingBindKeyForSlot = (_currentlySettingBindKeyForSlot == i) ? -1 : i; 
                    }
                    GUILayout.EndHorizontal();
                }
            }
        }

        private static void DrawPageActivationKeySettings()
        {
            GUILayout.Label("页面激活键配置 (Page Activation Keys - Ctrl + YourKey):", GUILayout.ExpandWidth(false));
            GUILayout.Space(5);
            if (Settings.PageActivation_Keys != null && _pageActivationLevelLabels != null)
            {
                for (int i = 0; i < Settings.PageActivation_Keys.Length; i++)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(_pageActivationLevelLabels[i], GUILayout.Width(200));
                    string currentKeyDisplay = (_currentlySettingPageKeyForLevel == i) ? "请按键..." : Settings.PageActivation_Keys[i].ToString();

                    // 冲突检测
                    string conflictWarning = "";
                    if (Settings.PageActivation_Keys[i] != KeyCode.None)
                    {
                        for (int j = 0; j < Settings.PageActivation_Keys.Length; j++)
                        {
                            if (i != j && Settings.PageActivation_Keys[j] == Settings.PageActivation_Keys[i])
                            {
                                conflictWarning = "  <color=red>按键冲突!</color>";
                                break;
                            }
                        }
                    }
                    GUILayout.Label(currentKeyDisplay + conflictWarning, GUILayout.Width(180)); // 调整宽度以容纳警告

                    if (GUILayout.Button("设置", GUILayout.Width(60)))
                    {
                        _isSettingReturnKey = false;
                        _currentlySettingBindKeyForSlot = -1;
                        _currentlySettingPageKeyForLevel = (_currentlySettingPageKeyForLevel == i) ? -1 : i;
                    }
                    GUILayout.EndHorizontal();
                }
            }
        }

        private static void DrawReturnKeySettings()
        {
            GUILayout.Label("返回主快捷栏键配置 (Return to Main Hotbar Key):", GUILayout.ExpandWidth(false));
            GUILayout.Space(5);
            GUILayout.BeginHorizontal();
            GUILayout.Label("返回键:", GUILayout.Width(200));
            string returnKeyDisplay = _isSettingReturnKey ? "请按键..." : Settings.ReturnToMainKey.ToString();
            GUILayout.Label(returnKeyDisplay, GUILayout.Width(120));
            if (GUILayout.Button("设置", GUILayout.Width(60)))
            {
                _currentlySettingBindKeyForSlot = -1;
                _currentlySettingPageKeyForLevel = -1;
                _isSettingReturnKey = !_isSettingReturnKey;
            }
            GUILayout.EndHorizontal();
        }
        
        private static void DrawBehaviorSettings()
        {
            GUILayout.Label("行为设置 (Behavior Settings):", GUILayout.ExpandWidth(false));
            GUILayout.Space(5);
            Settings.EnableDoubleTapToReturn = GUILayout.Toggle(Settings.EnableDoubleTapToReturn, "允许双击页面激活键返回主快捷栏");
            Settings.AutoReturnAfterCast = GUILayout.Toggle(Settings.AutoReturnAfterCast, "施法启动后自动返回主快捷栏");
        }

        // 新增方法：绘制重置为默认值按钮
        private static void DrawResetToDefaultsButton()
        {
            GUILayout.Label("恢复默认设置 (Restore Defaults):", GUILayout.ExpandWidth(false));
            GUILayout.Space(5);
            if (GUILayout.Button("将所有设置重置为推荐默认值", GUILayout.ExpandWidth(true)))
            {
                if (Settings != null)
                {
                    Log("[Main OnGUI] 用户点击了重置为推荐默认值按钮。");
                    Settings temporaryDefaultSettings = new Settings(); // 这会调用构造函数并填充默认值

                    // 复制绑定键
                    if (temporaryDefaultSettings.BindKeysForLogicalSlots != null && Settings.BindKeysForLogicalSlots.Length == temporaryDefaultSettings.BindKeysForLogicalSlots.Length)
                    {
                        Array.Copy(temporaryDefaultSettings.BindKeysForLogicalSlots, Settings.BindKeysForLogicalSlots, temporaryDefaultSettings.BindKeysForLogicalSlots.Length);
                    }

                    // 复制页面激活键
                    if (temporaryDefaultSettings.PageActivation_Keys != null && Settings.PageActivation_Keys.Length == temporaryDefaultSettings.PageActivation_Keys.Length)
                    {
                        Array.Copy(temporaryDefaultSettings.PageActivation_Keys, Settings.PageActivation_Keys, temporaryDefaultSettings.PageActivation_Keys.Length);
                    }

                    // 复制其他设置
                    Settings.ReturnToMainKey = temporaryDefaultSettings.ReturnToMainKey;
                    Settings.EnableDoubleTapToReturn = temporaryDefaultSettings.EnableDoubleTapToReturn;
                    Settings.AutoReturnAfterCast = temporaryDefaultSettings.AutoReturnAfterCast;

                    Log("[Main OnGUI] 设置已重置为推荐默认值。");
                    // ModEntry.ModSettings.Save(ModEntry); // 可选：立即保存，或者让用户手动保存
                }
                else
                {
                    Log("[Main OnGUI] 错误：尝试重置设置时，Settings 实例为空。");
                }
            }
        }

        private static void HandleKeyCaptureForSettings()
        {
            if (!(_currentlySettingBindKeyForSlot != -1 || _currentlySettingPageKeyForLevel != -1 || _isSettingReturnKey))
            {
                return; 
            }

            string settingWhichKeyLabel = "";
            if (_currentlySettingBindKeyForSlot != -1) settingWhichKeyLabel = _logicalSlotLabels[_currentlySettingBindKeyForSlot];
            else if (_currentlySettingPageKeyForLevel != -1) settingWhichKeyLabel = _pageActivationLevelLabels[_currentlySettingPageKeyForLevel];
            else if (_isSettingReturnKey) settingWhichKeyLabel = "返回键";

            GUILayout.Label($"正在为 [{settingWhichKeyLabel.TrimEnd(':')}] 设置按键。按Esc取消。", GUILayout.ExpandWidth(true));

            Event e = Event.current; 
            if (e.isKey && e.type == EventType.KeyDown) 
            {
                if (e.keyCode == KeyCode.Escape) 
                {
                    Log($"为 [{settingWhichKeyLabel.TrimEnd(':')}] 设置按键已通过 Escape 取消。");
                    _currentlySettingBindKeyForSlot = -1;
                    _currentlySettingPageKeyForLevel = -1;
                    _isSettingReturnKey = false;
                }
                else if (e.keyCode != KeyCode.None) 
                {
                    string logMessage = $"按键 {e.keyCode} 已设置给";
                    if (_currentlySettingBindKeyForSlot != -1)
                    {
                        Settings.BindKeysForLogicalSlots[_currentlySettingBindKeyForSlot] = e.keyCode;
                        logMessage += $" {_logicalSlotLabels[_currentlySettingBindKeyForSlot].TrimEnd(':')}";
                        _currentlySettingBindKeyForSlot = -1; 
                    }
                    else if (_currentlySettingPageKeyForLevel != -1)
                    {
                        Settings.PageActivation_Keys[_currentlySettingPageKeyForLevel] = e.keyCode;
                        logMessage += $" {_pageActivationLevelLabels[_currentlySettingPageKeyForLevel].TrimEnd(':')}";
                        _currentlySettingPageKeyForLevel = -1; 
                    }
                    else if (_isSettingReturnKey)
                    {
                        Settings.ReturnToMainKey = e.keyCode;
                        logMessage += " 返回键";
                        _isSettingReturnKey = false; 
                    }
                    Log(logMessage);
                }
                e.Use(); 
            }
            // 新增：处理鼠标右键点击以取消按键设置
            else if (e.isMouse && e.type == EventType.MouseDown && e.button == 1) // e.button == 1 是右键点击
            {
                string logMessage = $"通过右键点击取消了为 [{settingWhichKeyLabel.TrimEnd(':')}] 设置按键。";
                bool cancelled = false;
                if (_currentlySettingBindKeyForSlot != -1)
                {
                    Settings.BindKeysForLogicalSlots[_currentlySettingBindKeyForSlot] = KeyCode.None;
                    _currentlySettingBindKeyForSlot = -1;
                    cancelled = true;
                }
                else if (_currentlySettingPageKeyForLevel != -1)
                {
                    Settings.PageActivation_Keys[_currentlySettingPageKeyForLevel] = KeyCode.None;
                    _currentlySettingPageKeyForLevel = -1;
                    cancelled = true;
                }
                else if (_isSettingReturnKey)
                {
                    Settings.ReturnToMainKey = KeyCode.None; // 允许将返回键也清空
                    _isSettingReturnKey = false;
                    cancelled = true;
                }

                if (cancelled)
                {
                    Log(logMessage);
                    e.Use();
                }
            }
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
