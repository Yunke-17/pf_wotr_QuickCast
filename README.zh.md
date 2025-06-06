# QuickCast Mod - 法术快速施法系统 (中文说明)

[English](./README.en.md) | [中文](./README.zh.md)

---

## 目录
1. [简介](#1-简介)
2. [核心功能](#2-核心功能)
3. [系统要求与安装](#3-系统要求与安装)
4. [如何使用](#4-如何使用)
    - [4.1 核心概念](#41-核心概念)
    - [4.2 快捷施法页激活](#42-快捷施法页激活)
    - [4.3 法术绑定](#43-法术绑定)
    - [4.4 清除法术绑定](#44-清除法术绑定)
    - [4.5 快速施法](#45-快速施法)
    - [4.6 返回主快捷栏](#46-返回主快捷栏)
    - [4.7 施法后状态管理](#47-施法后状态管理)
5. [自定义与配置](#5-自定义与配置)

---

## 1. 简介
QuickCast Mod 为《开拓者：正义之怒》带来更便捷的施法体验！通过简单的键盘操作，即可按法术环阶快速选择和施放法术，大幅提升战斗效率和流畅感。

## 2. 核心功能
*   **分层快捷施法页**: 为不同环阶法术创建独立的快捷访问层。
*   **动态主行动栏**: 激活快捷页时，主行动栏即时显示对应法术，一目了然。
*   **动态法术书标题**: 激活快捷页时，法术书标题会临时更新以指明当前环阶（例如“快捷施法: 3环”）。
*   **智能法术书界面调整**: 法术书界面会根据当前环阶的法术数量动态调整显示范围，更整洁。
*   **纯键盘操作**: 告别繁琐鼠标点击，专注于战斗节奏。
*   **高度可定制**: 自由配置核心按键，适应您的操作习惯。
*   **清晰状态提示**: 通过游戏内事件日志了解当前激活状态。
*   **内置多语言支持**: 可在Mod设置中切换中/英文界面和提示。

## 3. 系统要求与安装
*   《开拓者：正义之怒》游戏本体。
*   [Unity Mod Manager (UMM)](https://www.nexusmods.com/site/mods/21) 并已正确安装到游戏中。

**安装步骤:**
1.  从本 GitHub 仓库的 "Releases" 页面下载最新的 Mod 压缩包 (例如 `QuickCast.zip`)。
2.  打开 Unity Mod Manager，切换到 "Mods" 标签页。
3.  将下载的 Mod 压缩包 (`QuickCast.zip`) 拖拽到 UMM 窗口中，或使用 "Install Mod" 按钮选择压缩包进行安装。
4.  确保 Mod 已启用 (UMM中显示为绿色对勾)。

*注：如果您需要自行编译此 Mod，项目依赖一个 Publicized 版本的 `Assembly-CSharp.dll`。请参考项目文件或使用 [AssemblyPublicizer](https://github.com/CabbageCrow/AssemblyPublicizer) 自行生成，并放置于项目根目录下的 `publicized_assemblies` 文件夹内。对于普通用户，直接下载 Release 版本即可，无需此操作。*

## 4. 如何使用
本 Mod 的设计非常直观，旨在让您快速上手。

### 4.1 核心概念
*   **快捷施法页**: 每个法术环都有一个独立的“页面”，临时显示在主行动栏。
*   **页面激活键**: `Ctrl + 数字/字母` (可自定义) 用来打开对应环阶的法术页。
*   **施法键**: 您在游戏【按键设置】中为行动栏格子（如“行动栏1”）设置的快捷键。Mod 直接使用这些原生按键。
*   **绑定键**: 在 Mod 设置中自定义的单个按键，用于在【法术书】界面将法术“绑定”到快捷页的某个格子上。
*   **返回键**: `X` 键 (可自定义) 用来关闭快捷页，返回游戏默认行动栏。

### 4.2 快捷施法页激活
*   按下特定组合键 (如 `Ctrl + 1`) 激活1环法术页。主行动栏会立刻显示1环页绑定的法术。
*   **注意使用前先在游戏中打开“选项”——“控制”——找到“行动栏”标题下的附加行动栏1-6并取消绑定，除非您打算自定义激活按键。**
*   此时，若打开法术书，其标题会变为类似“快捷施法: 1环”，且法术书仅显示该环阶法术并调整界面大小。

### 4.3 法术绑定
1.  激活目标环阶的法术页 (如 `Ctrl + 3` 打开3环页)，Mod 会尝试打开法术书并定位到3环。
2.  在法术书界面，鼠标悬停于您想绑定的法术图标上。
3.  按下您在 Mod 设置中为目标格子配置的“绑定键” (例如，按键 '1' 设为绑定到第1格)。
*   法术即被绑定到该快捷页的对应格子。

### 4.4 清除法术绑定
*   **通过拖拽清除**: 当快捷施法页激活时，如果您从主行动栏上将一个由QuickCast显示的法术图标【拖拽移除】（例如拖到屏幕空白处），该法术将从此快捷施法页的对应逻辑槽位解绑。
*   **自动清理**: Mod也会在加载或激活快捷页时，自动校验并移除当前角色已无法使用的法术绑定（例如遗忘的法术）。

### 4.5 快速施法
1.  激活目标环阶的法术页 (如 `Ctrl + 3`)。
2.  按下该法术在主行动栏对应格子上的原生“施法键” (例如，游戏内“行动栏1”的快捷键是数字 '1')。
*   游戏将如同您直接点击行动栏一样施放该法术。

### 4.6 返回主快捷栏
*   按下“返回键” (默认为 `X`)。
*   或 (可配置)：双击按下当前已激活的“页面激活键”。
*   法术书标题和界面将恢复正常。

### 4.7 施法后状态管理
*   **默认**: 在 Mod 设置中改为施法后自动返回主快捷栏。
*   **可选**: 施法后保持在当前快捷页。

## 5. 自定义与配置
通过 UMM 打开 Mod 设置界面，您可以：
*   **语言选择**: 选择中文或英文界面。
*   自定义所有“页面激活键”。
*   自定义所有“绑定键”。
*   自定义“返回键”。
*   配置施法后行为及其他便利选项。
*   **一键重置**: 将所有Mod设置恢复为默认值。
*   开启/关闭详细日志输出（用于调试）。

--- 
