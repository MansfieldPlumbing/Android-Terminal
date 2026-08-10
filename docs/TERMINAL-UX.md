# Terminal native console contract

Terminal is an Android phone application first. It is not a desktop environment, Linux skin, WebView terminal, or PC control panel. Its interaction model is three complementary instruments:

```text
native terminal + command puck + optional mini keyboard
```

The terminal is foundational. The command puck is standard Terminal UX. The compact keyboard is an optional text-entry projection and never controls puck availability.

## Responsibility boundary

```text
TerminalEngine       VT, screens, cursor, modes, scrollback, selection
NativeConsoleView    fixed-cell Android Canvas projection and touch viewport
TerminalInput        IME, physical-keyboard, mini-keyboard and puck input routing
PowerShell           commands, objects and behavior
C# / Android         machinery requiring platform authority
Surface XML          optional composition of typed major instruments only
```

VT state, terminal cells, gestures, and physics are never described in XML. Android classes are never exposed to PowerShell.

## Implementation order

1. TerminalEngine: screens, cursor, VT and scrollback.
2. NativeConsoleView: fixed-cell visible-viewport rendering, dirty rows, backgrounds, selection and cursor.
3. TerminalInput: invisible IME bridge, physical keys, and grid-owned composition.
4. CommandGraph: reuse the existing command palette as the deep readable surface.
5. TerminalCommandPuck: rotary traversal, directional precision, center commitment and restrained haptics.
6. Puck mobility: deliberate long-hold, inertia, trajectory-based edge capture and peek state.
7. Terminal Mini Keyboard: compact layered QWERTY and shared acrylic material.
8. Selection, links, search, accessibility, lifecycle and performance polish.

Do not reverse this order because the puck is more entertaining to build.

## Lifecycle invariant

The Activity owns views, gestures, physics and transient materials. When it is absent there is no Canvas work, animation clock, physics callback, terminal polling, or hidden acrylic animation. The process-owned runtime may retain the CoreCLR runspace, PowerShell state, terminal model, and explicitly requested background work.

Nothing receives a timer merely because Terminal exists.

## Puck invariant

The puck is a work instrument, not a joystick, D-pad graphic, fake keyboard, or accessibility bubble. Gesture intent selects rotary versus discrete behavior. A normal tap reveals a small contextual radial layer; deeper navigation hands off to the existing command palette and shared semantic command graph. Moving the puck requires a deliberately long hold. Edge capture depends on trajectory, not proximity, and a docked puck retracts to a tappable crescent without competing with Android edge gestures.

## Keyboard invariant

The optional Terminal keyboard is a dense, conventional, layered QWERTY slab for character and shell-symbol entry. Navigation belongs to the puck. System IME remains available as both a preference and a temporary escape hatch. Acrylic preserves spatial continuity, not terminal readability through the keys.
