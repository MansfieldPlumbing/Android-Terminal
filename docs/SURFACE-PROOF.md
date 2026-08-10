# Surface Contract 0 and 1 proof

Validated on 2026-08-10 using a OnePlus CPH2451 running Android 16 / API 36.

## Passing evidence

- Android ARM64 build: 0 errors. The repository's existing analyzer/nullability warning debt remains visible.
- `Test-SurfaceContract`: 9/9 parser, literal-text, and origin-stream checks passed on-device.
- Hydrated hardpoint discovery: `dev.mansfield.surface-proof` loaded from APK assets without a `.csproj` asset reference.
- Native renderer: typed nodes rendered as Android `TextView`, `EditText`, `Button`, `ListView`, and `View` instances.
- One-way dispatch: Android callbacks returned through the bounded queue; PowerShell handlers ran on the owning runspace.
- Live command palette: typing `process` populated results without pressing Search.
- Managed discovery: broad and empty queries remained inside Alias/Function/Filter/Cmdlet discovery and did not call PowerShell's unavailable native `IsExecutable` probe.
- Object identity: selecting `Get-Process` delivered the retained `CommandInfo` through `$Event.Item`, and the handler read its `Name` property.
- Shadow state: the query and result count survived Android Activity replacement and native View reconstruction.
- Additive admission: attempting to use the hydrated APK as a base was rejected.
- Release verification: `zipalign -c` and `apksigner verify --verbose --print-certs` passed.

The proof release receipt records these manufacturing hashes:

```text
base       6c85d142e55aa171e925839d67bd9daeb3b78346879f15c43c96eb472dd7ed25
recipe     a19fc36896121ccef9a099a561cbd153eff434f06ad7a51629f0a1bc80e77b06
palette    5bf7f412fce570037004fbb4eb9d01ecee61d840245314428d7a365ea5f75170
edit       51c3f085b7a88c226717bc8497ac3a689a4cfe02bb5fa26dbf669001bdd66db4
artifact   b0d3c150056da78b791aa98332d2b0a3093acc9e2e5393037a16d011170e13c7
```

## Pressure revealed by the proof

These are deliberately not part of Contract 1 yet:

- an accessibility metadata vocabulary;
- shrink and minimum-size layout semantics beyond Contract 1's explicit `grow` intent;
- a versioned style/theme contract beyond opaque style tokens;
- list templates and richer object projections;
- cancellation or coalescing policy for expensive live-search handlers;
- durable hardpoint installation outside release hydration.

They remain outside the contract until a real hardpoint proves the smallest defensible shape.
