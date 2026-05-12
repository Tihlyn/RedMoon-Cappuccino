# GUI / ImGui / `GUI:` library

For windows, widgets, custom drawing, side-menu integration, overlays, and icon handling. Read SKILL.md first.

## What it is

`GUI:` is a Lua binding over Dear ImGui (~v1.6x branch with patches through 2024). The wiki: *"the used function names and syntax are 99% the same, just the arguments might be slightly different due to LUA not supporting references"*.

- Methods use **colon syntax**: `GUI:Begin(...)`, `GUI:Text(...)`, `GUI:Button(...)`.
- Enums and flags use **dotted constants**: `GUI.WindowFlags_NoMove`, `GUI.Col_WindowBg`, `GUI.SetCond_FirstUseEver`.
- Drawing event: every frame Minion fires `Gameloop.Draw`. **All `GUI:*` calls must run inside a `Gameloop.Draw` handler** — calling them from `Gameloop.Update` is a hard error.

Discoverability tip: `ml_gui.showtestwindow = true` opens the full ImGui demo window, which doubles as a live reference for every widget.

## The multi-return idiom

Lua 5.1 has no by-reference args, so every interactive widget returns `(newvalue, changed)`. Windows return `(visible, open)`.

```lua
myset.enable, changed = GUI:Checkbox("Enable", myset.enable)
if changed then SaveSettings() end
```

For widgets returning multiple primitive values (`InputFloat3`, `ColorEdit4`, etc.):

```lua
local r, g, b, a, changed = GUI:ColorEdit4("Tint", s.r, s.g, s.b, s.a)
if changed then s.r, s.g, s.b, s.a = r, g, b, a end
```

### IDs vs labels

Labels also serve as IDs. To create stable identity decoupled from display:

- `"##suffix"` — hidden suffix; label hidden if empty before `##`.
- `"###id"` — full identity; label and ID are independent.
- `GUI:PushID(stringOrInt) ... GUI:PopID()` — stack-based scoping for loops.

Use `"###stable_id"` whenever the label is localised — otherwise translation changes the widget identity and ImGui forgets its state (window size, expansion, etc.).

## Window/container API

| Function | Signature | Returns |
|---|---|---|
| `GUI:Begin` | `(name, open, flags?)` | `visible, open` |
| `GUI:End` | `()` | – |
| `GUI:BeginChild` | `(name, sx, sy, border, flags?)` | – |
| `GUI:EndChild` | `()` | – |
| `GUI:BeginGroup`/`EndGroup` | – | – |
| `GUI:Columns` | `(count, name, border)` | – |
| `GUI:NextColumn` / `GetColumnIndex` / `SetColumnOffset(i,x)` / `SetColumnWidth(i,w)` | – | – |
| `GUI:BeginTabBar`/`EndTabBar` | – | – |
| `GUI:BeginTabItem(label)`/`EndTabItem()` | – | bool open |
| `GUI:BeginTable`/`EndTable`/`TableNextRow`/`TableNextColumn`/`TableSetupColumn(label, flags, w)`/`TableHeadersRow` | – | – |
| `GUI:OpenPopup(id)` / `BeginPopup(id, flags)` / `EndPopup` | – | bool |
| `GUI:BeginPopupModal(name, opened, flags)` | – | `visible, open` |
| `GUI:BeginPopupContextItem/Window/Void` | – | bool |
| `GUI:CloseCurrentPopup` / `IsPopupOpen(id)` | – | – |
| `GUI:BeginMainMenuBar/EndMainMenuBar`, `BeginMenuBar/EndMenuBar`, `BeginMenu/EndMenu`, `MenuItem(label, shortcut, selected, enabled)` | – | bools |
| `GUI:SetNextWindowSize(x,y, SetCondflags)` | – | – |
| `GUI:SetNextWindowPos(x,y, SetCondflags, pivotX, pivotY)` | – | – |
| `GUI:SetNextWindowCollapsed(b, cond)` / `SetNextWindowFocus()` | – | – |
| `GUI:GetWindowPos/Size/Width/Height` / `GetContentRegionAvail` / `GetCursorPos/SetCursorPos` | – | numbers |
| `GUI:GetScreenSize` | – | `x, y` |
| `GUI:IsWindowAppearing/Collapsed/Focused/Hovered` | – | bool |

