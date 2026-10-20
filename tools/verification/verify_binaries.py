#!/usr/bin/env python3
"""Compare ILVerify results without executing either target assembly."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import struct
import subprocess
import sys

import dnfile


def digest(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def is_managed(data: bytes) -> bool:
    try:
        if data[:2] != b"MZ":
            return False
        pe = struct.unpack_from("<I", data, 0x3C)[0]
        if data[pe:pe + 4] != b"PE\0\0":
            return False
        optional = pe + 24
        magic = struct.unpack_from("<H", data, optional)[0]
        if magic not in (0x10B, 0x20B):
            return False
        directories = optional + (96 if magic == 0x10B else 112)
        rva, size = struct.unpack_from("<II", data, directories + 14 * 8)
        return bool(rva and size)
    except (struct.error, IndexError):
        return False


def extract(assembly: Path, directory: Path) -> dict:
    directory.mkdir(parents=True, exist_ok=True)
    pe = dnfile.dnPE(str(assembly))
    assemblies, excluded = {}, []
    try:
        for resource in pe.net.resources:
            data = resource.data
            entries = [(str(resource.name), data)] if isinstance(data, bytes) else [
                (str(entry.name), entry.value) for entry in getattr(data, "entries", [])
            ]
            for name, value in entries:
                if not isinstance(value, bytes) or not value.startswith(b"MZ"):
                    continue
                if not is_managed(value):
                    excluded.append(name)
                    continue
                embedded = dnfile.dnPE(data=value)
                try:
                    identity = str(embedded.net.mdtables.Assembly.rows[0].Name)
                finally:
                    embedded.close()
                if not re.fullmatch(r"[A-Za-z0-9_.-]+", identity):
                    raise ValueError(f"Unsafe embedded assembly name: {identity!r}")
                key = identity.casefold()
                sha = digest(value)
                if key in assemblies and assemblies[key]["sha256"] != sha:
                    raise ValueError(f"Conflicting embedded assemblies: {identity}")
                path = directory / (identity + ".dll")
                path.write_bytes(value)
                assemblies[key] = {"path": str(path), "sha256": sha, "resource": name}
    finally:
        pe.close()
    if not assemblies:
        raise ValueError(f"No embedded managed dependencies found in {assembly}")
    return {"assemblies": assemblies, "excluded_native_resources": excluded}


def framework_directory(explicit: str | None) -> Path:
    candidates = [Path(explicit)] if explicit else []
    for variable in ("ProgramFiles(x86)", "ProgramFiles"):
        if os.environ.get(variable):
            candidates.append(Path(os.environ[variable]) / "Reference Assemblies/Microsoft/Framework/.NETFramework/v4.8")
    for path in candidates:
        if all((path / name).is_file() for name in (
            "mscorlib.dll", "PresentationFramework.dll", "PresentationCore.dll", "WindowsBase.dll", "Microsoft.VisualBasic.dll"
        )):
            return path.resolve()
    raise ValueError(".NET Framework 4.8 reference assemblies with WPF were not found; pass --framework-dir")


def changed_method_names(assembly: Path, changes: list[dict]) -> list[dict]:
    pe = dnfile.dnPE(str(assembly))
    owners = {}
    try:
        for typedef in pe.net.mdtables.TypeDef.rows:
            for method in typedef.MethodList:
                owners[method.row_index] = str(typedef.TypeName)
        result = []
        for change in changes:
            token = int(change["token"], 0)
            if token >> 24 != 6:
                raise ValueError(f"Invalid changed method token: {change['token']}")
            index = token & 0xFFFFFF
            method = pe.net.mdtables.MethodDef.rows[index - 1]
            name = str(method.Name)
            owner = owners[index]
            if f"::{name}(" not in change["method"]:
                raise ValueError(f"Patch report method/token mismatch: {change['method']}")
            result.append({"token": change["token"], "method": change["method"], "owner": owner, "name": name})
        return result
    finally:
        pe.close()


def run_verify(command: list[str], assembly: Path, references: list[Path], output: Path) -> dict:
    args = [str(assembly), "-s", "mscorlib", "--statistics", "-t"]
    for reference in references:
        args += ["-r", str(reference)]
    # Hundreds of full Windows reference paths exceed CreateProcess's 32K limit.
    # System.CommandLine (used by ILVerify 8) supports quoted response files.
    if any('"' in arg or "\n" in arg or "\r" in arg for arg in args):
        raise ValueError("Unsupported quote/newline in verification path")
    response = output.with_suffix(".rsp")
    response.write_text("\n".join('"' + arg + '"' for arg in args) + "\n", encoding="utf-8")
    completed = subprocess.run(command + ["@" + str(response)], text=True, encoding="utf-8", errors="replace", stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    output.write_text(completed.stdout, encoding="utf-8")
    normalized = completed.stdout.replace(str(assembly), "<TARGET>")
    errors = sorted(set(line.strip() for line in normalized.splitlines() if "Error [" in line))
    statistics = {key: int(value) for key, value in re.findall(r"((?:Types|Methods) (?:found|verified)):\s*(\d+)", normalized)}
    if completed.returncode not in (0, 2) or "Methods verified" not in statistics or not statistics["Methods verified"]:
        raise ValueError(f"ILVerify did not complete for {assembly}; see {output}")
    if completed.returncode and not errors:
        raise ValueError(f"Unrecognized ILVerify failure for {assembly}; see {output}")
    return {"exit_code": completed.returncode, "errors": errors, "statistics": statistics}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--original", required=True, type=Path)
    parser.add_argument("--patched", "--restored", dest="restored", required=True, type=Path)
    parser.add_argument("--patch-report", type=Path, help="Default: RESTORED.patch.json")
    parser.add_argument("--output", "--output-dir", dest="output_dir", required=True, type=Path)
    parser.add_argument("--framework-dir", help="Auto-detects installed Windows v4.8 targeting pack")
    parser.add_argument("--ilverify", default="ilverify", help="ILVerify app/shim path, or ILVerify.dll")
    parser.add_argument("--dotnet", default="dotnet", help="Host used only if --ilverify ends in .dll")
    args = parser.parse_args()
    original, restored = args.original.resolve(), args.restored.resolve()
    output = args.output_dir.resolve()
    output.mkdir(parents=True, exist_ok=True)
    report_path = args.patch_report or Path(str(restored) + ".patch.json")
    patch = json.loads(report_path.read_text(encoding="utf-8"))
    if digest(original.read_bytes()) != patch["original"] or digest(restored.read_bytes()) != patch["output"]:
        raise ValueError("Patch report SHA256 does not match input assemblies")
    if not patch.get("changedMethods"):
        raise ValueError("Patch report contains no changed methods")
    original_dependencies = extract(original, output / "original-dependencies")
    restored_dependencies = extract(restored, output / "restored-dependencies")
    original_hashes = {key: value["sha256"] for key, value in original_dependencies["assemblies"].items()}
    restored_hashes = {key: value["sha256"] for key, value in restored_dependencies["assemblies"].items()}
    if original_hashes != restored_hashes:
        raise ValueError("Embedded managed dependencies changed; common-reference baseline is invalid")
    framework = framework_directory(args.framework_dir)
    references = {}
    excluded = []
    for path in list(framework.glob("*.dll")) + list((framework / "Facades").glob("*.dll")):
        if is_managed(path.read_bytes()):
            references[path.stem.casefold()] = path
        else:
            excluded.append(str(path))
    for key, value in original_dependencies["assemblies"].items():
        references[key] = Path(value["path"])
    reference_paths = [references[key] for key in sorted(references)]
    command = [args.dotnet, args.ilverify] if args.ilverify.lower().endswith(".dll") else [args.ilverify]
    baseline = run_verify(command, original, reference_paths, output / "original-ilverify.log")
    final = run_verify(command, restored, reference_paths, output / "restored-ilverify.log")
    new_errors = sorted(set(final["errors"]) - set(baseline["errors"]))
    changed = changed_method_names(restored, patch["changedMethods"])
    changed_errors = []
    for method in changed:
        # ILVerify prints nested declaring types using their simple metadata name.
        needle = method["owner"] + "::" + method["name"] + "("
        matches = [line for line in final["errors"] if needle in line]
        method["errors"] = matches
        changed_errors.extend(matches)
    stats_complete = all(run["statistics"].get("Methods found") == run["statistics"].get("Methods verified") and
                         run["statistics"].get("Types found") == run["statistics"].get("Types verified")
                         for run in (baseline, final))
    success = not new_errors and not changed_errors and stats_complete
    summary = {
        "passed": success, "scope": "Static metadata/IL validation; target programs were not executed",
        "framework_directory": str(framework), "reference_count": len(reference_paths),
        "reference_paths": [str(path) for path in reference_paths], "excluded_native_references": excluded,
        "original_dependencies": original_dependencies, "restored_dependencies": restored_dependencies,
        "baseline": baseline, "restored": final, "new_errors": new_errors,
        "changed_methods": changed, "all_methods_and_types_checked": stats_complete,
    }
    (output / "verification-summary.json").write_text(json.dumps(summary, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"IL verification {'PASSED' if success else 'FAILED'}: {len(reference_paths)} references; "
          f"{len(baseline['errors'])} baseline errors; {len(new_errors)} new errors; "
          f"{len(set(changed_errors))} errors in {len(changed)} changed methods")
    return 0 if success else 1


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (ValueError, OSError, KeyError, IndexError) as error:
        print(f"Verification failed: {error}", file=sys.stderr)
        raise SystemExit(1)
