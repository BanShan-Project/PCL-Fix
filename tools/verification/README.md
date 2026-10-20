# Verification

These scripts distinguish static IL checking from a Windows startup smoke check.
Neither check proves game downloads or all UI actions work.

## Static verification

Install `dnfile` and ILVerify 8.0.0, then run from PowerShell on a Windows runner:

```powershell
python -m pip install dnfile
dotnet tool install dotnet-ilverify --version 8.0.0 --tool-path .tools
python tools/verification/verify_binaries.py `
  --original inputs/latest.exe `
  --patched artifacts/PCL-2.99-restored.exe `
  --output artifacts/verification `
  --ilverify .tools/ilverify.exe
```

The default patch report is `RESTORED.patch.json`; override with `--patch-report`.
The installed Windows .NET Framework 4.8 targeting pack is detected automatically;
`--framework-dir PATH` supports an extracted reference-assemblies NuGet package.
`--ilverify PATH/ILVerify.dll --dotnet PATH/dotnet` supports a DLL-based tool install.

The script extracts embedded managed dependencies from both EXEs, checks that
their hashes match, excludes native DLLs, and uses one identical reference set
for both checks. It rejects incomplete verification, additional normalized IL
errors, or any error in a method identified by `changedMethods` in the patch
report. Input hashes must match the patch report. Logs, dependency hashes, and
results are written to the output directory. The original sample has one existing
generic-delegate assignment verification error outside the changed methods.

## JIT acceptance

```powershell
powershell -File tools/verification/jitcheck.ps1 `
  -Executable artifacts/PCL-2.99-restored.exe `
  -PatchReport artifacts/PCL-2.99-restored.exe.patch.json `
  -OutputDirectory jitcheck
```

ILVerify is a verifier: it checks types, stack shapes and exception-region shape, but it never asks
the runtime's importer to accept a body, and it ignores the method header's `MaxStackSize`. A body
can verify cleanly and still throw `System.InvalidProgramException` the first time it is compiled,
because JIT is lazy — the 2.99 restoration shipped exactly that defect: the modpack dispatcher was
left with a `try` block falling through into its `finally` handler, and it only surfaced when a
user installed a modpack.

This step loads the patched EXE, extracts the managed dependencies it embeds into
`<OutputDirectory>/references` (override with `-ReferenceDirectory`), and asks the CLR to compile
every method named in the patch report through `RuntimeHelpers.PrepareMethod`. Methods are compiled,
never executed; `jitcheck.json` records the result per method and the step fails on
`InvalidProgramException`. It must run under Windows PowerShell 5.1 (`powershell.exe`): the payload
targets .NET Framework 4.8 and cannot be loaded by PowerShell 7.

## Windows startup smoke

```powershell
pwsh -File tools/verification/smoke.ps1 `
  -Executable artifacts/PCL-2.99-restored.exe `
  -OutputDirectory artifacts/startup-smoke `
  -WorkRoot $env:GITHUB_WORKSPACE
```

The launcher is copied to a unique clean folder and gets separate cache and
application-data environment paths. The EXE itself must remain outside the
system temporary directory, which PCL treats as an unextracted archive. Use a
fresh GitHub-hosted Windows runner; existing per-user registry settings on a
developer PC are not reset. No credentials or game files are supplied.

The script waits for a titled window to remain present for ten seconds, records
the actual title, attempts a screenshot, and copies generated logs/configuration.
It does not click first-run prompts, sign in, select a game, or start downloads.
A first-run prompt can therefore be the observed startup window. The process is
closed after observation. `startup-smoke.json` states this limited scope; screenshot
failure is reported separately because runner desktop capture may be unavailable.