### Window flags (`GUI.WindowFlags_*`) — combine with `+`

`NoTitleBar`, `NoResize`, `NoMove`, `NoScrollbar`, `NoScrollWithMouse`, `NoCollapse`, `AlwaysAutoResize`, `NoSavedSettings`, `NoInputs`, `MenuBar`, `HorizontalScrollbar`, `NoFocusOnAppearing`, `NoBringToFrontOnFocus`, `ForceVerticalScrollbar`, `ForceHorizontalScrollbar`, `AlwaysUseWindowPadding`.

### Other enums

- `SetCond_Always / Once / FirstUseEver / Appearing` — for the `*NextWindow*` family.
- `InputTextFlags_*`: `CharsDecimal`, `CharsHexadecimal`, `CharsUppercase`, `CharsNoBlank`, `AutoSelectAll`, `EnterReturnsTrue`, `Password`, `ReadOnly`, etc.
- `SelectableFlags_*`, `ComboFlags_*`.
- `TreeNodeFlags_*`: `Selected`, `Framed`, `Leaf`, `DefaultOpen`, `OpenOnDoubleClick`, `CollapsingHeader`, ...
- `FocusedFlags_*`, `HoveredFlags_*`.
- `ColorEditMode_*`: `NoAlpha`, `AlphaBar`, `HDR`, `RGB`, `HSV`, `HEX`, `PickerHueBar`, `PickerHueWheel`, ...
- `Dir_Left/Right/Up/Down`.

## Widget reference

| Widget | Signature | Returns |
|---|---|---|
| `GUI:Button` | `(label, sx?, sy?)` | bool pressed |
| `GUI:SmallButton` / `ArrowButton(label, Dir_*)` / `InvisibleButton(label, sx, sy)` | – | bool |
| `GUI:ColorButton` | `(id, r, g, b, a, flags?, sx?, sy?)` | bool |
| `GUI:ImageButton` | `(id, filepath, sx, sy [, uvs/bg/tint])` | bool |
| `GUI:Image` | `(filepath, sx, sy [, uvs/bg/tint])` | – |
| `GUI:Checkbox` | `(label, value)` | `value, changed` |
| `GUI:CheckboxFlags` | `(label, flags, flags_value)` | `flags, changed` |
| `GUI:RadioButton` | `(label, current_int, val)` | `int, changed` |
| `GUI:Combo` | `(label, current_idx, itemtable, height_in_items?)` | `int, changed` |
| `GUI:BeginCombo`/`EndCombo` | `(label, preview, ComboFlags)` / `()` | bool / – |
| `GUI:ListBox` | `(label, current_idx, itemtable, height_items)` | `int, changed` |
| `GUI:Selectable` | `(label, selected, flags, sx, sy)` | `sel, changed` |
| `GUI:CollapsingHeader` | `(label, flags)` or `(label, p_open, flags)` | bool |
| `GUI:TreeNode` / `TreePush(id)` / `TreePop()` | – | bool / – |
| `GUI:Text` / `TextColored(R,G,B,A,t)` / `TextDisabled(t)` / `TextWrapped(t)` / `LabelText(label, t)` | – | – |
| `GUI:InputText` | `(label, text, flags?)` | `string, changed` |
| `GUI:InputTextMultiline` | `(label, text, sx, sy, flags?)` | `string, changed` |
| `GUI:InputInt`/`Int2/3/4` | `(label, val.., step, step_fast, flags)` | values, changed |
| `GUI:InputFloat`/`Float2/3/4` | `(label, val.., precision, flags)` | values, changed |
| `GUI:DragInt`/`DragFloat`/`DragIntRange2`/`DragFloatRange2` | `(label, val, v_speed, v_min, v_max, fmt[, power])` | values, changed |
| `GUI:SliderInt`/`Int2/3/4` | `(label, val.., v_min, v_max, fmt)` | values, changed |
| `GUI:SliderFloat`/`Float2/3/4` | `(label, val.., v_min, v_max, fmt, power)` | values, changed |
| `GUI:VSliderInt`/`VSliderFloat` | `(label, sx, sy, val, vmin, vmax, fmt[, power])` | val, changed |
| `GUI:SliderAngle` | `(label, v_rad, v_deg_min, v_deg_max)` | val, changed |
| `GUI:ColorEdit3/4` / `ColorPicker3/4` | `(label, R, G, B[, A], flags)` | RGB[A], changed |
| `GUI:ProgressBar` | `(fraction, sx, sy, overlay)` | – |
| `GUI:Keybind` | `(label, virtualKey, width)` | `vk, keyName, changed` |

