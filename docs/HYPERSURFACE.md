# HyperSurface design record

Status: design draft; not an executable Surface API version

HyperSurface is Terminal's proposed portable UI architecture. It preserves a
semantic description of an interface independently of PowerShell, Android,
Windows, terminal cells, and GPU implementation details.

The durable product is the contract:

```text
semantic graph
    +
projection profile
    +
trusted control recipes
    +
theme tokens
    =
render tree
```

A renderer is replaceable. A valid semantic document must remain meaningful
when no particular renderer exists.

## 1. Architectural lineage

HyperSurface deliberately combines a small set of proven ideas:

- HTML/XHTML contributes readable hierarchical structure.
- UIML contributes separation between abstract interface structure and
  platform presentation vocabularies.
- Win32 menus contribute a battle-tested command and state algebra.
- PowerShell contributes behavior, object binding, discovery, and composition.
- CSS contributes centralized design authority, but not selectors, cascading,
  specificity, arbitrary declarations, or executable expressions.
- Android Views/Canvas and Windows Direct2D/DirectWrite provide native
  projections.
- Shader and terminal surfaces remain typed children rather than alternate
  application frameworks.

HyperSurface is not HTML, CSS, XAML, UIML, or a browser engine. It is a small
semantic UI ABI that may use XML as one serialization.

## 2. Hard boundaries

1. Markup declares structure and initial/default semantic state only. Live
   authoritative state may subsequently come from the semantic graph and its
   admitted binding source; markup defaults and live state never compete for
   authority.
2. Markup contains no executable expressions or event-handler source.
3. PowerShell owns application behavior and consumes serialized semantic
   events.
4. C# owns parsing, validation, graph state, profile compilation, binding
   machinery (observation and propagation), dispatch, and renderer-neutral
   layout policy. PowerShell owns application binding semantics and source
   values.
5. A platform adapter owns native lifecycle, input, accessibility, IME, and
   documented platform capabilities.
6. Renderers own pixels and graphics resources, not application authority.
7. Android classes, Win32 handles, native pointers, renderer objects, and GPU
   handles never enter the semantic graph or PowerShell API.
8. Unknown or unsupported nodes fail locally and diagnostically. They do not
   silently acquire platform behavior.

## 3. The four-stage model

### 3.1 Semantic graph

The semantic graph records what exists and what it means. It contains stable
node identifiers, semantic roles, values, state, commands, and hierarchy. It
does not contain coordinates, platform types, animations, gradients, or
renderer callbacks.

### 3.2 Projection Plan

The Projection Plan is the compiled representation of how a semantic graph is
exposed in a particular interaction context. A profile may be authored in a
small declarative form, but its compiled result is a plan rather than an
application syntax tree. A plan may project the same subtree simultaneously on
front, right, and overlay planes. It owns navigation stacks, inline expansion,
drill-down, cascades, breadcrumbs, responsive placement, and transitions.

Projection state is private to a projection instance:

- expanded nodes
- navigation stack
- focused and hovered node
- selection
- scroll offsets
- active pointer capture
- search query

It is not written into the semantic graph. Two projections may therefore show
the same graph without fighting over presentation state.

### 3.3 Control recipes

Recipes are trusted renderer-owned definitions of invariant component
construction. A `NavigationCard` recipe always supplies its icon slot, title,
optional caption, trailing affordance, minimum touch target, focus semantics,
and accessibility role.

Application authors select semantic roles. They do not reconstruct recipes.

### 3.4 Theme tokens

Themes provide validated values consumed by recipes. Resolution is explicit:

```text
built-in defaults
    -> selected theme
    -> accessibility policy
    -> runtime interaction state
```

There are no selectors, ancestor rules, specificity, or `!important`.

## 4. MenuGraph: the first mature primitive

Menus are the conformance nucleus because they force the contract to represent
hierarchy, commands, grouping, state, navigation, input, lazy data, failure,
and styling without requiring a general document engine.

The initial semantic vocabulary is:

