"""Inject optional fork-owned settings without logging credential values."""
from pathlib import Path
import json, os
root = Path(__file__).resolve().parents[1]
module = root / "Plain Craft Launcher 2/Modules/ModSecret.vb"
source = module.read_text(encoding="utf-8-sig")
configured = {}
for runtime_key, build_key in [("PCL_MS_CLIENT_ID", "PCL_BUILD_CLIENT_ID"), ("PCL_CURSEFORGE_API_KEY", "PCL_BUILD_CURSEFORGE_API_KEY")]:
    value = os.environ.get(build_key, "")
    configured[runtime_key] = bool(value)
    if value:
        if any(c in value for c in (chr(13),chr(10),chr(0))): raise ValueError("Multiline build setting")
        old = f'If(Environment.GetEnvironmentVariable("{runtime_key}"), "")'
        escaped = value.replace(chr(34), chr(34) * 2)
        new = f'If(Environment.GetEnvironmentVariable("{runtime_key}"), "{escaped}")'
        if source.count(old) != 1: raise RuntimeError("Credential injection anchor changed")
        source = source.replace(old, new)
module.write_text(source, encoding="utf-8-sig")
base = root / "Plain Craft Launcher 2/Modules/Base/ModBase.vb"
text = base.read_text(encoding="utf-8-sig")
sha = os.environ.get("GITHUB_SHA", "")
if sha:
    if len(sha) != 40 or any(c not in "0123456789abcdef" for c in sha): raise ValueError("Invalid commit SHA")
    text = text.replace('Public Const CommitHash As String = ""', f'Public Const CommitHash As String = "{sha}"')
base.write_text(text, encoding="utf-8-sig")
out = root / "artifacts"
out.mkdir(exist_ok=True)
(out / "source-build-settings.json").write_text(json.dumps({"configured": configured, "commit": sha}, indent=2), encoding="utf-8")
print("Prepared source build; credential values are not logged.")