### Layout/spacing

`GUI:SameLine(local_pos_x, spacing)`, `GUI:NewLine()`, `GUI:Separator()`, `GUI:Spacing()`, `GUI:Dummy(sx,sy)`, `GUI:Indent()/Unindent()`, `GUI:AlignFirstTextHeightToWidgets()`, `GUI:GetTextLineHeight()`, `GUI:GetFrameHeightWithSpacing()`.

### Item-test (post-widget queries)

After drawing a widget, query its state with the `IsItem*` family:

`GUI:IsItemHovered(HoveredFlags)`, `IsItemActive`, `IsItemFocused`, `IsItemClicked(mb=0)`, `IsItemEdited`, `IsItemActivated`, `IsItemDeactivated`, `IsItemDeactivatedAfterEdit`, `IsAnyItemHovered/Active/Focused`, `GetItemRectMin/Max/Size`, `SetItemAllowOverlap`.

### Tooltips

```lua
GUI:Checkbox("Enable", g)
if GUI:IsItemHovered() then
    GUI:SetTooltip(GetString("Enable feature..."))
end
```

For rich tooltips with multiple widgets: `GUI:BeginTooltip() ... GUI:EndTooltip()`.

### Mouse / keyboard

`IsMouseDown/Clicked/DoubleClicked/Released(button [,repeat])`, `IsMouseDragging(b, t)`, `GetMousePos()`, `GetMouseDragDelta(b)`, `ResetMouseDragDelta(b)`, `GetMouseScroll(b)`, `IsMouseHoveringWindow`, `IsKeyDown/Pressed/Released(virtualKey)`. Keys use Win32 VK codes (`17` = CTRL, `16` = SHIFT). Clipboard: `GetClipboardText() / SetClipboardText(s)`.

## Style/theming

```
GUI:PushStyleColor(GUI.Col_*, R, G, B, A)  -- floats 0-1
GUI:PopStyleColor(count)
GUI:PushStyleVar(GUI.StyleVar_*, val [,val2])
GUI:PopStyleVar(count)
GUI:GetStyle().colors[GUI.Col_WindowBg]    -- returns {r,g,b,a}
GUI:PushItemWidth(w)                       -- <0 right-aligns; 0 default
GUI:PushTextWrapPos(x)
GUI:SetGlobalFontSize(scale)               -- per-window: SetWindowFontSize
```

`Col_*` keys: `Text`, `TextDisabled`, `WindowBg`, `ChildWindowBg`, `Border`, `BorderShadow`, `FrameBg`, `FrameBgHovered`, `FrameBgActive`, `TitleBg`, `TitleBgCollapsed`, `TitleBgActive`, `MenuBarBg`, `ScrollbarBg`, `ScrollbarGrab/Hovered/Active`, `CheckMark`, `SliderGrab/Active`, `Button/Hovered/Active`, `Header/Hovered/Active`, `Column/Hovered/Active`, `ResizeGrip/Hovered/Active`, `PlotLines/Hovered`, `PlotHistogram/Hovered`, `TextSelectedBg`, `TooltipBg`, `ModalWindowDarkening`, `DragDropTarget`, `NavHighlight`, `NavWindowingHighlight`.

