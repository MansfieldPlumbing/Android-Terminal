# Release manufacturing

Terminal separates the compiled Android identity from optional product cargo.

The `.csproj` produces a clean base APK containing CoreCLR, PowerShell, Roslyn, the native console host, Surface Contract 1, the renderer, and the hardpoint loader. A release recipe then admits hardpoints under `assets/hardpoints/`, aligns the APK, signs it, verifies it, and writes an external XML receipt.

The hydrator never edits `AndroidManifest.xml`, `resources.arsc`, `res/`, `classes*.dex`, `lib/`, or core managed assemblies. A different package ID, launcher label, launcher icon, manifest component, or Android version requires a separately compiled base. Runtime Surface branding and feature composition do not.

## Distribution profiles are admission policy

Release channels may share BOS semantics without admitting the same package origins.

```text
Play profile
    executable cargo delivered inside the Play-distributed artifact
    interpreted hardpoints admitted only under the declared Terminal policy
    no external dex, JAR or .so download/admission

Owner / sideload profile
    may admit explicitly user-obtained cargo
    still requires provenance, digest, manifest, consent and worker containment
```

Google Play currently prohibits Play-distributed apps from downloading executable code such as dex, JAR or `.so` files from outside Google Play. Interpreted code loaded at runtime remains responsible for complying with every Play policy. Therefore a Play recipe must fail manufacturing if it enables an external native/package acquisition path; a runtime setting is not a sufficient distribution boundary.

This restriction belongs to release admission, not BOS ontology. `ExecutorId = native.shared-object` remains valid, but a Play build may resolve only cargo delivered through its admitted Play artifact or another explicitly Play-compliant delivery mechanism.

The Play profile must also pass a manifest-policy receipt before signing:

- `MANAGE_EXTERNAL_STORAGE` is absent unless broad file management is documented as core functionality and the required Play declaration has been approved;
- every foreground-service type is user-beneficial, user-perceptible, terminable, accurately declared and included in the Play Console declaration;
- the artifact exposes no self-update or externally downloaded native-code path;
- declared permissions and exported components match the release allowlist.

Policy references are inputs to the release receipt and must be revalidated for each public submission:

- <https://support.google.com/googleplay/android-developer/answer/16559646>
- <https://support.google.com/googleplay/android-developer/answer/10467955>

## Build the development release

```powershell
./scripts/build.ps1
```

The default recipe is `releases/dev.xml`. Outputs are kept outside `src`:

- clean base: `build/bin/Terminal.Android/<Configuration>/net11.0-android/android-arm64/dev.mansfieldplumbing.terminal.apk`
- hydrated artifact: `build/releases/dev/Terminal-dev-Signed.apk`
- provenance: `build/releases/dev/Terminal-dev-Signed.receipt.xml`

Use `-SkipHydration` to build only the base.

The publisher requires PowerShell 7; `build.ps1` launches the bundled `C:\bin\pwsh\pwsh.exe` by default and exposes `-PowerShell` when it lives elsewhere.

## Recipe and hardpoint shape

```xml
<release id="dev">
  <base ref="terminal" />
  <hardpoints>
    <hardpoint src="../hardpoints/dev.mansfield.surface-proof" />
    <hardpoint src="../hardpoints/dev.mansfield.edit" />
  </hardpoints>
  <signing ref="debug" />
</release>
```

```text
hardpoints/dev.mansfield.surface-proof/
    manifest.xml
    UI/main.xml
    Scripts/main.ps1
    Assets/...

hardpoints/dev.mansfield.edit/
    manifest.xml
    UI/main.xml
    Scripts/main.ps1
```

Every recipe path is relative. Hardpoints may not contain links, absolute paths, path escapes, duplicate IDs, unsupported Surface API versions, or undeclared UI/script files.

## Signing

The `debug` profile uses the standard local Xamarin Android debug keystore. A non-debug recipe requires a matching `-SigningProfile`, a `-Keystore`, and passwords supplied only through the process environment:

```powershell
$env:TERMINAL_KEYSTORE_PASSWORD = '<secret>'
$env:TERMINAL_KEY_PASSWORD = '<secret>'
./scripts/build.ps1 -Configuration Release -Recipe releases/play.xml `
    -SigningProfile play -Keystore C:\secure\terminal-release.jks
```

Passwords are passed to `apksigner` through environment-backed password specifications, not command-line text or repository files.

## Admission guarantees

`scripts/Publish-TerminalRelease.ps1`:

1. validates the recipe and every hardpoint with DTDs disabled;
2. deterministically hashes each sorted hardpoint tree;
3. rejects signed or previously hydrated bases;
4. adds only `assets/hardpoints/<id>/...` entries;
5. re-hashes every original APK payload entry and rejects mutation;
6. runs and verifies `zipalign`;
7. signs and verifies the APK, then records the signer certificate hash;
8. publishes atomically only after all checks pass;
9. emits an external receipt containing the base, recipe, cargo, signer, and final artifact hashes.

The receipt is provenance, not runtime authority. Terminal boots without it. A hardpoint cannot assume a package name, APK filename, signing key, channel, or workstation path.
