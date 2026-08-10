# Surface design canon

Terminal's donor applications are executable design references, not implementation references. Preserve their UX; discard their web substrate.

## Product shape

Terminal is a PowerShell/CoreCLR workstation whose applications are peers:

```text
PowerShell / CoreCLR host
    terminal
    edit
    files
    task manager
    graph
    agent
    future hardpoints
```

The editor is not the product root and hardpoints are not editor plugins. Surface is the semantic boundary that lets each application attach PowerShell behavior to native presentation without becoming a compile-time dependency of the base APK.

## Authoritative donors

- `settings.obp` is the visual north star: compact rows, useful hierarchy, interactive navigation, restrained toggles, and fluent drill-downs.
- `edit.obp` is the first serious post-build hardpoint and the authority for editor anatomy.
- `taskmgr.obp` stresses dense live object state.
- `graph.obp` stresses spatial and custom presentation.
- `files.obp` is archaeological input, not a normative file-manager design.

The archived sources currently live under `S:\backup\src00002\Shell\presenters`. They do not ship and are not build inputs.

## Preserve

- silhouette and information hierarchy;
- workstation density and command priority;
- spacing and typography relationships;
- selection, dirty, and transient state;
- theme vocabulary, one-accent restraint, and low rounding;
- interaction flow and motion intent.

Translate HTML hierarchy to typed Surface XML, JavaScript behavior to PowerShell, browser controls to native renderer primitives, and expensive reusable machinery to an explicitly admitted C# control seam.

Discard DOM, CSS mechanics, browser storage, JavaScript plumbing, WebView workarounds, transport glue made unnecessary by the in-process runspace, and implementation-specific icon codepoints.

Semantic fidelity is mandatory. Visual fidelity is the default. Literal pixel fidelity is subordinate to native input, density, accessibility, and device geometry.

## Edit anatomy

The target remains deliberately dense:

```text
document identity + dirty state
compact essential command strip
editor consuming the workspace
very compact cursor / language / size status
transient menu, prompts, notifications, and settings blade
```

The current `dev.mansfield.edit` hardpoint proves the granite seam: path identity, native multiline editing, dirty state, cursor position, UTF-8 size, open/save, and Activity reconstruction. It is not yet the complete donor UI. Menus, the settings blade, syntax-aware reusable editor machinery, and the full essential command set remain deliberate follow-on work.

Do not modernize this into a card feed, mobile IDE imitation, or Acode-shaped plugin tree. The house style is what Metro/Fluent might have become as a dense PowerShell workstation.
