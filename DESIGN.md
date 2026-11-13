# Design System — Native Windows PDF Studio (Avalonia UI + Skia)

A **Linear / Figma-inspired** native desktop design system engineered specifically for **Avalonia UI 11.x (Skia-rendered)** targeting **`.NET 10` (`net10.0-windows`)**.

The aesthetic prioritizes visual restraint, crisp `1px` hairline geometry, high-contrast scannability, zero chrome clutter around the PDF canvas, and GPU-composited micro-interactions.

---

## 1. Visual Philosophy & Spatial Hierarchy

1. **Canvas First:** The PDF viewport is the primary workspace. Sidebars, inspectors, and toolbars recede into low-chroma cool slate surfaces so document colors remain accurate and unpolluted.
2. **Hairline Separation Over Heavy Drop Shadows:** Structural panels (left thumbnail rail, right inspector/page tools, top window chrome) are separated by crisp `1px` hairline borders (`BorderBrush`). Elevation shadows are reserved exclusively for floating surfaces (floating bottom/top pill toolbar, command palette, popovers, modal dialogs).
3. **Skia Crispness & Pixel Alignment:** All borders and `1.5px` vector icon strokes are authored for clean rasterization on Windows `100%`, `125%`, `150%`, and `200%` DPI scaling (`UseLayoutRounding="True"`).

---

## 2. Color Tokens

All colors are defined inside `Themes/DesignTokens.axaml` via `ResourceDictionary.ThemeDictionaries` (`Dark` and `Light`), managed dynamically at runtime by `ThemeManager` (`Themes/ThemePreset.cs`), and consumed exclusively via `{DynamicResource ...}` so live theme switching across presets is instantaneous without restarting.

### 2.0 Curated Theme Presets (4 Surface Elevation Layers)

Each preset separates the workspace into 4 distinct elevation layers (`Desk Canvas` → `Sidebar Surface` → `Elevated Toolbar/Tabs` → `Crisp Page Shadow`):

| Preset | Mode | Desk (`CanvasBackgroundBrush`) | Sidebar (`PanelBackgroundBrush`) | Toolbar / Active Tab (`FloatingToolbarBrush`) | Hairline Border (`HairlineBorderBrush`) | Accent (`AccentBrush`) |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| `Studio Light` | Light | `#E5E7EB` | `#F5F6F8` | `#FFFFFF` | `#D1D5DB` | `#2563EB` |
| `Warm Paper` | Light | `#E8E3D9` | `#F4F0E8` | `#FAF8F5` | `#D6CFC2` | `#B45309` |
| `Obsidian` | Dark | `#0D0F12` | `#14171D` | `#1C2028` | `#2A303C` | `#3B82F6` |
| `Midnight Nord` | Dark | `#1E222A` | `#252A34` | `#2E3440` | `#3B4252` | `#88C0D0` |
| `Tokyo Night` | Dark | `#16161E` | `#1A1B26` | `#24283B` | `#2F3549` | `#7AA2F7` |
| `Monokai Dimmed` | Dark | `#19181A` | `#222124` | `#2D2A2E` | `#3A373C` | `#FFD866` |

All presets share a soft ambient drop shadow (`PageDropShadow`: `0 6 24 0 #26000000`) and crisp `1px` `PageBorderBrush` outer outline beneath rendered PDF sheets.

### 2.1 Core Surface & Ink Tokens (Default `Obsidian` / `Studio Light`)

| Semantic Token Key | Role / Usage | Obsidian (Dark) | Studio Light |
| :--- | :--- | :--- | :--- |
| `CanvasBackgroundBrush` | Main PDF viewport backdrop (Desk) | `#0D0F12` | `#E5E7EB` |
| `PanelBackgroundBrush` | Sidebars, thumbnail rail, inspector, window chrome | `#14171D` | `#F5F6F8` |
| `PanelSubtleBackgroundBrush` | Recessed wells, search inputs, segmented track | `#101318` | `#ECEEF2` |
| `FloatingToolbarBrush` | Elevated floating toolbar, active tab, popovers, modals | `#1C2028` | `#FFFFFF` |
| `HairlineBorderBrush` | `1px` structural dividers, panel edges, input borders | `#2A303C` | `#D1D5DB` |
| `FloatingBorderBrush` | Elevated floating panel `1px` crisp edge | `#363E4D` | `#C4C9D4` |
| `PageBorderBrush` | Crisp `1px` outer border around PDF pages | `#2A303C` | `#CBD5E1` |
| `TextPrimaryBrush` | Primary labels, active headings, page content chrome | `#F3F4F6` | `#111827` |
| `TextSecondaryBrush` | Muted labels, metadata, inactive icons, placeholders | `#9CA3AF` | `#4B5563` |
| `TextTertiaryBrush` | Keyboard shortcut hints, disabled text | `#6B7280` | `#6B7280` |
| `AccentBrush` | Primary CTA, active tool highlight, focus ring, selection border | `#3B82F6` | `#2563EB` |
| `AccentHoverBrush` | Hovered primary CTA | `#60A5FA` | `#1D4ED8` |
| `AccentSubtleBrush` | Active sidebar item / selected thumbnail tint | `#2E3B82F6` | `#242563EB` |
| `AccentTextBrush` | Text on solid `AccentBrush` button | `#FFFFFF` | `#FFFFFF` |

