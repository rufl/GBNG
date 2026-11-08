# Game Builder

![Game Builder](gb-hero.jpg)

An unofficial, community-maintained continuation of Google Game Builder. This is not an officially supported Google product.

The implementations are maintained separately. Choose a branch for its source and current setup instructions:

- **[Unity 6](https://github.com/rufl/GBNG/tree/unity)** — the original Unity project, pinned to Unity 6000.6.6f1. Includes restored editor workflows, a bundled Monaco code editor, and player-hosted multiplayer using NGO/Unity Transport without paid Unity networking services. The Unity branch README documents platform requirements, build steps, current feature coverage, and known limitations.
- **[Godot](https://github.com/rufl/GBNG/tree/godot)** — the GBNG — New Gen rewrite. It loads and saves legacy `.voos` projects and implements native editor and LAN multiplayer workflows. Its README lists current capabilities and the remaining parity gaps.

`master` is the repository landing page; active implementation work and branch-specific instructions live in `unity` and `godot`.

The [`builds/`](https://github.com/rufl/GBNG/tree/unity/builds) directory contains historical binaries, not builds of the current source. See the [Unity README](https://github.com/rufl/GBNG/blob/unity/README.md) for current build instructions and [archive notes](https://github.com/rufl/GBNG/blob/unity/builds/README.txt).
