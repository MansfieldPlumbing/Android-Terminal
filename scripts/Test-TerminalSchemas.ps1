[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$surfaceSchema = Join-Path $repository 'schemas\surface-1.xsd'
$hardpointSchema = Join-Path $repository 'schemas\hardpoint-1.xsd'
$hardpoints = Join-Path $repository 'hardpoints'

function Get-RepositoryRelativePath([string]$Path) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    $prefix = $repository + [IO.Path]::DirectorySeparatorChar
    if ($fullPath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        return $fullPath.Substring($prefix.Length)
    }
    return $fullPath
}

function Assert-Schema([string]$DocumentPath, [string]$SchemaPath) {
    $schemaSet = [Xml.Schema.XmlSchemaSet]::new()
    $null = $schemaSet.Add('', $SchemaPath)
    $schemaSet.Compile()

    $settings = [Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $settings.ValidationType = [Xml.ValidationType]::Schema
    $settings.Schemas = $schemaSet
    $failures = [Collections.Generic.List[string]]::new()
    $settings.add_ValidationEventHandler({
        param($sender, $eventArgs)
        $failures.Add($eventArgs.Message)
    })

    $reader = [Xml.XmlReader]::Create($DocumentPath, $settings)
    try { while ($reader.Read()) { } }
    finally { $reader.Dispose() }

    if ($failures.Count -gt 0) {
        throw "$DocumentPath failed $([IO.Path]::GetFileName($SchemaPath)): $($failures -join '; ')"
    }
    Write-Host "PASS  $(Get-RepositoryRelativePath $DocumentPath)"
}

foreach ($directory in Get-ChildItem -LiteralPath $hardpoints -Directory | Sort-Object Name) {
    $manifest = Join-Path $directory.FullName 'manifest.xml'
    Assert-Schema $manifest $hardpointSchema

    [xml]$manifestDocument = Get-Content -LiteralPath $manifest -Raw
    $ui = $manifestDocument.hardpoint.ui.src
    if ([string]::IsNullOrWhiteSpace($ui)) { throw "$manifest has no UI document." }
    Assert-Schema (Join-Path $directory.FullName $ui) $surfaceSchema
}

Write-Host 'Terminal schema contract passed.'
