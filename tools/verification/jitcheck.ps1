<#
    JIT acceptance check for the restored launcher.

    ILVerify is a verifier: it checks types, stack shapes and exception-region shape, but it never
    asks the runtime's importer to accept a method body, and it ignores the header's MaxStackSize.
    A body can therefore verify cleanly and still throw System.InvalidProgramException the first
    time it is compiled. The 2.99 restoration shipped exactly that: the modpack dispatcher was left
    with a try block falling through into its finally handler, and it only surfaced when a user
    installed a modpack (JIT is lazy).

    This script loads the patched EXE, extracts the managed dependencies it embeds, asks the CLR to
    compile every method named in the patch report via RuntimeHelpers.PrepareMethod, and fails on
    InvalidProgramException. Methods are compiled, never executed.

    Run under Windows PowerShell 5.1 (powershell.exe). The payload targets .NET Framework 4.8 and
    cannot be loaded by PowerShell 7 or any .NET (Core) host.

    Example:
        powershell -File tools/verification/jitcheck.ps1 `
            -Executable artifacts/PCL-2.99-restored.exe `
            -PatchReport artifacts/PCL-2.99-restored.exe.patch.json `
            -OutputDirectory jitcheck
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Executable,
    [Parameter(Mandatory = $true)][string]$PatchReport,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [string]$ReferenceDirectory
)
$ErrorActionPreference = 'Stop'

$exe = (Resolve-Path -LiteralPath $Executable).Path
$reportPath = (Resolve-Path -LiteralPath $PatchReport).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $output | Out-Null
$report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
if (-not $report.changedMethods) { throw 'Patch report contains no changedMethods; refusing to report success' }

$refs = if ($ReferenceDirectory) {
    (Resolve-Path -LiteralPath $ReferenceDirectory).Path
} else {
    Join-Path $output 'references'
}

$payload = [IO.File]::ReadAllBytes($exe)
$sha256 = [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($payload)).Replace('-', '').ToLowerInvariant()

# Assemblies loaded from bytes are not on any probe path, so hand the CLR the copies embedded in
# PCL.Resources.resources (or the ones extracted earlier by verify_binaries.py).
$cache = @{}
$resolver = [System.ResolveEventHandler] {
    param($sender, $args)
    try {
        $simple = ($args.Name -split ',')[0]
        if ($cache.ContainsKey($simple)) { return $cache[$simple] }
        $path = Join-Path $refs ($simple + '.dll')
        if (-not (Test-Path -LiteralPath $path)) { return $null }
        $loaded = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($path))
        $cache[$simple] = $loaded
        return $loaded
    }
    catch { return $null }
}
[AppDomain]::CurrentDomain.add_AssemblyResolve($resolver)

$assembly = [Reflection.Assembly]::Load($payload)
if (-not (Test-Path -LiteralPath $refs)) {
    New-Item -ItemType Directory -Force -Path $refs | Out-Null
    $stream = $assembly.GetManifestResourceStream('PCL.Resources.resources')
    if (-not $stream) { throw 'PCL.Resources.resources not found; pass -ReferenceDirectory' }
    $memory = New-Object IO.MemoryStream
    $stream.CopyTo($memory); $stream.Close(); $memory.Position = 0
    $reader = New-Object System.Resources.ResourceReader($memory)
    $enumerator = $reader.GetEnumerator()
    $extracted = 0
    while ($enumerator.MoveNext()) {
        $value = $enumerator.Value
        if ($value -is [byte[]] -and $value.Length -gt 2 -and $value[0] -eq 0x4D -and $value[1] -eq 0x5A) {
            [IO.File]::WriteAllBytes((Join-Path $refs ([string]$enumerator.Key + '.dll')), $value)
            $extracted++
        }
    }
    Write-Verbose "Extracted $extracted embedded assemblies to $refs"
}

$flags = [Reflection.BindingFlags]'Public,NonPublic,Instance,Static,DeclaredOnly'
$byToken = @{}
$types = @()
try { $types = @($assembly.GetTypes()) }
catch [Reflection.ReflectionTypeLoadException] { $types = @($_.Exception.Types | Where-Object { $null -ne $_ }) }
foreach ($type in $types) {
    if ($null -eq $type) { continue }
    foreach ($method in @($type.GetMethods($flags))) {
        if (-not $byToken.ContainsKey($method.MetadataToken)) { $byToken[$method.MetadataToken] = $method }
    }
}

$rows = @(); $failures = @(); $skipped = @()
foreach ($change in $report.changedMethods) {
    $token = if ($change.token -is [int]) { $change.token } else { [Convert]::ToInt32(([string]$change.token).Substring(2), 16) }
    $method = $byToken[$token]
    $row = [ordered]@{
        token = ('0x{0:x8}' -f $token); originalToken = $change.originalToken; method = $change.method
        result = 'OK'; detail = $null
    }
    if (-not $method) { $row.result = 'MISSING' }
    elseif ($method.DeclaringType.IsGenericTypeDefinition) { $row.result = 'SKIP'; $row.detail = 'open generic declaring type' }
    else {
        try { [System.Runtime.CompilerServices.RuntimeHelpers]::PrepareMethod($method.MethodHandle) }
        catch {
            $inner = $_.Exception.InnerException
            if ($null -eq $inner) { $inner = $_.Exception }
            $row.result = if ($inner -is [System.InvalidProgramException]) { 'INVALID IL' } else { 'ERROR' }
            $row.detail = $inner.GetType().Name + ': ' + $inner.Message
        }
    }
    if ($row.result -in @('INVALID IL', 'ERROR', 'MISSING')) { $failures += $row }
    elseif ($row.result -eq 'SKIP') { $skipped += $row }
    $rows += $row
}

$summary = [ordered]@{
    passed = ($failures.Count -eq 0)
    scope = 'JIT acceptance of the methods listed in the patch report; the target program was not executed'
    executable = $exe; sha256 = $sha256; patch_report = $reportPath
    reference_directory = $refs
    checked = $rows.Count; failed = $failures.Count; skipped = $skipped.Count
    failures = @($failures); methods = @($rows)
}
$summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'jitcheck.json') -Encoding utf8

$rows | ForEach-Object { '{0}  {1,-11} {2}' -f $_.token, $_.result, $_.method }
'JIT check {0}: {1} method(s), {2} failure(s), {3} skipped' -f $(if ($failures.Count -eq 0) { 'PASSED' } else { 'FAILED' }), $rows.Count, $failures.Count, $skipped.Count
if ($failures.Count -ne 0) { exit 1 }