### 2.2 Interactive State & Semantic Tokens

| Semantic Token Key | Role / Usage | Dark Mode | Light Mode |
| :--- | :--- | :--- | :--- |
| `ControlHoverBrush` | Ghost button / list row hover fill | `#1AFFFFFF` (`~10%` white) | `#0D111827` (`~5%` ink) |
| `ControlPressedBrush` | Ghost button / list row pressed fill | `#26FFFFFF` (`~15%` white) | `#17111827` (`~9%` ink) |
| `PageSheetShadowColor` | Drop shadow beneath rendered PDF pages on canvas | `#66000000` | `#240F172A` |
| `ModalScrimBrush` | Backdrop overlay behind modal dialogs | `#99090B0E` | `#66111827` |
| `SuccessBrush` | Save complete / valid signature / toast badge | `#10B981` | `#059669` |
| `WarningBrush` | Unsaved changes indicator / encryption warning | `#F59E0B` | `#D97706` |
| `DangerBrush` | Delete page / discard changes destructive action | `#EF4444` | `#DC2626` |
| `DangerSubtleBrush` | Destructive hover fill | `#26EF4444` | `#1ADC2626` |

### 2.3 Avalonia `ThemeDictionaries` Blueprint

```xml
<ResourceDictionary xmlns="https://github.com/avaloniaui"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <ResourceDictionary.ThemeDictionaries>
    <ResourceDictionary x:Key="Dark">
      <SolidColorBrush x:Key="CanvasBackgroundBrush" Color="#0F1115" />
      <SolidColorBrush x:Key="PanelBackgroundBrush" Color="#161920" />
      <SolidColorBrush x:Key="PanelSubtleBackgroundBrush" Color="#12151B" />
      <SolidColorBrush x:Key="FloatingToolbarBrush" Color="#1E222B" />
      <SolidColorBrush x:Key="HairlineBorderBrush" Color="#282D38" />
      <SolidColorBrush x:Key="FloatingBorderBrush" Color="#323846" />
      <SolidColorBrush x:Key="TextPrimaryBrush" Color="#F3F4F6" />
      <SolidColorBrush x:Key="TextSecondaryBrush" Color="#9CA3AF" />
      <SolidColorBrush x:Key="TextTertiaryBrush" Color="#6B7280" />
      <SolidColorBrush x:Key="AccentBrush" Color="#3B82F6" />
      <SolidColorBrush x:Key="AccentHoverBrush" Color="#60A5FA" />
      <SolidColorBrush x:Key="AccentSubtleBrush" Color="#263B82F6" />
      <SolidColorBrush x:Key="AccentTextBrush" Color="#FFFFFF" />
      <SolidColorBrush x:Key="ControlHoverBrush" Color="#14FFFFFF" />
      <SolidColorBrush x:Key="ControlPressedBrush" Color="#21FFFFFF" />
      <SolidColorBrush x:Key="ModalScrimBrush" Color="#99090B0E" />
      <SolidColorBrush x:Key="SuccessBrush" Color="#10B981" />
      <SolidColorBrush x:Key="WarningBrush" Color="#F59E0B" />
      <SolidColorBrush x:Key="DangerBrush" Color="#EF4444" />
      <SolidColorBrush x:Key="DangerSubtleBrush" Color="#26EF4444" />
    </ResourceDictionary>

    <ResourceDictionary x:Key="Light">
      <SolidColorBrush x:Key="CanvasBackgroundBrush" Color="#F3F4F6" />
      <SolidColorBrush x:Key="PanelBackgroundBrush" Color="#FFFFFF" />
      <SolidColorBrush x:Key="PanelSubtleBackgroundBrush" Color="#F8F9FB" />
      <SolidColorBrush x:Key="FloatingToolbarBrush" Color="#FFFFFF" />
      <SolidColorBrush x:Key="HairlineBorderBrush" Color="#E5E7EB" />
      <SolidColorBrush x:Key="FloatingBorderBrush" Color="#D1D5DB" />
      <SolidColorBrush x:Key="TextPrimaryBrush" Color="#111827" />
      <SolidColorBrush x:Key="TextSecondaryBrush" Color="#6B7280" />
      <SolidColorBrush x:Key="TextTertiaryBrush" Color="#9CA3AF" />
      <SolidColorBrush x:Key="AccentBrush" Color="#2563EB" />
      <SolidColorBrush x:Key="AccentHoverBrush" Color="#1D4ED8" />
      <SolidColorBrush x:Key="AccentSubtleBrush" Color="#1A2563EB" />
      <SolidColorBrush x:Key="AccentTextBrush" Color="#FFFFFF" />
      <SolidColorBrush x:Key="ControlHoverBrush" Color="#0D111827" />
      <SolidColorBrush x:Key="ControlPressedBrush" Color="#17111827" />
      <SolidColorBrush x:Key="ModalScrimBrush" Color="#66111827" />
      <SolidColorBrush x:Key="SuccessBrush" Color="#059669" />
      <SolidColorBrush x:Key="WarningBrush" Color="#D97706" />
      <SolidColorBrush x:Key="DangerBrush" Color="#DC2626" />
      <SolidColorBrush x:Key="DangerSubtleBrush" Color="#1ADC2626" />
    </ResourceDictionary>
  </ResourceDictionary.ThemeDictionaries>
</ResourceDictionary>
```