`StyleVar_*`: `Alpha`, `WindowPadding`, `WindowRounding`, `WindowBorderSize`, `WindowMinSize`, `ChildWindowRounding`, `ChildBorderSize`, `PopupRounding`, `PopupBorderSize`, `FramePadding`, `FrameRounding`, `FrameBorderSize`, `ItemSpacing`, `ItemInnerSpacing`, `IndentSpacing`, `ScrollbarSize`, `ScrollbarRounding`, `GrabMinSize`, `GrabRounding`, `ButtonTextAlign`.

### Colors — float vs U32

ImGui widget color args (`PushStyleColor`, `TextColored`, `ColorEdit/Picker`) take **0–1 floats RGBA as separate args**, NOT a table and NOT `0xAARRGGBB`. Custom-draw helpers (`AddLine`, `AddRectFilled`, etc.) take a packed `U32`. Convert:

```lua
local u32   = GUI:ColorConvertFloat4ToU32(r, g, b, a)
local r,g,b,a = GUI:ColorConvertU32ToFloat4(u32)
```

RGB↔HSV: `ColorConvertRGBtoHSV / HSVtoRGB`.

## Custom drawing (foreground canvas)

```
GUI:AddLine(x1,y1,x2,y2, u32, thickness)
GUI:AddRect / AddRectFilled(x1,y1,x2,y2, u32, rounding, corner_flags)
GUI:AddCircle / AddCircleFilled(cx,cy,r, u32, segments)
GUI:AddTriangleFilled / AddQuadFilled
GUI:AddText(x,y, u32, text)
GUI:AddImage(filepath, x1,y1,x2,y2)
RenderManager:WorldToScreen(pos, fast?)   -- world(x,y,z) -> screen sx,sy
```

For world-space overlays, project with `RenderManager:WorldToScreen(pos, fast?)` — and **prefer `fast=true`** when possible. The wiki says: *"USE THIS ONE IF POSSIBLE, it is A LOT FASTER"*. The non-fast form does precise depth-test culling; the fast form skips that and is acceptable for most overlays.

For richer world-space drawing (filled circles, cones, donuts on the floor with proper depth) use the Argus binding — see `references/argus-aoe.md`. The plain `GUI:Add*` calls are 2D-only.

## Drawing event hookup

```lua
RegisterEventHandler("Gameloop.Draw",   myMod.Draw,   "myMod.Draw")
RegisterEventHandler("Gameloop.Update", myMod.Update, "myMod.Update")
```

## Minion side-menu integration

Use `ml_gui.ui_mgr` to register a header + members (pattern from `ffxiv_init.lua`):

```lua
local ffxiv_mainmenu = {
    header  = { id="FFXIVMINION##MENU_HEADER", expanded=false, name="FFXIVMinion",
                texture = GetStartupPath().."\\GUI\\UI_Textures\\ffxiv_shiny.png" },
    members = {
        { id="FFXIVMINION##MENU_MAINMENU", name="Main Task",
          onClick=function() ffxivminion.GUI.main.open = true end,
          tooltip="Open the Main Task window." },
        { id="FFXIVMINION##MENU_DEV", name="Dev Tools",
          onClick=function() dev.GUI.open = not dev.GUI.open end,
          tooltip="Open the Developer tools." },
    }
}
ml_gui.ui_mgr:AddComponent(ffxiv_mainmenu)

-- Add a member to an existing header:
ml_gui.ui_mgr:AddMember({ id="FFXIVMINION##MENU_Music", name="Music",
    onClick=function() ffxiv_music.GUI.open = not ffxiv_music.GUI.open end },
    "FFXIVMINION##MENU_HEADER")
```

