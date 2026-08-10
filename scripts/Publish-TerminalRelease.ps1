[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Recipe,
    [Parameter(Mandatory)][string]$BaseApk,
    [string]$BaseIdentity = 'terminal',
    [string]$SigningProfile = 'debug',
    [string]$OutputDirectory,
    [string]$BuildTools = 'S:\bin\android-sdk\build-tools\36.0.0',
    [string]$JavaSdk = 'S:\bin\jdk',
    [string]$Keystore,
    [string]$KeyAlias = 'androiddebugkey'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-RequiredFile([string]$Path, [string]$Label) {
    $resolved = [IO.Path]::GetFullPath($Path)
    if (-not [IO.File]::Exists($resolved)) { throw "$Label does not exist: $resolved" }
    return $resolved
}

function Get-Sha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-StrictXml([string]$Path) {
    $settings = [Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.IgnoreComments = $true
    $settings.IgnoreWhitespace = $true
    $settings.XmlResolver = $null
    $reader = [Xml.XmlReader]::Create($Path, $settings)
    try {
        $document = [Xml.XmlDocument]::new()
        $document.PreserveWhitespace = $false
        $document.Load($reader)
        return $document
    }
    finally { $reader.Dispose() }
}

function Assert-Attributes([Xml.XmlElement]$Element, [string[]]$Allowed) {
    foreach ($attribute in $Element.Attributes) {
        if ($attribute.Prefix -eq 'xmlns' -or $attribute.Name -eq 'xmlns' -or $attribute.Name -notin $Allowed) {
            throw "Unknown attribute '$($attribute.Name)' on <$($Element.Name)>."
        }
    }
}

function Get-RequiredAttribute([Xml.XmlElement]$Element, [string]$Name) {
    $value = $Element.GetAttribute($Name)
    if ([string]::IsNullOrWhiteSpace($value)) { throw "<$($Element.Name)> requires '$Name'." }
    return $value
}

function Get-ElementChildren([Xml.XmlElement]$Element) {
    $children = [Collections.Generic.List[Xml.XmlElement]]::new()
    foreach ($node in $Element.ChildNodes) {
        if ($node -is [Xml.XmlElement]) { $children.Add($node) }
        elseif ($node.NodeType -notin @([Xml.XmlNodeType]::Whitespace, [Xml.XmlNodeType]::SignificantWhitespace)) {
            throw "Unexpected content inside <$($Element.Name)>."
        }
    }
    return $children
}

function Assert-EmptyElement([Xml.XmlElement]$Element) {
    if (@(Get-ElementChildren $Element).Count -ne 0 -or -not [string]::IsNullOrWhiteSpace($Element.InnerText)) {
        throw "<$($Element.Name)> must be empty."
    }
}

function ConvertTo-HardpointPath([string]$Path) {
    if ([IO.Path]::IsPathRooted($Path) -or $Path.Contains(':')) { throw "Hardpoint path must be relative: $Path" }
    $parts = [Collections.Generic.List[string]]::new()
    foreach ($part in ($Path.Replace('\', '/') -split '/')) {
        if ([string]::IsNullOrEmpty($part) -or $part -eq '.') { continue }
        if ($part -eq '..') {
            if ($parts.Count -eq 0) { throw "Hardpoint path escapes its origin: $Path" }
            $parts.RemoveAt($parts.Count - 1)
            continue
        }
        if ($part.IndexOfAny([char[]]@(0, 10, 13)) -ge 0) { throw "Hardpoint path contains a control character." }
        $parts.Add($part)
    }
    if ($parts.Count -eq 0) { throw 'Hardpoint path cannot be empty.' }
    return [string]::Join('/', $parts)
}

function Get-HardpointFiles([string]$Root) {
    $rootItem = Get-Item -LiteralPath $Root -Force
    if (-not $rootItem.PSIsContainer) { throw "Hardpoint root is not a directory: $Root" }
    if (($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Hardpoint root cannot be a link: $Root" }
    $files = [Collections.Generic.List[object]]::new()
    $pending = [Collections.Generic.Stack[IO.DirectoryInfo]]::new()
    $pending.Push($rootItem)
    while ($pending.Count -gt 0) {
        $directory = $pending.Pop()
        foreach ($item in (Get-ChildItem -LiteralPath $directory.FullName -Force | Sort-Object Name)) {
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Hardpoint cargo cannot contain links: $($item.FullName)"
            }
            if ($item.PSIsContainer) { $pending.Push($item) }
            else {
                $relative = [IO.Path]::GetRelativePath($rootItem.FullName, $item.FullName).Replace('\', '/')
                $files.Add([pscustomobject]@{ Relative = (ConvertTo-HardpointPath $relative); FullName = $item.FullName })
            }
        }
    }
    return @($files | Sort-Object Relative)
}

function Add-HashLength([Security.Cryptography.IncrementalHash]$Hasher, [long]$Value) {
    $length = [BitConverter]::GetBytes($Value)
    if ([BitConverter]::IsLittleEndian) { [Array]::Reverse($length) }
    $Hasher.AppendData($length)
}

function Add-HashFrame([Security.Cryptography.IncrementalHash]$Hasher, [byte[]]$Bytes) {
    Add-HashLength $Hasher $Bytes.LongLength
    $Hasher.AppendData($Bytes)
}

function Get-HardpointHash([object[]]$Files) {
    $hasher = [Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA256)
    try {
        foreach ($file in $Files) {
            Add-HashFrame $hasher ([Text.Encoding]::UTF8.GetBytes($file.Relative))
            $stream = [IO.File]::OpenRead($file.FullName)
            try {
                Add-HashLength $hasher $stream.Length
                $buffer = [byte[]]::new(1MB)
                while (($read = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                    $hasher.AppendData($buffer, 0, $read)
                }
            }
            finally { $stream.Dispose() }
        }
        return [Convert]::ToHexString($hasher.GetHashAndReset()).ToLowerInvariant()
    }
    finally { $hasher.Dispose() }
}

function Read-Hardpoint([string]$Root) {
    $fullRoot = [IO.Path]::GetFullPath($Root)
    $manifestPath = Get-RequiredFile (Join-Path $fullRoot 'manifest.xml') 'Hardpoint manifest'
    $document = Get-StrictXml $manifestPath
    $element = $document.DocumentElement
    if ($null -eq $element -or $element.Name -ne 'hardpoint') { throw "$manifestPath must have a <hardpoint> root." }
    Assert-Attributes $element @('id', 'surface-api')
    $id = Get-RequiredAttribute $element 'id'
    if ($id -notmatch '^[A-Za-z_][A-Za-z0-9_-]*(\.[A-Za-z_][A-Za-z0-9_-]*)*$' -or $id.Length -gt 128) {
        throw "Invalid hardpoint id '$id'."
    }
    $apiText = Get-RequiredAttribute $element 'surface-api'
    $api = 0
    if (-not [int]::TryParse($apiText, [ref]$api) -or $api -ne 0) { throw "Hardpoint '$id' requires unsupported Surface API '$apiText'." }
    $ui = $null
    $script = $null
    foreach ($child in (Get-ElementChildren $element)) {
        if ($child.Name -notin @('ui', 'script')) { throw "Unknown hardpoint element <$($child.Name)>." }
        Assert-Attributes $child @('src')
        Assert-EmptyElement $child
        $src = ConvertTo-HardpointPath (Get-RequiredAttribute $child 'src')
        if ($child.Name -eq 'ui') {
            if ($null -ne $ui) { throw "Hardpoint '$id' declares more than one UI document." }
            $ui = $src
        } else {
            if ($null -ne $script) { throw "Hardpoint '$id' declares more than one script." }
            $script = $src
        }
    }
    if ($null -eq $ui -or $null -eq $script) { throw "Hardpoint '$id' requires one <ui> and one <script>." }
    $files = @(Get-HardpointFiles $fullRoot)
    if ($files.Relative -notcontains $ui -or $files.Relative -notcontains $script) { throw "Hardpoint '$id' references an absent UI or script file." }
    return [pscustomobject]@{ Id = $id; SurfaceApi = $api; Root = $fullRoot; Files = $files; Hash = (Get-HardpointHash $files) }
}

function Read-Recipe([string]$Path) {
    $document = Get-StrictXml $Path
    $root = $document.DocumentElement
    if ($null -eq $root -or $root.Name -ne 'release') { throw 'Release recipe root must be <release>.' }
    Assert-Attributes $root @('id')
    $id = Get-RequiredAttribute $root 'id'
    if ($id -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$') { throw "Invalid release id '$id'." }
    $base = $null
    $signing = $null
    $hardpointGroupSeen = $false
    $sources = [Collections.Generic.List[string]]::new()
    foreach ($child in (Get-ElementChildren $root)) {
        switch ($child.Name) {
            'base' {
                Assert-Attributes $child @('ref')
                Assert-EmptyElement $child
                if ($null -ne $base) { throw 'Release recipe declares more than one base.' }
                $base = Get-RequiredAttribute $child 'ref'
            }
            'signing' {
                Assert-Attributes $child @('ref')
                Assert-EmptyElement $child
                if ($null -ne $signing) { throw 'Release recipe declares more than one signing profile.' }
                $signing = Get-RequiredAttribute $child 'ref'
            }
            'hardpoints' {
                Assert-Attributes $child @()
                if ($hardpointGroupSeen) { throw 'Release recipe declares more than one hardpoint group.' }
                $hardpointGroupSeen = $true
                foreach ($hardpoint in (Get-ElementChildren $child)) {
                    if ($hardpoint.Name -ne 'hardpoint') { throw "Unknown element <$($hardpoint.Name)> inside <hardpoints>." }
                    Assert-Attributes $hardpoint @('src')
                    Assert-EmptyElement $hardpoint
                    $source = Get-RequiredAttribute $hardpoint 'src'
                    if ([IO.Path]::IsPathRooted($source) -or $source.Contains(':')) {
                        throw "Hardpoint recipe source must be relative: $source"
                    }
                    $sources.Add($source)
                }
            }
            default { throw "Unknown release element <$($child.Name)>." }
        }
    }
    if ($null -eq $base -or $null -eq $signing) { throw 'Release recipe requires one base and one signing profile.' }
    return [pscustomobject]@{ Id = $id; Base = $base; Signing = $signing; HardpointSources = $sources }
}

function Get-ZipPayload([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Read, $false)
    try {
        $entries = @{}
        foreach ($entry in $archive.Entries) {
            if ($entries.ContainsKey($entry.FullName)) { throw "APK contains duplicate entry '$($entry.FullName)'." }
            $entryStream = $entry.Open()
            $sha = [Security.Cryptography.SHA256]::Create()
            try { $hash = [Convert]::ToHexString($sha.ComputeHash($entryStream)).ToLowerInvariant() }
            finally { $sha.Dispose(); $entryStream.Dispose() }
            $entries[$entry.FullName] = $hash
        }
        return $entries
    }
    finally { $archive.Dispose(); $stream.Dispose() }
}

function Add-HardpointsToApk([string]$Path, [object[]]$Hardpoints) {
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Update, $false)
    try {
        $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($entry in $archive.Entries) {
            if (-not $names.Add($entry.FullName)) { throw "APK contains duplicate entry '$($entry.FullName)'." }
            if ($entry.FullName.StartsWith('assets/hardpoints/', [StringComparison]::Ordinal)) {
                throw 'Base APK is already hydrated; additive releases must start from a clean base.'
            }
            if ($entry.FullName -match '^META-INF/.+\.(SF|RSA|DSA|EC)$') { throw 'Base APK is already signed.' }
        }
        foreach ($hardpoint in $Hardpoints) {
            foreach ($file in $hardpoint.Files) {
                $name = "assets/hardpoints/$($hardpoint.Id)/$($file.Relative)"
                if (-not $names.Add($name)) { throw "APK entry collision: $name" }
                $entry = $archive.CreateEntry($name, [IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = [DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
                $source = [IO.File]::OpenRead($file.FullName)
                $target = $entry.Open()
                try { $source.CopyTo($target) }
                finally { $target.Dispose(); $source.Dispose() }
            }
        }
    }
    finally { $archive.Dispose(); $stream.Dispose() }
}

function Invoke-Native([string]$Tool, [string[]]$Arguments) {
    $output = & $Tool @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw "$([IO.Path]::GetFileName($Tool)) failed ($LASTEXITCODE):`n$($output -join [Environment]::NewLine)" }
    return @($output)
}

function Write-Receipt([string]$Path, [object]$Data) {
    $settings = [Xml.XmlWriterSettings]::new()
    $settings.Indent = $true
    $settings.Encoding = [Text.UTF8Encoding]::new($false)
    $writer = [Xml.XmlWriter]::Create($Path, $settings)
    try {
        $writer.WriteStartDocument()
        $writer.WriteStartElement('terminal-release-receipt')
        $writer.WriteAttributeString('release', $Data.ReleaseId)
        $writer.WriteAttributeString('surface-api', '0')
        $writer.WriteStartElement('base')
        $writer.WriteAttributeString('identity', $Data.BaseIdentity)
        $writer.WriteAttributeString('package-id', $Data.PackageId)
        $writer.WriteAttributeString('sha256', $Data.BaseHash)
        $writer.WriteEndElement()
        $writer.WriteStartElement('recipe')
        $writer.WriteAttributeString('sha256', $Data.RecipeHash)
        $writer.WriteEndElement()
        $writer.WriteStartElement('hardpoints')
        foreach ($hardpoint in $Data.Hardpoints) {
            $writer.WriteStartElement('hardpoint')
            $writer.WriteAttributeString('id', $hardpoint.Id)
            $writer.WriteAttributeString('sha256', $hardpoint.Hash)
            $writer.WriteEndElement()
        }
        $writer.WriteEndElement()
        $writer.WriteStartElement('admission')
        $writer.WriteAttributeString('additive-only', 'true')
        $writer.WriteAttributeString('base-payload-preserved', 'true')
        $writer.WriteEndElement()
        $writer.WriteStartElement('verification')
        $writer.WriteAttributeString('zipalign', 'true')
        $writer.WriteAttributeString('signature', 'true')
        $writer.WriteAttributeString('signing-profile', $Data.SigningProfile)
        $writer.WriteAttributeString('certificate-sha256', $Data.CertificateHash)
        $writer.WriteEndElement()
        $writer.WriteStartElement('artifact')
        $writer.WriteAttributeString('file', [IO.Path]::GetFileName($Data.Artifact))
        $writer.WriteAttributeString('sha256', $Data.FinalHash)
        $writer.WriteEndElement()
        $writer.WriteEndElement()
        $writer.WriteEndDocument()
    }
    finally { $writer.Dispose() }
}

$recipePath = Get-RequiredFile $Recipe 'Release recipe'
$basePath = Get-RequiredFile $BaseApk 'Base APK'
$parsedRecipe = Read-Recipe $recipePath
if ($parsedRecipe.Base -ne $BaseIdentity) { throw "Recipe requires base '$($parsedRecipe.Base)', not '$BaseIdentity'." }
if ($parsedRecipe.Signing -ne $SigningProfile) { throw "Recipe requires signing profile '$($parsedRecipe.Signing)', not '$SigningProfile'." }

$recipeDirectory = [IO.Path]::GetDirectoryName($recipePath)
$hardpoints = [Collections.Generic.List[object]]::new()
$hardpointIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($source in $parsedRecipe.HardpointSources) {
    $hardpoint = Read-Hardpoint ([IO.Path]::GetFullPath((Join-Path $recipeDirectory $source)))
    if (-not $hardpointIds.Add($hardpoint.Id)) { throw "Recipe contains duplicate hardpoint '$($hardpoint.Id)'." }
    $hardpoints.Add($hardpoint)
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $PSScriptRoot "..\build\releases\$($parsedRecipe.Id)"
}
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($outputRoot) | Out-Null
$artifact = Join-Path $outputRoot "Terminal-$($parsedRecipe.Id)-Signed.apk"
$receipt = Join-Path $outputRoot "Terminal-$($parsedRecipe.Id)-Signed.receipt.xml"
$zipalign = Get-RequiredFile (Join-Path $BuildTools 'zipalign.exe') 'zipalign'
$apksigner = Get-RequiredFile (Join-Path $BuildTools 'apksigner.bat') 'apksigner'
$aapt2 = Get-RequiredFile (Join-Path $BuildTools 'aapt2.exe') 'aapt2'

if ($SigningProfile -eq 'debug') {
    if ([string]::IsNullOrWhiteSpace($Keystore)) { $Keystore = Join-Path $env:LOCALAPPDATA 'Xamarin\Mono for Android\debug.keystore' }
    $ksPassword = 'android'
    $keyPassword = 'android'
} else {
    if ([string]::IsNullOrWhiteSpace($Keystore)) { throw 'A release keystore path is required.' }
    $ksPassword = $env:TERMINAL_KEYSTORE_PASSWORD
    $keyPassword = $env:TERMINAL_KEY_PASSWORD
    if ([string]::IsNullOrEmpty($ksPassword) -or [string]::IsNullOrEmpty($keyPassword)) {
        throw 'Set TERMINAL_KEYSTORE_PASSWORD and TERMINAL_KEY_PASSWORD for non-debug signing.'
    }
}
$keystorePath = Get-RequiredFile $Keystore 'Keystore'
$baseHash = Get-Sha256 $basePath
$recipeHash = Get-Sha256 $recipePath
$basePayload = Get-ZipPayload $basePath
if (@($basePayload.Keys | Where-Object { $_.StartsWith('assets/hardpoints/', [StringComparison]::Ordinal) }).Count -ne 0) {
    throw 'Base APK is already hydrated.'
}

$badging = Invoke-Native $aapt2 @('dump', 'badging', $basePath)
$packageLine = $badging | Where-Object { $_ -match "^package: name='([^']+)'" } | Select-Object -First 1
if ($packageLine -notmatch "^package: name='([^']+)'") { throw 'Could not read compiled package identity from base APK.' }
$packageId = $Matches[1]

$temporary = Join-Path $outputRoot ('.terminal-release-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($temporary) | Out-Null
$hydrated = Join-Path $temporary 'hydrated.apk'
$aligned = Join-Path $temporary 'aligned.apk'
$signedCandidate = Join-Path $temporary 'signed.apk'
$receiptCandidate = Join-Path $temporary 'receipt.xml'
$oldJavaHome = $env:JAVA_HOME
$oldKsPass = $env:TERMINAL_APKSIGNER_KS_PASS
$oldKeyPass = $env:TERMINAL_APKSIGNER_KEY_PASS
try {
    [IO.File]::Copy($basePath, $hydrated, $true)
    Add-HardpointsToApk $hydrated $hardpoints
    $hydratedPayload = Get-ZipPayload $hydrated
    foreach ($name in $basePayload.Keys) {
        if (-not $hydratedPayload.ContainsKey($name) -or $hydratedPayload[$name] -ne $basePayload[$name]) {
            throw "Hydration mutated compiled base payload '$name'."
        }
    }
    $expectedAdded = @($hardpoints | ForEach-Object { $hp = $_; $hp.Files | ForEach-Object { "assets/hardpoints/$($hp.Id)/$($_.Relative)" } })
    $actualAdded = @($hydratedPayload.Keys | Where-Object { -not $basePayload.ContainsKey($_) } | Sort-Object)
    if ([string]::Join("`n", ($expectedAdded | Sort-Object)) -ne [string]::Join("`n", $actualAdded)) {
        throw 'Hydration introduced payload outside the admitted hardpoint namespace.'
    }

    Invoke-Native $zipalign @('-f', '-p', '4', $hydrated, $aligned) | Out-Null
    $env:JAVA_HOME = [IO.Path]::GetFullPath($JavaSdk)
    $env:TERMINAL_APKSIGNER_KS_PASS = $ksPassword
    $env:TERMINAL_APKSIGNER_KEY_PASS = $keyPassword
    Invoke-Native $apksigner @('sign', '--ks', $keystorePath, '--ks-key-alias', $KeyAlias,
        '--ks-pass', 'env:TERMINAL_APKSIGNER_KS_PASS', '--key-pass', 'env:TERMINAL_APKSIGNER_KEY_PASS',
        '--out', $signedCandidate, $aligned) | Out-Null
    Invoke-Native $zipalign @('-c', '-p', '4', $signedCandidate) | Out-Null
    $signatureReport = Invoke-Native $apksigner @('verify', '--verbose', '--print-certs', $signedCandidate)
    $certificateLine = $signatureReport | Where-Object { $_ -match '^Signer #1 certificate SHA-256 digest: ([0-9a-fA-F]+)$' } | Select-Object -First 1
    if ($certificateLine -notmatch '^Signer #1 certificate SHA-256 digest: ([0-9a-fA-F]+)$') {
        throw 'Signature verification did not report a signer certificate SHA-256 digest.'
    }
    $certificateHash = $Matches[1].ToLowerInvariant()
    $finalPayload = Get-ZipPayload $signedCandidate
    foreach ($name in $hydratedPayload.Keys) {
        if (-not $finalPayload.ContainsKey($name) -or $finalPayload[$name] -ne $hydratedPayload[$name]) {
            throw "Alignment or signing mutated APK payload '$name'."
        }
    }
    $unexpectedSignedEntries = @($finalPayload.Keys | Where-Object {
        -not $hydratedPayload.ContainsKey($_) -and $_ -notmatch '^META-INF/(MANIFEST\.MF|.+\.(SF|RSA|DSA|EC))$'
    })
    if ($unexpectedSignedEntries.Count -ne 0) { throw "Signing introduced unexpected APK payload: $($unexpectedSignedEntries -join ', ')" }
    $finalHash = Get-Sha256 $signedCandidate
    Write-Receipt $receiptCandidate ([pscustomobject]@{
        ReleaseId = $parsedRecipe.Id; BaseIdentity = $BaseIdentity; PackageId = $packageId
        BaseHash = $baseHash; RecipeHash = $recipeHash; Hardpoints = $hardpoints
        SigningProfile = $SigningProfile; CertificateHash = $certificateHash
        Artifact = $artifact; FinalHash = $finalHash
    })
    [IO.File]::Move($signedCandidate, $artifact, $true)
    [IO.File]::Move($receiptCandidate, $receipt, $true)
}
finally {
    $env:JAVA_HOME = $oldJavaHome
    $env:TERMINAL_APKSIGNER_KS_PASS = $oldKsPass
    $env:TERMINAL_APKSIGNER_KEY_PASS = $oldKeyPass
    if ([IO.Directory]::Exists($temporary)) { [IO.Directory]::Delete($temporary, $true) }
}

Write-Host "Release APK: $artifact"
Write-Host "Receipt:    $receipt"
[pscustomobject]@{ Artifact = $artifact; Receipt = $receipt; Sha256 = $finalHash }
