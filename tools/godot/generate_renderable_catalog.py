#!/usr/bin/env python3
"""Generate Godot resource paths for Game Builder's Unity built-in renderables."""

from __future__ import annotations

import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
ASSETS = ROOT / "Assets"
OUTPUT = ROOT / "Godot" / "Generated" / "renderables.json"
GUID_RE = re.compile(r"^guid:\s*([0-9a-fA-F]{32})\s*$", re.MULTILINE)
MESH_GUID_RE = re.compile(r"m_Mesh:\s*\{[^}]*guid:\s*([0-9a-fA-F]{32})")
SPRITE_GUID_RE = re.compile(r"m_Sprite:\s*\{[^}]*guid:\s*([0-9a-fA-F]{32})")
SOURCE_PREFAB_GUID_RE = re.compile(r"m_SourcePrefab:\s*\{[^}]*guid:\s*([0-9a-fA-F]{32})")
MATERIAL_GUID_RE = re.compile(r"\{[^}]*guid:\s*([0-9a-fA-F]{32})")
MODEL_EXTENSIONS = {".fbx", ".gltf", ".glb", ".obj"}
IMAGE_EXTENSIONS = {".png", ".jpg", ".jpeg", ".tga", ".tif", ".tiff", ".psd"}


def read_text(path: Path) -> str:
    return path.read_text(encoding="utf-8", errors="replace")


def build_guid_index() -> dict[str, Path]:
    paths: dict[str, Path] = {}
    for meta_path in ASSETS.rglob("*.meta"):
        match = GUID_RE.search(read_text(meta_path))
        if not match:
            continue
        guid = match.group(1).lower()
        asset_path = Path(str(meta_path)[:-5])
        prior = paths.get(guid)
        if prior is not None and prior != asset_path:
            raise ValueError(f"Duplicate Unity GUID {guid}: {prior} and {asset_path}")
        paths[guid] = asset_path
    return paths


def resource_roots() -> list[Path]:
    roots = sorted(ASSETS.glob("**/Resources"))
    return [root for root in roots if (root / "BuiltinAssets").is_dir()]


def resolve_references(
    prefab: Path,
    guid_index: dict[str, Path],
    prefabs_by_guid: dict[str, Path],
    seen: set[Path] | None = None,
) -> tuple[set[Path], set[Path]]:
    if seen is None:
        seen = set()
    prefab = prefab.resolve()
    if prefab in seen:
        return set(), set()
    seen.add(prefab)

    text = read_text(prefab)
    models: set[Path] = set()
    images: set[Path] = set()

    for guid in MESH_GUID_RE.findall(text):
        asset = guid_index.get(guid.lower())
        if asset and asset.suffix.lower() in MODEL_EXTENSIONS:
            models.add(asset)

    for guid in SPRITE_GUID_RE.findall(text):
        asset = guid_index.get(guid.lower())
        if asset and asset.suffix.lower() in IMAGE_EXTENSIONS:
            images.add(asset)

    for guid in SOURCE_PREFAB_GUID_RE.findall(text):
        nested = prefabs_by_guid.get(guid.lower())
        if nested is None:
            nested_asset = guid_index.get(guid.lower())
            if nested_asset and nested_asset.suffix.lower() in MODEL_EXTENSIONS:
                models.add(nested_asset)
        else:
            nested_models, nested_images = resolve_references(nested, guid_index, prefabs_by_guid, seen)
            models.update(nested_models)
            images.update(nested_images)

    for guid in MATERIAL_GUID_RE.findall(text):
        material_path = guid_index.get(guid.lower())
        if not material_path or material_path.suffix.lower() != ".mat":
            continue
        material_text = read_text(material_path)
        for texture_guid in MATERIAL_GUID_RE.findall(material_text):
            texture_path = guid_index.get(texture_guid.lower())
            if texture_path and texture_path.suffix.lower() in IMAGE_EXTENSIONS:
                images.add(texture_path)

    return models, images


def res_path(path: Path) -> str:
    return "res://" + path.relative_to(ROOT).as_posix()


def main() -> int:
    if not ASSETS.is_dir():
        print(f"Unity asset directory not found: {ASSETS}", file=sys.stderr)
        return 2

    guid_index = build_guid_index()
    prefabs_by_guid = {
        guid: path
        for guid, path in guid_index.items()
        if path.suffix.lower() == ".prefab"
    }

    renderables: dict[str, dict[str, list[str] | str]] = {}
    for resources in resource_roots():
        for prefab in sorted((resources / "BuiltinAssets").rglob("*.prefab")):
            uri = "builtin:" + prefab.relative_to(resources).with_suffix("").as_posix()
            models, images = resolve_references(prefab, guid_index, prefabs_by_guid)
            renderables[uri] = {
                "prefab": res_path(prefab),
                "models": sorted(res_path(path) for path in models),
                "images": sorted(res_path(path) for path in images),
            }

    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    OUTPUT.write_text(
        json.dumps({"formatVersion": 1, "renderables": renderables}, indent=2) + "\n",
        encoding="utf-8",
        newline="\n",
    )

    unresolved = [uri for uri, item in renderables.items() if not item["models"] and not item["images"]]
    print(f"Generated {len(renderables)} built-in renderables at {OUTPUT.relative_to(ROOT)}")
    print(f"Resolved {len(renderables) - len(unresolved)} to at least one source model or image")
    if unresolved:
        print(f"No model/image source found for {len(unresolved)} built-in prefabs", file=sys.stderr)
    for uri in ("builtin:BuiltinAssets/AvatarExplorer", "builtin:BuiltinAssets/BillboardTextPanel"):
        if uri in renderables:
            print(f"{uri}: {renderables[uri]}")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
