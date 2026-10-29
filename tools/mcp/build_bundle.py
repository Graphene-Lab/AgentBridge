#!/usr/bin/env python3
"""Build the AgentBridge MCPB bundle.

The bundle is a plain ZIP (renamed to .mcpb) whose root holds the MCPB manifest plus the
stdio bridge shipped in mcp/. Extension hosts run `node index.js` from the extracted
folder, so a single bundle serves Windows, macOS and Linux (Node.js >= 18).

The archive is deterministic: entries are sorted, timestamps are pinned and permission
bits are fixed, so the same commit plus the same version always produces the same
SHA-256. That is what lets server.json carry a real fileSha256 in git while the release
workflow regenerates the exact same bytes.

Usage:
	python tools/mcp/build_bundle.py --version 26.10.22
	python tools/mcp/build_bundle.py --version 26.10.22 --out dist
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
import zipfile
from pathlib import Path

SEMVER = re.compile(r"^\d+\.\d+\.\d+$")
BUNDLE_NAME = "agentbridge-mcp.mcpb"
FIXED_DATE = (1980, 1, 1, 0, 0, 0)
FILE_MODE = 0o644


def add_file(archive: zipfile.ZipFile, name: str, data: bytes) -> None:
	info = zipfile.ZipInfo(name, date_time=FIXED_DATE)
	info.compress_type = zipfile.ZIP_DEFLATED
	info.external_attr = FILE_MODE << 16
	info.create_system = 3
	archive.writestr(info, data, compress_type=zipfile.ZIP_DEFLATED, compresslevel=9)


def read_normalized(path: Path) -> bytes:
    """Read a text file with LF endings so the bundle does not depend on the checkout."""
    return path.read_bytes().replace(b"\r\n", b"\n")


def build(repo_root: Path, out_dir: Path, version: str) -> Path:
	source = repo_root / "mcp"
	manifest_path = source / "manifest.json"
	entry_point = source / "index.js"
	for required in (manifest_path, entry_point):
		if not required.is_file():
			raise SystemExit(f"missing bundle source: {required}")

	manifest = json.loads(read_normalized(manifest_path).decode("utf-8"))
	manifest["version"] = version
	if manifest.get("server", {}).get("entry_point") != entry_point.name:
		raise SystemExit("manifest server.entry_point must be index.js")

	payload = json.dumps(manifest, indent=2, ensure_ascii=False).encode("utf-8") + b"\n"
	files = {
		"manifest.json": payload,
		"index.js": read_normalized(entry_point),
	}

	license_path = repo_root / "LICENSE.md"
	if license_path.is_file():
		files["LICENSE"] = read_normalized(license_path)

	out_dir.mkdir(parents=True, exist_ok=True)
	bundle = out_dir / BUNDLE_NAME
	with zipfile.ZipFile(bundle, "w") as archive:
		for name in sorted(files):
			add_file(archive, name, files[name])
	return bundle


def main() -> int:
	parser = argparse.ArgumentParser(description="Build the AgentBridge MCPB bundle")
	parser.add_argument("--version", required=True, help="semver release version (X.Y.Z)")
	parser.add_argument("--out", default="dist", help="output directory (default: dist)")
	parser.add_argument("--repo-root", default=None, help="repository root (default: auto)")
	args = parser.parse_args()

	if not SEMVER.match(args.version):
		raise SystemExit(f"--version must be semver X.Y.Z, got {args.version!r}")

	repo_root = Path(args.repo_root).resolve() if args.repo_root else Path(__file__).resolve().parents[2]
	bundle = build(repo_root, (repo_root / args.out).resolve(), args.version)

	digest = hashlib.sha256(bundle.read_bytes()).hexdigest()
	(bundle.parent / f"{bundle.name}.sha256").write_text(
		f"{digest}  {bundle.name}\n", encoding="utf-8", newline="\n"
	)

	print(f"bundle: {bundle}")
	print(f"size:   {bundle.stat().st_size} bytes")
	print(f"sha256: {digest}")
	print(f"version: {args.version}")
	return 0


if __name__ == "__main__":
	sys.exit(main())