---

## 3. Typography Scale

### 3.1 Font Families
- **UI Sans (`Inter`)**: Bundled via `Avalonia.Fonts.Inter` (`avares://Avalonia.Fonts.Inter/Assets#Inter`) for deterministic rendering across all Windows builds.
- **Technical Mono (`JetBrains Mono`)**: Embedded as a `FontFamily` resource (`avares://PdfStudio/Assets/Fonts#JetBrains Mono`) with OpenType **tabular numerals (`tnum`)** enabled so dynamic numeric readouts never jitter horizontally as digits change.

### 3.2 Type Ramp

| Token / Style Class | Font Family | Size | Weight | Line Height | Tracking | Usage |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| `TypeMicro` | `Inter` | `11px` | `Medium` (500) | `14px` | `+0.02em` | Micro-labels, badge text, keyboard shortcut caps, thumbnail page tags |
| `TypeBody` | `Inter` | `13px` | `Regular` (400) | `18px` | `0` | Standard UI copy, menu items, tree nodes, form inputs, dialog prose |
| `TypeControl` | `Inter` | `13px` | `Medium` (500) | `18px` | `0` | Buttons, tabs, segmented controls, toolbar actions |
| `TypeSectionHeader` | `Inter` | `15px` | `SemiBold` (600) | `20px` | `-0.01em` | Inspector section headers, modal titles, empty-state titles |
| `TypeMonoNumeric` | `JetBrains Mono` | `12px` | `Medium` (500) | `16px` | `0` | Zoom percentages (`125%`), page indicators (`04 / 28`), coordinates (`X: 214.0  Y: 792.5 pt`) |
| `TypeMonoMicro` | `JetBrains Mono` | `11px` | `Regular` (400) | `14px` | `0` | Thumbnail index overlays, PDF object/version metadata |

### 3.3 Tabular Numerals in Avalonia
Avalonia supports OpenType font features via `FontFeatureCollection` (`tnum` = 1) or explicit `TextBlock` / `Style` setters:

```xml
<Style Selector="TextBlock.mono-num">
  <Setter Property="FontFamily" Value="{StaticResource MonoFont}" />
  <Setter Property="FontSize" Value="12" />
  <Setter Property="FontFeatures">
    <FontFeatureCollection>
      <FontFeature Tag="tnum" Value="1" />
    </FontFeatureCollection>
  </Setter>
</Style>
```

---

## 4. Spacing Grid, Geometry & Iconography

### 4.1 Strict `4px` / `8px` Spacing Grid
Every margin, padding, and gap must map to the grid tokens below (`x:Double` / `Thickness` resources):

| Token | Value | Primary Application |
| :--- | :--- | :--- |
| `Space1` | `4px` | Tight icon-to-label gaps, inline badge padding, pill separator gaps |
| `Space2` | `8px` | Standard control spacing, toolbar item gaps, compact list padding |
| `Space3` | `12px` | Panel internal horizontal padding, card padding, button horizontal padding |
| `Space4` | `16px` | Section gaps, modal body padding, floating toolbar bottom offset |
| `Space6` | `24px` | Modal header/footer separation, empty-state block spacing |
| `Space8` | `32px` | Canvas page vertical/horizontal resting gutter |

