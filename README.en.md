# QuickCast Mod (English Guide)

[English](./README.en.md) | [中文](./README.zh.md)

---

## Table of Contents
1. [Introduction](#1-introduction)
2. [Core Features](#2-core-features)
3. [System Requirements & Installation](#3-system-requirements--installation)
4. [How to Use](#4-how-to-use)
    - [4.1 Core Concepts](#41-core-concepts)
    - [4.2 Quick Cast Page Activation](#42-quick-cast-page-activation)
    - [4.3 Spell Binding](#43-spell-binding)
    - [4.4 Clearing Spell Bindings](#44-clearing-spell-bindings)
    - [4.5 Quick Casting](#45-quick-casting)
    - [4.6 Return to Main Hotbar](#46-return-to-main-hotbar)
    - [4.7 Post-Cast State Management](#47-post-cast-state-management)
5. [Customization & Configuration](#5-customization--configuration)

---

## 1. Introduction
QuickCast Mod brings a more convenient spellcasting experience to Pathfinder: Wrath of the Righteous! Quickly select and cast spells by spell level with simple keyboard operations, significantly boosting combat efficiency and fluidity.

## 2. Core Features
*   **Tiered Quick Cast Pages**: Independent quick access layers for different spell levels.
*   **Dynamic Main Action Bar**: When a quick page is active, the main action bar instantly displays corresponding spells for clarity.
*   **Dynamic Spellbook Title**: When a Quick Cast Page is active, the spellbook title temporarily updates to indicate the current spell level (e.g., "QuickCast: Level 3").
*   **Smart Spellbook UI Resizing**: The spellbook interface dynamically adjusts its display based on the number of spells for the current level, keeping it tidy.
*   **Keyboard-Only Operation**: Say goodbye to cumbersome mouse clicks and focus on the rhythm of combat.
*   **Highly Customizable**: Freely configure core hotkeys to suit your playstyle.
*   **Clear Status Indicators**: Understand the current activation status via the in-game event log.
*   **Built-in Multi-language Support**: Switch between English and Chinese interface/tooltips in Mod settings.

## 3. System Requirements & Installation
*   Pathfinder: Wrath of the Righteous (base game).
*   [Unity Mod Manager (UMM)](https://www.nexusmods.com/site/mods/21) installed and configured for the game.

**Installation Steps:**
1.  Download the latest Mod archive (e.g., `QuickCast.zip`) from the "Releases" page of this GitHub repository.
2.  Open Unity Mod Manager and navigate to the "Mods" tab.
3.  Drag and drop the downloaded Mod archive (`QuickCast.zip`) into the UMM window, or use the "Install Mod" button to select the archive for installation.
4.  Ensure the Mod is enabled (shows a green checkmark in UMM).

*Note: If you intend to compile this Mod yourself, the project depends on a Publicized version of `Assembly-CSharp.dll`. Please refer to the project files or use [AssemblyPublicizer](https://github.com/CabbageCrow/AssemblyPublicizer) to generate it yourself and place it in the `publicized_assemblies` folder within the project root. For regular users, simply downloading the Release version is sufficient; this step is not needed.*

## 4. How to Use
This Mod is designed to be intuitive and easy to get started with.

### 4.1 Core Concepts
*   **Quick Cast Pages**: Each spell level has an independent "page" temporarily displayed on the main action bar.
*   **Page Activation Keys**: `Ctrl + Number/Letter` (customizable) to open the spell page for the corresponding level.
*   **Cast Keys**: The hotkeys you set in the game's "Keybindings" for action bar slots (e.g., "Action Bar Slot 1"). The Mod uses these native keys directly.
*   **Bind Keys**: Single keys customized in Mod settings, used in the "Spellbook" interface to "bind" a spell to a slot on a quick page.
*   **Return Key**: The `X` key (customizable) to close the quick page and return to the game's default action bar.

### 4.2 Quick Cast Page Activation
*   Press a specific key combination (e.g., `Ctrl + 1`) to activate the Level 1 spell page. The main action bar will immediately show the spells bound to the Level 1 page.
*   **Attention: Before use, go to Options -> Controls in-game. Under the "Action Bar" heading, find and unbind "Additional Action Bar 1-6" unless you plan to customize their hotkeys.**
*   If you open the spellbook at this time, its title will change to something like "QuickCast: Level 1", and the spellbook will only display spells of that level, adjusting its size accordingly.

### 4.3 Spell Binding
1.  Activate the target spell level's page (e.g., `Ctrl + 3` for Level 3). The Mod will attempt to open the spellbook and navigate to Level 3.
2.  In the spellbook interface, hover your mouse over the spell icon you want to bind.
3.  Press the "Bind Key" you configured in Mod settings for the target slot (e.g., key '1' is set to bind to the first slot).
*   The spell is now bound to the corresponding slot on that quick page.

### 4.4 Clearing Spell Bindings
*   **Clear by Dragging**: When a Quick Cast Page is active, if you drag a spell icon (displayed by QuickCast) off the main action bar (e.g., drag it to an empty area of the screen), that spell will be unbound from the corresponding logical slot on the Quick Cast Page.
*   **Automatic Cleanup**: The Mod also automatically validates and removes bindings for spells that the current character can no longer use (e.g., forgotten spells) when loading or activating a Quick Cast Page.

### 4.5 Quick Casting
1.  Activate the target spell level's page (e.g., `Ctrl + 3`).
2.  Press the native "Cast Key" for that spell's slot on the main action bar (e.g., if the game's hotkey for "Action Bar Slot 1" is the number '1').
*   The game will cast the spell as if you clicked it directly on the action bar.

### 4.6 Return to Main Hotbar
*   Press the "Return Key" (defaults to `X`).
*   Or (configurable): Double-press the currently active "Page Activation Key".
*   The spellbook title and interface will return to normal.

### 4.7 Post-Cast State Management
*   **Default**: Change in Mod settings to automatically return to the main hotbar after casting.
*   **Optional**: Remains on the current quick page after casting.

## 5. Customization & Configuration
Open the Mod settings interface via UMM to:
*   **Language Selection**: Choose between English and Chinese interface.
*   Customize all "Page Activation Keys".
*   Customize all "Bind Keys".
*   Customize the "Return Key".
*   Configure post-cast behavior and other convenience options.
*   **Reset to Defaults**: Restore all Mod settings to their original values.
*   Enable/disable verbose logging (for debugging).

--- 
