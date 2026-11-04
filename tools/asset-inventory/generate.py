#!/usr/bin/env python3
"""Rebuild the offline asset inventory: python3 tools/asset-inventory/generate.py"""
from __future__ import annotations

import json
import mimetypes
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
OUT = Path(__file__).resolve().parent
SCOPES = (ROOT / "Assets", ROOT / "third_party")
EXCLUDED_DIRS = {".git", "Library", "Temp", "Obj", "obj", "Build", "Builds", "bin", "dist", "target", "node_modules", "__pycache__"}
IMAGE_EXTS = {".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".svg"}
EVIDENCE_NAMES = {"license", "licence", "notice", "thirdpartynotices"}
SPDX = re.compile(r"SPDX-License-Identifier:\s*([^\s*]+)", re.I)
LICENSE_WORDS = re.compile(r"(?:licensed under|license(?:d)?[: ]+)\s*(the\s+)?((?:Apache|MIT|BSD|GNU|Mozilla|ISC|Unlicense|Zlib|Boost|Creative Commons)[^\n.;]*)", re.I)


def category(path: Path) -> str:
    ext = path.suffix.lower()
    if ext in IMAGE_EXTS:
        return "Images"
    if ext in {".cs", ".js", ".ts", ".jsx", ".tsx", ".py", ".shader", ".cginc", ".hlsl", ".glsl", ".json", ".xml", ".yaml", ".yml", ".toml"}:
        return "Code & data"
    if ext in {".wav", ".mp3", ".ogg", ".flac", ".aiff", ".mp4", ".webm", ".mov"}:
        return "Audio & video"
    if ext in {".fbx", ".obj", ".blend", ".gltf", ".glb", ".dae", ".3ds"}:
        return "3D models"
    if ext in {".fnt", ".font", ".ttf", ".otf", ".woff", ".woff2"}:
        return "Fonts"
    if ext in {".unity", ".prefab", ".asset", ".mat", ".controller", ".anim"}:
        return "Unity assets"
    if ext in {".md", ".txt", ".rst", ".html", ".css"}:
        return "Text & web"
    return "Other files"


def evidence_for(path: Path, root: Path) -> tuple[str, str, str]:
    third_party = "third_party" in path.relative_to(ROOT).parts
    parent = path.parent
    while parent == root or root in parent.parents:
        try:
            candidates = sorted((p for p in parent.iterdir() if p.is_file() and p.stem.lower().replace("_", "").replace("-", "") in EVIDENCE_NAMES), key=lambda p: p.name.lower())
        except OSError:
            candidates = []
        if candidates:
            ev = candidates[0]
            text = ev.read_text(encoding="utf-8", errors="replace")[:12000]
            spdx = SPDX.search(text)
            title = re.search(r"(?im)^\s*(?:the )?([A-Za-z0-9 .+()-]*license(?: agreement)?|Apache License(?:, Version [0-9.]+)?|MIT License|BSD [0-9-]+-Clause License)\s*$", text)
            label = spdx.group(1) if spdx else (title.group(1).strip() if title else "License evidence (see file)")
            return label, ev.relative_to(ROOT).as_posix(), f"Files in {parent.relative_to(ROOT).as_posix() or '.'} and descendants"
        if parent == root:
            break
        parent = parent.parent
    if path.suffix.lower() in {".cs", ".js", ".ts", ".py", ".h", ".hpp", ".c", ".cpp", ".shader", ".css"}:
        try:
            head = path.read_text(encoding="utf-8", errors="replace")[:5000]
        except OSError:
            head = ""
        spdx = SPDX.search(head)
        explicit = LICENSE_WORDS.search(head)
        if spdx or explicit:
            label = spdx.group(1) if spdx else explicit.group(2).strip()
            return label, path.relative_to(ROOT).as_posix(), "Explicit license statement in source header"
    if not third_party and (ROOT / "LICENSE").is_file():
        return "Apache-2.0", "LICENSE", "Project-owned files outside third-party trees"
    return "Unidentified", "", "No license evidence found"


def main() -> None:
    mimetypes.init()
    items = []
    for scope in SCOPES:
        if not scope.exists():
            continue
        for path in sorted(scope.rglob("*"), key=lambda p: p.as_posix().lower()):
            if not path.is_file() or path.name.endswith(".meta") or any(part in EXCLUDED_DIRS for part in path.relative_to(scope).parts):
                continue
            rel = path.relative_to(ROOT).as_posix()
            label, evidence, evidence_scope = evidence_for(path, scope)
            ext = path.suffix.lower()
            mime = mimetypes.guess_type(path.name)[0] or ""
            preview = rel if ext in IMAGE_EXTS and mime.startswith("image/") else ""
            items.append({"path": rel, "name": path.name, "type": category(path), "extension": ext or "(no extension)", "bytes": path.stat().st_size, "mime": mime, "preview": preview, "license": label, "evidence": evidence, "evidenceScope": evidence_scope})
    items.sort(key=lambda item: item["path"].casefold())
    (OUT / "catalog.json").write_text(json.dumps({"assets": items}, ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")
    print(f"Wrote {len(items)} assets to tools/asset-inventory/catalog.json")


if __name__ == "__main__":
    main()