The header `id` doubles as the ImGui ID — keep it stable and unique per addon. Many community plugins add to `FFXIVMINION##MENU_HEADER` rather than creating their own header to avoid menu clutter.

## Persistence loop (GUI ↔ disk)

The official codebase uses `persistence.store(path, table)` / `persistence.load(path)` (see `references/scaffolding.md` for the deep dive on the three persistence layers). The save-on-change pattern:

```lua
ffxiv_radar.Enable3D, changed = GUI:Checkbox("##Enable3D", ffxiv_radar.Enable3D)
if changed then Settings.ffxiv_radar.Enable3D = ffxiv_radar.Enable3D end
if GUI:IsItemHovered() then GUI:SetTooltip("Show 3D radar.") end
```

FFXIVMinion ships convenience helpers in `ffxiv_init.lua` / `ffxiv_helpers.lua`: `GUI_Capture(widgetReturn, "globalName", sideeffectFn?)` wraps a widget call; if `changed`, writes the new value to the named global and into `Settings.FFXIVMINION`. Plus `GUI_Combo`, `GUI_DrawIntMinMax`, `GUI_Set`. Use them when extending built-in tabs to match house style.

## Translucent / full-screen overlay (from `ffxiv_radar.lua`)

```lua
local maxW, maxH = GUI:GetScreenSize()
GUI:SetNextWindowPos(0, 0, GUI.SetCond_Always)
GUI:SetNextWindowSize(maxW, maxH, GUI.SetCond_Always)
local flags = GUI.WindowFlags_NoInputs + GUI.WindowFlags_NoBringToFrontOnFocus
            + GUI.WindowFlags_NoTitleBar + GUI.WindowFlags_NoResize
            + GUI.WindowFlags_NoScrollbar + GUI.WindowFlags_NoCollapse
GUI:Begin("ffxiv_radar 3D Overlay", true, flags)
-- ... AddLine/AddCircleFilled/AddText calls ...
GUI:End()
```

`NoInputs + NoBringToFrontOnFocus` is the click-through combo for HUD-like overlays — the user clicks past the overlay onto the game world below.

Translucent settings window (from `ffxiv.lua`):

```lua
GUI:SetNextWindowSize(350, 300, GUI.SetCond_FirstUseEver)
local winBG = GUI:GetStyle().colors[GUI.Col_WindowBg]
GUI:PushStyleColor(GUI.Col_WindowBg, winBG[1], winBG[2], winBG[3], 0.75)
ffxivminion.GUI.main.visible, ffxivminion.GUI.main.open =
    GUI:Begin(ffxivminion.GUI.main.name, ffxivminion.GUI.main.open)
-- ... contents ...
GUI:End()
GUI:PopStyleColor(1)
```

## Icons / images

`GUI:Image(filepath, sx, sy)` and `GUI:ImageButton(id, filepath, sx, sy, ...)` take a **filesystem path**, not a memory texture handle. Bundle PNG/DDS under your addon's `GUI\UI_Textures\` and reference via `GetStartupPath() .. "\\LuaMods\\<addon>\\GUI\\UI_Textures\\foo.png"` or `ml_global_information.path .. "\\GUI\\UI_Textures\\..."` for built-ins.

Cache loaded textures by reusing the same path string — the underlying loader memoizes by path, so reusing the string across draws is free.

There is **no exposed `GetActionIcon(id)`** returning an ImGui handle; pre-extract PNG copies and reference by file path. **FontAwesome glyphs are not part of the public `GUI:` API.**

## Idiom cookbook

### Toggle from menu

```lua
onClick = function() my.GUI.open = not my.GUI.open end
```

### Tabbed settings

```lua
if GUI:BeginTabBar("##settings_tabs") then
    if GUI:BeginTabItem("General")  then drawGeneral();  GUI:EndTabItem() end
    if GUI:BeginTabItem("Combat")   then drawCombat();   GUI:EndTabItem() end
    if GUI:BeginTabItem("Advanced") then drawAdvanced(); GUI:EndTabItem() end
    GUI:EndTabBar()
end
```