### 4.2 Corner Radii & Borders
- **Buttons, Inputs, List Items, Thumbnails:** `CornerRadius="6"` (`ControlCornerRadius`)
- **Floating Panels (Floating Toolbar, Popovers, Command Palette, Modals):** `CornerRadius="10"` (`FloatingCornerRadius`)
- **Micro Badges / Keyboard Caps:** `CornerRadius="4"` (`BadgeCornerRadius`)
- **Hairline Borders:** `BorderThickness="1"` using `HairlineBorderBrush` (docked panels) or `FloatingBorderBrush` (floating surfaces).

### 4.3 Elevation Shadows (`BoxShadows`)
- **Docked Panels:** `BoxShadows=""` (none — `1px` hairline only).
- **Floating Toolbar / Popovers (`Dark`):** `0 8 24 0 #66000000, 0 2 6 0 #40000000`
- **Floating Toolbar / Popovers (`Light`):** `0 8 24 0 #1A0F172A, 0 2 6 0 #0F0F172A`
- **Modal Dialogs (`Dark`):** `0 20 48 0 #99000000`
- **Modal Dialogs (`Light`):** `0 20 48 0 #260F172A`

### 4.4 `1.5px` Stroke Vector Icons
- **Grid:** `16x16` or `20x20` viewbox (`Width="16" Height="16"` inside `28x28` or `32x32` hit targets).
- **Stroke Style:** `StrokeThickness="1.5"`, `StrokeLineCap="Round"`, `StrokeLineJoin="Round"`, `Fill="Transparent"` (Lucide-compatible geometry).
- **Color Binding:** `Stroke="{Binding $parent[TemplatedControl].Foreground}"` (or `Path` bound to `TextSecondaryBrush` by default, transitioning to `TextPrimaryBrush` on hover and `AccentBrush` when active).

```xml
<Style Selector="Path.icon-stroke">
  <Setter Property="StrokeThickness" Value="1.5" />
  <Setter Property="StrokeLineCap" Value="Round" />
  <Setter Property="StrokeLineJoin" Value="Round" />
  <Setter Property="Stretch" Value="Uniform" />
  <Setter Property="Width" Value="16" />
  <Setter Property="Height" Value="16" />
</Style>
```

---

## 5. Layout Architecture & Component Specs

### 5.1 Application Shell (Windows Custom Chrome)
- **Title Bar (`36px` height):** Integrated custom client chrome (`ExtendClientAreaToDecorationsHint="True"`, `ExtendClientAreaChromeHints="PreferSystemChrome"` or custom `36px` drag strip + native-style min/max/close caption buttons).
  - Left: App mark + open document filename + dirty dot (`6px` circle in `WarningBrush` when structural edits are unsaved) + page count pill.
  - Center: Quick-search / Command Palette trigger (`Ctrl+K`) styled as a subtle recessed pill (`240px` wide, `24px` tall, `PanelSubtleBackgroundBrush`).
  - Right: Theme toggle, sidebar toggles, Windows caption buttons.
- **Left Sidebar (`220px` default, collapsible to `0px`, resizable `180px–320px`):**
  - Top segmented switch (`Pages` | `Outline` | `Attachments`).
  - Virtualized page thumbnail list (`8px` vertical gap, `6px` selection frame in `AccentBrush`, `TypeMonoMicro` badge in bottom-right corner).
- **Main PDF Canvas (`#0F1115` Dark / `#F3F4F6` Light):**
  - Full-bleed viewport hosting the custom Skia `PdfCanvasControl`.
  - **Floating Bottom Toolbar (`FloatingToolbarBrush`, `CornerRadius="10"`, `FloatingBorderBrush`):**
    - Centered `16px` above the bottom edge of the canvas.
    - Contains: Hand/Select mode toggle, Previous/Next page (`↑`/`↓`), `JetBrains Mono` current/total page input (`04 / 28`), Zoom Out (`-`), `JetBrains Mono` zoom readout/dropdown (`100%`), Zoom In (`+`), Fit Width / Fit Page, Rotate CW/CCW, Structural Edit drawer trigger.
- **Right Inspector Panel (`260px` default, collapsible to `0px`):**
  - Document metadata, page dimensions in `pt`/`mm`/`in` (`JetBrains Mono`), and Structural Operations (Insert Blank, Extract Pages, Merge PDF, Split PDF, Delete Selected Pages, Apply Page Order).

---

## 6. Motion & GPU-Composited Transitions

