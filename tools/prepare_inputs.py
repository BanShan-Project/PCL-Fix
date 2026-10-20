"""Use immutable public upstream inputs; never upload local user files."""
from pathlib import Path
import hashlib, io, json, urllib.parse, urllib.request, zipfile
import dnfile
ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / "build-inputs"
OUT.mkdir(exist_ok=True)
DONOR_COMMIT = "5ef81e490ae46b1100d61e33d0183094c7a80ca8"
DONOR_HASH = "5582952ded3b443503f66c23b810802bd20f1a3e847b54d9e09653a825859a36"
ORIGINAL_HASH = "6b6e1260cb5a1d6a303855f1c60cab3d0bc1aebba7e40a964180188b271e7386"
def unpack(raw, filename, expected):
    with zipfile.ZipFile(io.BytesIO(raw)) as archive:
        names = [name for name in archive.namelist() if name.lower().endswith(".exe")]
        if len(names) != 1: raise RuntimeError("Unexpected upstream archive contents")
        data = archive.read(names[0])
    actual = hashlib.sha256(data).hexdigest()
    if actual != expected: raise RuntimeError(f"Input checksum mismatch: {filename}: {actual}")
    (OUT / filename).write_bytes(data)
    return {"file": filename, "sha256": actual, "size": len(data)}
url = f"https://raw.githubusercontent.com/Meloong-Git/PCL/{DONOR_COMMIT}/" + urllib.parse.quote("最新正式版.zip")
with urllib.request.urlopen(url, timeout=60) as response: donor_zip = response.read()
inputs = [unpack((ROOT / "最新正式版.zip").read_bytes(), "original.exe", ORIGINAL_HASH),
          unpack(donor_zip, "donor-2.14.exe", DONOR_HASH)]
# Cecil needs enum definitions from the original embedded dependencies when
# serializing optional-parameter constants. Parse resources without executing them.
pe = dnfile.dnPE(str(OUT / "original.exe"))
for resource in pe.net.resources:
    for entry in resource.data.entries:
        data = entry.value
        if not isinstance(data, bytes) or not data.startswith(b"MZ"):
            continue
        embedded = dnfile.dnPE(data=data)
        if embedded.net is None:
            continue
        name = str(entry.name) + ".dll"
        if Path(name).name != name:
            raise RuntimeError("Unexpected embedded assembly resource name")
        (OUT / name).write_bytes(data)
(OUT / "inputs.json").write_text(json.dumps(inputs, indent=2), encoding="utf-8")
print(json.dumps(inputs, indent=2))
