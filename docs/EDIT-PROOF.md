# Edit hardpoint proof

Validated on 2026-08-10 using a OnePlus CPH2451 running Android 16 / API 36.

`dev.mansfield.edit` is hydrated after the clean APK build. The base project has no compile-time knowledge of the hardpoint's XML or PowerShell behavior.

## Passing evidence

- Surface API 1 admitted and rendered the hardpoint from `assets/hardpoints/dev.mansfield.edit`.
- The vertical workspace obeyed explicit `grow`; the native multiline editor consumed remaining space.
- Android IME input produced two lines without WebView or terminal-string mediation.
- `Changed` updated dirty state and UTF-8 byte size; `CursorChanged` reported one-based line and column.
- Saving `Home:\untitled.ps1` wrote UTF-8 without a BOM to Terminal's private PowerShell home.
- New followed by Open restored the exact two-line document and reported `OPENED`.
- An unsaved `MODIFIED` buffer survived portrait-to-landscape and landscape-to-portrait Activity recreation.
- Native `EditText` supplies Android selection, handles, clipboard integration, and IME composition at the renderer boundary.
- Closing a hardpoint leaves a quiet prompt; `$UI` remains available for intentional PowerShell inspection.

## Scope boundary

This proves the post-build application seam and a defensible native text primitive. It does not claim a complete code editor. Syntax services, line numbers, undo/redo command projection, find/replace, command overflow, the settings blade, crash recovery, and file-picker integration remain follow-on hardpoint or admitted-control work.

## Receipt identity

```text
base       6c85d142e55aa171e925839d67bd9daeb3b78346879f15c43c96eb472dd7ed25
recipe     a19fc36896121ccef9a099a561cbd153eff434f06ad7a51629f0a1bc80e77b06
edit       51c3f085b7a88c226717bc8497ac3a689a4cfe02bb5fa26dbf669001bdd66db4
artifact   b0d3c150056da78b791aa98332d2b0a3093acc9e2e5393037a16d011170e13c7
```