### Confirm modal

```lua
if GUI:Button("Reset...") then GUI:OpenPopup("ConfirmReset") end
if GUI:BeginPopupModal("ConfirmReset", true, GUI.WindowFlags_AlwaysAutoResize) then
    GUI:Text("Reset all settings?")
    if GUI:Button("Yes",80,0) then myset = defaults(); save(); GUI:CloseCurrentPopup() end
    GUI:SameLine()
    if GUI:Button("No",80,0)  then GUI:CloseCurrentPopup() end
    GUI:EndPopup()
end
```

### Right-click context menu

```lua
GUI:Selectable(item.name, sel)
if GUI:BeginPopupContextItem("ctx_"..item.id, 1) then
    if GUI:Selectable("Delete") then table.remove(items, i) end
    GUI:EndPopup()
end
```

### Translation-stable button

```lua
GUI:Button(GetString("Save").."###save_btn")
```

The `###save_btn` makes the widget identity independent of its display label, so translating "Save" → "保存" doesn't reset its state.

## Deprecated / version notes (2018-04-18 changelog)

Removed: `GUI:SetNextWindowPosCenter`, `SetNextWindowContentWidth`, `SetWindowFontScale`, `ColorEditMode()` (was a function — replaced by enum flags), `IsItemHoveredRect`, `IsRootWindowFocused`, `IsAnyWindowHovered`, `IsRootWindowOrAnyChildFocused`, `IsPosHoveringAnyWindow`, `CalcItemRectClosestPoint`. Window flag `WindowFlags_ShowBorders` replaced by `StyleVar_WindowBorderSize`. Style color keys `Col_ComboBg`, `Col_CloseButton*` removed.

There is no separate `ImGui.*` global; the wrapper is consistently `GUI:`.

## Complete settings-window plugin (~100 lines)

A full plugin demonstrating tabs, persistence-on-change, color picker, and a confirm modal:

