using UnityEngine;
using UnityModManagerNet;

namespace QuickCast
{
    /// <summary>
    /// 定义Mod的设置项，UMM会自动处理其加载和保存。
    /// </summary>
    public class Settings : UnityModManager.ModSettings, IDrawable
    {
        #region Mod设置字段
        // 绑定键的新设置，每个逻辑槽位一个 (共12个槽位)
        public KeyCode[] BindKeysForLogicalSlots = new KeyCode[12];
        // 页面激活键，对应法术等级 0-10 (共11个等级)
        public KeyCode[] PageActivation_Keys = new KeyCode[11];
        // 返回主快捷栏的按键
        public KeyCode ReturnToMainKey = KeyCode.X;
        // 是否启用双击页面激活键返回主快捷栏的功能
        public bool EnableDoubleTapToReturn = false; // 默认为 false
        // 是否启用施法后自动返回主快捷栏的功能
        public bool AutoReturnAfterCast = false; // 默认为 false
        #endregion

        #region 构造函数与初始化
        // 构造函数，用于初始化设置的默认值
        public Settings()
        {
            // 初始化绑定键数组，默认都为KeyCode.None (未绑定)
            if (BindKeysForLogicalSlots == null || BindKeysForLogicalSlots.Length != 12)
            {
                BindKeysForLogicalSlots = new KeyCode[12];
                for (int i = 0; i < BindKeysForLogicalSlots.Length; i++)
                {
                    BindKeysForLogicalSlots[i] = KeyCode.None;
                }
            }
            // 初始化页面激活键数组的默认值
            if (PageActivation_Keys == null || PageActivation_Keys.Length != 11)
            {
                PageActivation_Keys = new KeyCode[11];
                PageActivation_Keys[0] = KeyCode.BackQuote; // 0环 (戏法)
                PageActivation_Keys[1] = KeyCode.Alpha1;    // 1环
                PageActivation_Keys[2] = KeyCode.Alpha2;    // 2环
                PageActivation_Keys[3] = KeyCode.Alpha3;    // 3环
                PageActivation_Keys[4] = KeyCode.Alpha4;    // 4环
                PageActivation_Keys[5] = KeyCode.Alpha5;    // 5环
                PageActivation_Keys[6] = KeyCode.Alpha6;    // 6环
                PageActivation_Keys[7] = KeyCode.Q;         // 7环
                PageActivation_Keys[8] = KeyCode.W;         // 8环
                PageActivation_Keys[9] = KeyCode.E;         // 9环
                PageActivation_Keys[10] = KeyCode.R;        // 10环 (神话)
            }
            // ReturnToMainKey 已经在声明时初始化为 KeyCode.X
        }
        #endregion

        #region UMM接口实现
        // UMM保存设置时调用的方法
        public override void Save(UnityModManager.ModEntry modEntry)
        {
            UnityModManager.ModSettings.Save(this, modEntry); // 调用UMM的静态Save方法
        }

        // IDrawable接口要求的方法，当设置在UMM界面中改变时可能被调用 (本Mod中未使用其回调特性)
        public void OnChange() { /* 如果设置更改时需要执行特定逻辑，可以在此添加 */ }
        #endregion
    }
} 