All UI motion shares a single snappy deceleration curve inspired by Linear/Figma:
- **Duration:** `160ms` (`0:0:0.16`)
- **Easing:** `cubic-bezier(0.16, 1, 0.3, 1)` -> Avalonia `<SplineEasing X1="0.16" Y1="1" X2="0.3" Y2="1" />`
- **GPU Compositing Rule:** Animate **`Opacity`**, **`TranslateTransform.X / Y`**, **`ScaleTransform.ScaleX / ScaleY`**, and **`Background` / `BorderBrush`** (`BrushTransition`). Avoid animating `Margin` or complex `Grid` re-layouts per frame where a `RenderTransform` or `Opacity` composite achieves the same visual effect at 60–144 FPS.

### 6.1 Transition Matrix

| Interaction | Properties Animated | Timing & Curve | Implementation Notes |
| :--- | :--- | :--- | :--- |
| **Hover / Active States** (Buttons, Icon toggles, Thumbnail cards) | `Background`, `BorderBrush`, `Foreground`, `Opacity` | `160ms`, `(0.16, 1, 0.3, 1)` | Declarative `Control.Transitions` with `BrushTransition` and `DoubleTransition` |
| **Left / Right Panel Collapse** | `TranslateTransform.X` + `Opacity` + container `MaxWidth` / clip | `160ms`, `(0.16, 1, 0.3, 1)` | Slide inner content via `RenderTransform` (`-16px` / `+16px`) and fade `Opacity` `1 -> 0` so text doesn't reflow during collapse |
| **Floating Toolbar Auto-Reveal / Mode Switch** | `TranslateTransform.Y` (`+8px -> 0`) + `Opacity` (`0 -> 1`) | `160ms`, `(0.16, 1, 0.3, 1)` | GPU-composited layer on floating pill |
| **Modal Overlays & Command Palette (`Ctrl+K`)** | Scrim `Opacity` (`0 -> 1`); Dialog `ScaleTransform` (`0.96 -> 1.0`) + `TranslateTransform.Y` (`6px -> 0`) + `Opacity` (`0 -> 1`) | `160ms`, `(0.16, 1, 0.3, 1)` | `RenderTransformOrigin="0.5,0.5"` on modal card |

### 6.2 Reusable Avalonia Transition Snippet

```xml
<!-- Shared button / icon toggle transitions -->
<Style Selector="Button.linear-ghost">
  <Setter Property="Background" Value="Transparent" />
  <Setter Property="CornerRadius" Value="6" />
  <Setter Property="Padding" Value="8,4" />
  <Setter Property="Transitions">
    <Transitions>
      <BrushTransition Property="Background" Duration="0:0:0.16">
        <BrushTransition.Easing>
          <SplineEasing X1="0.16" Y1="1" X2="0.3" Y2="1" />
        </BrushTransition.Easing>
      </BrushTransition>
      <BrushTransition Property="BorderBrush" Duration="0:0:0.16">
        <BrushTransition.Easing>
          <SplineEasing X1="0.16" Y1="1" X2="0.3" Y2="1" />
        </BrushTransition.Easing>
      </BrushTransition>
      <TransformOperationsTransition Property="RenderTransform" Duration="0:0:0.16">
        <TransformOperationsTransition.Easing>
          <SplineEasing X1="0.16" Y1="1" X2="0.3" Y2="1" />
        </TransformOperationsTransition.Easing>
      </TransformOperationsTransition>
    </Transitions>
  </Setter>
</Style>

<Style Selector="Button.linear-ghost:pointerover /template/ ContentPresenter">
  <Setter Property="Background" Value="{DynamicResource ControlHoverBrush}" />
</Style>

<Style Selector="Button.linear-ghost:pressed">
  <Setter Property="RenderTransform" Value="scale(0.98)" />
</Style>
```

---

## 7. Accessibility & Keyboard-First Ergonomics
- **Focus Ring:** `2px` outer ring in `AccentBrush` (`40%` alpha) + `1px` crisp `AccentBrush` border, shown only on `:focus-visible`.
- **Contrast Targets:** Primary text (`#F3F4F6` on `#161920` = `15.7:1`; `#111827` on `#FFFFFF` = `17.7:1`) exceeds WCAG AAA. Secondary text (`#9CA3AF` on `#161920` = `6.9:1`; `#6B7280` on `#FFFFFF` = `4.8:1`) meets WCAG AA.
- **Reduced Motion:** Respect Windows `SystemParameters` / OS animation preference by binding a global `MotionScale` (if reduced motion is active, snap durations to `0ms`).