```text
Menu
Group
Submenu
Command
Separator
Toggle
ChoiceGroup
Choice
Header
Value
```

`Rocker` is not a semantic node. It is a graphical recipe for `Toggle`. A TUI,
screen reader, Win32 menu, and Android renderer may project the same toggle
differently without changing its meaning.

Common node state includes:

```text
Visible
Enabled
Checked
Selected
Indeterminate
Default
Busy
```

Commands carry a stable command identifier and optional serializable context.
They do not carry delegates, script text, platform command pointers, or native
handles.

### 4.1 Win32 menu import

Win32 `HMENU` is a primary conformance source, not the canonical model. Import
is divided into two records:

```text
Win32MenuReceipt   exact captured evidence
MenuGraph          portable semantic interpretation
```

A receipt should preserve, where safely available:

- label and mnemonic
- command ID and position
- `MFT_*` type flags
- `MFS_*` state flags
- submenu identity
- bitmap/icon information
- canonical verb
- owner/source identity
- an opaque item-data receipt that is never invoked by a renderer

Ambiguous or unresolved shell entries remain explicit unknown commands. The
importer must not invent labels, roles, or invocation semantics.

### 4.2 SettingsGraph

SettingsGraph extends MenuGraph with editable values:

```text
Slider
TextInput
TextArea
ChoiceGroup
Value
```

Classic Win32 menu projection may degrade rich controls into checked items,
choice submenus, or commands that open a detail page. Rich Android, Direct2D,
and Canvas projections retain the full controls.

## 5. Profiles and simultaneous projections

Accordion, drill-down, cascade, palette, radial, macro-map, and TUI are
projections, not separate application models.

A profile can compose them:

```text
Profile
|- Plane "front"
|  |- MenuRoot
|  `- InlineExpansion source="front.active"
|- Plane "right"
|  `- ChildViewport source="front.active.children"
|- Plane "overlay"
|  `- CommandPalette source="graph.search"
`- Transitions
```

On a wide viewport, front and right may remain visible simultaneously. On a
narrow viewport, the right plane may translate into the front position while
the previous page remains in a retained navigation stack.

Profiles are trusted Terminal assets. Packages select an admitted profile; they
do not define arbitrary motion, hit testing, or renderer behavior.

Suggested built-in profiles:

```text
settings
phone-context
desktop-cascade
two-pane
command-palette
macro-map
tui
```

## 6. Example semantic document

```xml
<?xml version="1.0" encoding="utf-8"?>
<surface id="settings" title="Settings" profile="settings">
  <menu id="system">
    <submenu id="display"
             label="Display"
             caption="Monitors, brightness, night light, display profile"
             icon="display">
      <slider id="brightness"
              label="Brightness"
              bind="Display.Brightness"
              min="0"
              max="100" />
      <toggle id="nightLight"
              label="Night light"
              bind="Display.NightLight" />
      <choice-group id="scale"
                    label="Scale"
                    bind="Display.Scale">
        <choice value="100">100%</choice>
        <choice value="125">125%</choice>
        <choice value="150">150%</choice>
      </choice-group>
    </submenu>
    <submenu id="sound"
             label="Sound"
             caption="Volume levels, output, input, sound devices"
             icon="sound" />
  </menu>
</surface>
```

This document contains no Android class, Win32 control, CSS selector, script,
coordinate, or animation.

## 7. Behavior and event boundary

PowerShell attaches behavior to typed nodes after validation:

```powershell
$UI.brightness.Value = Get-DisplayBrightness

$UI.brightness.Changed = {
    param($Event)
    Set-DisplayBrightness $Event.Value
}

$UI.nightLight.Changed = {
    param($Event)
    Set-NightLight -Enabled $Event.Value
}
```

The renderer returns semantic events only:

```text
Invoke(commandId, context)
Set(bindingId, value)
Navigate(nodeId)
BackRequested
RequestChildren(nodeId, requestId)
CancelRequest(requestId)
```

