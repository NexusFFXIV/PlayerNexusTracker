[CmdletBinding()]
param(
    [string] $ResourceDirectory = (Join-Path (Join-Path $PSScriptRoot '..') 'PlayerNexusTracker.Plugin/Resources')
)

$ErrorActionPreference = 'Stop'

function Read-ResourceKeys([string] $Path) {
    [xml] $xml = Get-Content -LiteralPath $Path -Raw
    $data = @($xml.root.data)
    $keys = [System.Collections.Generic.Dictionary[string,string]]::new([System.StringComparer]::Ordinal)
    foreach ($entry in $data) {
        if ([string]::IsNullOrWhiteSpace($entry.name)) { continue }
        if ($keys.ContainsKey($entry.name)) { throw "Duplicate resource key '$($entry.name)' in $Path" }
        $keys[$entry.name] = [string]$entry.value
    }
    return $keys
}

$base = Read-ResourceKeys (Join-Path $ResourceDirectory 'Language.resx')
$german = Read-ResourceKeys (Join-Path $ResourceDirectory 'Language.de.resx')

$missingGerman = @($base.Keys | Where-Object { -not $german.ContainsKey($_) })
$orphanGerman = @($german.Keys | Where-Object { -not $base.ContainsKey($_) })
$emptyBase = @($base.Keys | Where-Object { [string]::IsNullOrWhiteSpace($base[$_]) })

if ($missingGerman.Count -or $orphanGerman.Count -or $emptyBase.Count) {
    if ($missingGerman.Count) { Write-Error "German resource keys missing: $($missingGerman -join ', ')" }
    if ($orphanGerman.Count) { Write-Error "German resource keys without English base: $($orphanGerman -join ', ')" }
    if ($emptyBase.Count) { Write-Error "English resource values empty: $($emptyBase -join ', ')" }
    exit 1
}

Write-Host "Localization resources valid: $($base.Count) keys present in English and German."