```lua
-- LuaMods\my_addon\my_addon.lua
my_addon = {
    GUI       = { open = false, visible = true, name = "My Addon" },
    settings  = nil,
    file      = GetLuaModsPath().."\\my_addon\\settings.lua",
    defaults  = {
        enabled = true, modeIdx = 1, threshold = 50,
        tintR = 0.20, tintG = 0.80, tintB = 0.30, tintA = 1.0,
        notes = "",
    },
    modes = { "Off", "Passive", "Aggressive" },
}

function my_addon.Load()
    local data, err = persistence.load(my_addon.file)
    if err or type(data) ~= "table" then data = {} end
    my_addon.settings = {}
    for k, v in pairs(my_addon.defaults) do
        my_addon.settings[k] = (data[k] ~= nil) and data[k] or v
    end
end
function my_addon.Save()
    if not FolderExists(GetLuaModsPath().."\\my_addon") then
        FolderCreate(GetLuaModsPath().."\\my_addon")
    end
    persistence.store(my_addon.file, my_addon.settings)
end

function my_addon.Draw(event, ticks)
    if not my_addon.GUI.open then return end
    local s = my_addon.settings
    GUI:SetNextWindowSize(420, 360, GUI.SetCond_FirstUseEver)
    my_addon.GUI.visible, my_addon.GUI.open =
        GUI:Begin(my_addon.GUI.name.."###my_addon_main", my_addon.GUI.open)
    if my_addon.GUI.visible then
        if GUI:BeginTabBar("##my_addon_tabs") then
            if GUI:BeginTabItem("General") then
                local ch
                s.enabled, ch = GUI:Checkbox("Enabled", s.enabled)
                if ch then my_addon.Save() end
                if GUI:IsItemHovered() then GUI:SetTooltip("Master toggle") end
                GUI:PushItemWidth(180)
                s.modeIdx, ch = GUI:Combo("Mode", s.modeIdx, my_addon.modes)
                if ch then my_addon.Save() end
                s.threshold, ch = GUI:SliderInt("Threshold", s.threshold, 0, 100)
                if ch then my_addon.Save() end
                GUI:PopItemWidth()
                GUI:Separator()
                s.notes, ch = GUI:InputTextMultiline("##notes", s.notes, 380, 80)
                if ch then my_addon.Save() end
                GUI:EndTabItem()
            end
            if GUI:BeginTabItem("Theme") then
                local r, g, b, a, ch = GUI:ColorEdit4("Tint",
                    s.tintR, s.tintG, s.tintB, s.tintA)
                if ch then
                    s.tintR, s.tintG, s.tintB, s.tintA = r, g, b, a
                    my_addon.Save()
                end
                GUI:TextColored(s.tintR, s.tintG, s.tintB, s.tintA, "Preview text")
                GUI:EndTabItem()
            end
            if GUI:BeginTabItem("Actions") then
                if GUI:Button("Reset to defaults", 160, 22) then
                    GUI:OpenPopup("##confirm_reset")
                end
                if GUI:BeginPopupModal("Reset?###confirm_reset", true,
                        GUI.WindowFlags_AlwaysAutoResize) then
                    GUI:Text("Discard all settings?")
                    if GUI:Button("Yes",80,0) then
                        my_addon.settings = deepcopy(my_addon.defaults)
                        my_addon.Save(); GUI:CloseCurrentPopup()
                    end
                    GUI:SameLine()
                    if GUI:Button("No",80,0) then GUI:CloseCurrentPopup() end
                    GUI:EndPopup()
                end
                GUI:EndTabItem()
            end
            GUI:EndTabBar()
        end
    end
    GUI:End()  -- ALWAYS, regardless of visible
end

function my_addon.RegisterMenu()
    ml_gui.ui_mgr:AddComponent({
        header  = { id="MYADDON##MENU_HEADER", expanded=false, name="MyAddon" },
        members = {{
            id="MYADDON##MENU_MAIN", name="Settings",
            onClick=function() my_addon.GUI.open = not my_addon.GUI.open end,
            tooltip="Open MyAddon settings window." }},
    })
end

my_addon.Load()
my_addon.RegisterMenu()
RegisterEventHandler("Gameloop.Draw", my_addon.Draw, "my_addon.Draw")
```

## GUI gotchas

- **Always pair `Begin*` with `End*`,** even when `visible == false`. ImGui state machine breaks otherwise.
- **`Combo` / `ListBox` indices are 1-based Lua** (consistent with the array passed in).
- **`GUI:Image` requires a file path**; cache loaded textures by reusing the same path string.
- **Color args for widgets are 4 separate floats 0–1**; only `Add*` custom drawing takes packed U32.
- **Custom TTF fonts not exposed** — only `SetGlobalFontSize` / `SetWindowFontSize` (scale factor on the bundled font).
- **`GUI:Keybind` is a Minion-specific custom widget** not in upstream ImGui.
- **On script reload, your `Gameloop.Draw` handler must use a unique 3rd-arg name**; same name replaces prior binding.

## Source pointers

- **Wiki — GUI API (canonical, last updated 2024-06-23)**: https://wiki.mmominion.com/doku.php?id=gui_api
- **Wiki — GUI changelog (deprecated functions)**: https://wiki.mmominion.com/doku.php?id=gui_api_changelog
- **Source — `ffxiv_radar.lua`**: full-screen overlay pattern, `RenderManager:WorldToScreen`
- **Source — `ffxiv.lua`**: translucent settings window, tabbed settings layout
- **Source — `ffxiv_init.lua`**: `ml_gui.ui_mgr:AddComponent` patterns, `GUI_Capture` helper
- **Source — `Dev/dev.lua`**: dev-tools window — good reference for table widgets and inspectors
- **Forum thread 16372**: GUI examples