Dynamic submenus use bounded, cancellable `RequestChildren` operations. A
failed provider produces a local diagnostic row and does not destroy the menu
or runspace.

Markup files and PowerShell builder APIs converge on the same typed graph.
Dynamic scripts build typed nodes; they do not concatenate XML.

## 8. Recipe and theme separation

Application markup may select admitted semantic roles:

```text
Primary
Danger
Muted
Compact
Comfortable
```

It may not set arbitrary colors, margins, radii, shaders, or platform
properties.

A theme may use a deliberately small INI token format:

```ini
[Theme]
Name = Terminal Dark
Base = Terminal.Default

[Color]
Canvas = #202124
Surface = #292A2E
SurfaceRaised = #303137
Text = #F5F5F5
TextMuted = #A8ADB7
Accent = #47C5F1
Danger = #E74856

[Spacing]
Unit = 4
CardGap = 6
CardPaddingX = 14
CardPaddingY = 12

[Typography]
Family = System
MonoFamily = Cascadia Code
BodySize = 14
CaptionSize = 12
TitleSize = 24

[Motion]
Fast = 120ms
Normal = 180ms
PageTransition = 220ms
Curve = standard

[Gradient.Surface]
Start = ${Color.SurfaceRaised}
End = ${Color.Surface}
Angle = 90
```

Token references are allowed. General expressions are not. Semantic values
such as `system`, `pill`, and `hairline` are resolved by the trusted recipe
engine.

## 9. Glyph Profiles

A Glyph Profile is a small human-readable coverage mask that defines reusable
geometry visually. It is ASCII-authored vector geometry: durable as text,
directly meaningful in a TUI, and compilable into native paths or distance
fields.

Coverage characters are:

```text
.  outside / 0%
░  25%
▒  50%
▓  75%
█  inside / 100%
```

Example:

```ini
[Shape.WindowCorner]
Width = 12
Height = 8
Mirror = Both
Row0 = .....░▒▓████
Row1 = ...░▓██████
Row2 = ..▒████████
Row3 = .▓█████████
Row4 = ███████████
Row5 = ███████████
Row6 = ███████████
Row7 = ███████████
```

At theme admission or load time:

```text
glyph rows
    -> validated coverage grid
    -> normalized contour
    -> cached vector path and/or signed-distance field
```

Android Canvas consumes a cached `Path` or alpha mask. Windows Direct2D
consumes path geometry or an opacity mask. Shader surfaces consume the same
distance field. TUI projection may consume the original glyph rows.

Validation rules include:

- fixed maximum dimensions
- exact row width and declared height
- known coverage glyphs only
- explicit mirroring
- monotonic contour requirement for ordinary corner roles
- no disconnected islands unless the role explicitly permits them
- deterministic fallback to the built-in profile on rejection
- compilation outside the frame loop

Initial admitted roles should remain restrained:

```text
WindowCorner
CardCorner
RockerThumb
SelectionHandle
Pinger
FocusRing
MenuPointer
```

Glyph Profiles define reusable semantic geometry, not arbitrary bitmap skins.

## 10. WYSIWYG theme editing

Terminal may provide a native theme editor that edits validated token
candidates and Glyph Profiles against a live component gallery.

```text
candidate edit
    -> validate
    -> immutable ThemeSnapshot
    -> invalidate affected surfaces
    -> live preview
```

`Apply` atomically replaces the theme file. `Cancel` restores the previous
snapshot. `Reset` reloads built-in defaults. The renderer never reads a
partially written file and never reparses theme text per frame.

The preview gallery should contain at least:

- navigation card
- expanded group
- toggle/rocker
- slider
- choice group
- breadcrumb
- primary, danger, and disabled commands
- focused and pressed states

## 11. Rendering pipeline

```text
XML parser or typed PowerShell builder
    -> validated semantic graph
    -> Projection Plan compiler
    -> renderer-neutral layout and recipe resolution
    -> render tree / draw list
    -> platform renderer
```

