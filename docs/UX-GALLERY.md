# Surface UX gallery

`dev.mansfield.gallery` is the executable design fixture for Surface API 1. It is included in development releases and is not required cargo for a production release.

The gallery has three jobs:

1. show hardpoint authors the controls and compositions that actually exist;
2. give maintainers one screen on which renderer and theme changes can be reviewed together;
3. make house-style drift detectable before copied constants become a second styling system.

## What a hardpoint is

A hardpoint is a small, origin-scoped package:

```text
hardpoints/<reverse-domain-id>/
    manifest.xml
    UI/main.xml
    Scripts/main.ps1
    Assets/...                  optional
```

`manifest.xml` declares the Surface API, UI document, and behavior script. XML declares typed structure and initial state. PowerShell attaches behavior to typed nodes through `$UI`. Android renders those nodes natively. None of the three layers impersonates another.

## Style contract

Surface does not accept arbitrary CSS, colors, dimensions, Android classes, or renderer properties. A `style` is a closed semantic token interpreted by the native renderer and theme.

Surface API 1 defines:

| Token | Valid node | Meaning |
|---|---|---|
| `workspace` | `surface` | Dense application workspace. |
| `command-bar` | `stack` | Compact horizontal command grouping. |
| `status-bar` | `stack` | Compact status and document-state grouping. |
| `hero` | `text` | One primary heading, used sparingly. |
| `status` | `text` | Compact subordinate status text. |
| `editor` | `text-area` | Monospace, high-density editing surface. |

Unknown tokens and tokens applied to the wrong node are rejected. Shared colors, typography, spacing, and chrome dimensions live in `SurfaceTheme.cs`; hardpoints select semantics rather than copying visual constants.

## Composition guidance

- Start with one vertical workspace stack.
- Establish hierarchy with one hero or ordinary body text, not repeated headings.
- Use command bars for a few immediate actions; deeper command sets belong in the shared command graph.
- Give the primary list or editor the available `grow` space.
- Keep status compact and subordinate.
- Prefer native inputs, selection, clipboard, IME, lists, and accessibility behavior.
- Put behavior in PowerShell and expensive reusable machinery in a reviewed C# control seam.
- Do not encode scripts, bindings, permissions, Android resources, or platform types in XML.

The gallery is a floor, not a ceiling. A future Surface API version may add semantic controls and tokens only after they have a native implementation, a failure model, an accessibility story, a schema update, and a gallery example.
