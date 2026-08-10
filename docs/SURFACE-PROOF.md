# Surface Contract 0 proof

Validated on 2026-08-10 using a OnePlus CPH2451 running Android 16 / API 36.

## Passing evidence

- Android ARM64 build: 0 errors.
- `Test-SurfaceContract`: 9/9 parser, literal-text, and origin-stream checks passed on-device.
- Hydrated hardpoint discovery: `dev.mansfield.surface-proof` loaded from APK assets without a `.csproj` asset reference.
- Native renderer: typed nodes rendered as Android `TextView`, `EditText`, `Button`, `ListView`, and `View` instances.
- One-way dispatch: Android callbacks returned through the bounded queue; PowerShell handlers ran on the owning runspace.
- Live command palette: typing `process` populated results without pressing Search.
- Object identity: selecting `Get-Process` delivered the retained `CommandInfo` through `$Event.Item`, and the handler read its `Name` property.
- Shadow state: the query and result count survived Android Activity replacement and native View reconstruction.
- Additive admission: attempting to use the hydrated APK as a base was rejected.
- Release verification: `zipalign -c` and `apksigner verify --verbose --print-certs` passed.

The proof release receipt records these manufacturing hashes:

```text
base       0722d46dcb84d9ecda2b2c7cb16b621ad3ec1e06c5ac9170872ab2ac8d40a1f2
recipe     f1759c2df676b71659ce4ade15bbd552b9f03cce13d40ef709613873b0bf19ca
hardpoint  c9329b0a6957107a98844a670cb5d6ad06697664998671ba04e3aa6dd5701673
artifact   4bab4912748c6df397620ba2b5ca5305c76a5194ad0aed3fa015134901836f87
```

## Pressure revealed by the proof

These are deliberately not part of Contract 0 yet:

- an accessibility metadata vocabulary;
- explicit grow, shrink, and minimum-size layout semantics;
- a versioned style/theme contract beyond opaque style tokens;
- list templates and richer object projections;
- cancellation or coalescing policy for expensive live-search handlers;
- durable hardpoint installation outside release hydration.

They remain outside the contract until a real hardpoint proves the smallest defensible shape.