The resolved layout is inspectable and testable:

```text
NavigationCard display
  bounds         16,112,380,68
  iconBounds     30,128,24,24
  titleOrigin    68,132
  captionOrigin  68,153
  arrowBounds    359,135,16,16
  cornerProfile  CardCorner
```

### 11.1 Android

The first projection may continue to emit native Android Views using the
existing `SurfaceAndroidRenderer`. Platform-native input, accessibility, IME,
lifecycle, and system intents remain valuable.

Shared layout and recipe resolution target screen-space consistency, not pixel
identity. Renderers preserve the same semantic layout, proportions, spacing
system, control geometry, alignment, and visual hierarchy, subject to platform
text, DPI, accessibility, and input conventions. Android Canvas may execute the
resolved draw list while typed native text-editing escape hatches remain where
platform behavior is more valuable than identical rasterization.

### 11.2 Windows

The intended Windows projection is:

```text
Win32             window, input, DPI, IME, accessibility
Direct2D          geometry, clipping, gradients, images
DirectWrite       text shaping and glyph layout
D3D interop       shader, terminal, video, and shared GPU surfaces
```

WinForms may host a surface or serve as a diagnostic renderer, but WinForms
controls must not define the contract.

### 11.3 TUI and GPU surfaces

TUI and shader nodes are typed semantic children. They receive admitted input
and bounds from the host. They do not become alternate document runtimes.

## 12. Navigation and lifecycle invariants

Drill-down retains page instances and presentation state:

```text
NavigationStack
|- SystemRoot  scroll=412 focus=display
`- Display     scroll=0   focus=brightness
```

A transition may translate both pages, but it does not semantically purge the
parent. Renderer caches may be reclaimed under memory pressure while the
navigation state remains reconstructable.

Activity or HWND recreation destroys platform views, not the semantic graph,
Projection Plan state, or PowerShell session. Hidden surfaces produce no animation
or rendering work.

## 13. Versioning and capability negotiation

Every document declares the minimum contract version it requires. Every
renderer reports supported semantic nodes, roles, profile features, and
optional surface kinds.

Adding a node or role requires:

1. schema definition
2. typed managed representation
3. parser rejection tests
4. at least one admitted renderer
5. failure and fallback behavior
6. accessibility semantics
7. executable gallery fixture
8. serialization and round-trip tests

Profiles and themes cannot create new authority. Unsupported optional visual
features degrade; unsupported required semantics reject the document.

## 14. Conformance proof

The initial conformance fixture should use a real captured Explorer menu and
Terminal's Settings graph.

This proof is the design gate. No major node family, styling mechanism,
projection concept, or renderer abstraction should be added until the fixture
passes. Failures refine the existing contract before new ontology is invented.

1. Import the Win32 receipt without losing identifiers, flags, hierarchy,
   separators, state, or unresolved entries.
2. Render one MenuGraph as Android drill-down, Windows cascade, inline
   expansion, command palette, TUI, and macro map.
3. Dispatch the same semantic command IDs from every projection.
4. Round-trip `settings.xml -> SettingsGraph -> text dump -> SettingsGraph`.
5. Render a Settings page through both the existing handwritten Android UI and
   HyperSurface recipes, then compare geometry and interaction behavior.
6. Prove that one failed dynamic submenu or behavior script remains local.
7. Prove that theme changes update all recipes from one immutable snapshot.
8. Prove that the same Glyph Profile produces equivalent contours in Android,
   Direct2D, shader, and TUI projections.

Passing this proof validates the durable schema independently of renderer
completion.

## 15. Relationship to the existing Surface contract

Surface Contract 0 and 1 remain the executable product boundary. HyperSurface
is the design candidate for a later contract version. It should evolve by
extending the existing strict parser, typed node model, origin-scoped resources,
event dispatcher, theme catalog, and Android renderer rather than creating a
parallel framework.

No current schema, hardpoint, or Settings implementation is deprecated by this
document.
