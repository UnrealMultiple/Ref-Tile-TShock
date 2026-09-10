<p align="center">
  <img src="https://tshock.s3.us-west-001.backblazeb2.com/newlogo.png" alt="Ref-Tile-TShock"><br />
  <a href="https://github.com/UnrealMultiple/Ref-Tile-TShock/actions/workflows/ci-otapi3.yml">
    <img src="https://github.com/UnrealMultiple/Ref-Tile-TShock/actions/workflows/ci-otapi3.yml/badge.svg" alt="GitHub Actions Build Status">
  </a>
  <a href="https://github.com/UnrealMultiple/Ref-Tile-TShock/blob/general-devel/README_cn.md">中文版</a>
</p>

# Ref-Tile-TShock

**Ref-Tile-TShock is a fork of [TShock](https://github.com/Pryaxis/TShock)** built on the Ref-Tile ecosystem: it uses [OTAPI.RefTile](https://www.nuget.org/packages/OTAPI.RefTile) and [Ref-Tile-TSAPI](https://github.com/UnrealMultiple/Ref-Tile-TSAPI) as its foundation, keeps all of TShock's features (anti-cheat, server-side characters, groups, permissions, item bans, tons of commands…), and dramatically reduces memory usage thanks to the **Ref Struct Tile** mode.

## Key advantages

### 🚀 Ref Struct Tile: dramatically lower memory

Vanilla Terraria stores every tile as a **heap-allocated reference class**; the Ref Struct Tile mode used here rewrites it as a **16-byte value-type struct** `Terraria.TileData` stored in contiguous memory:

| | Vanilla / traditional OTAPI | Ref-Tile (this project) |
|---|---|---|
| Tile representation | `Terraria.Tile` (reference class, one heap object per tile) | `Terraria.TileData` (16-byte struct, inline storage) |
| World storage | `Tile[,]` — array of object references | `TileCollection` — contiguous `TileData` storage |
| Tile access | `Main.tile[x, y]` returns a heap object reference | returns `ref TileData` (a managed pointer into the storage) |

- **Hundreds of MB saved on large worlds**: for a world around 8400 × 2400 (~20 million tiles), storage drops from **over 1 GB of scattered heap objects to roughly 320 MB of contiguous struct data**.
- **Per-tile GC pressure is gone**: no more one GC-tracked heap object per tile, so world generation, chunk streaming, and in-game edits stop producing massive amounts of garbage.
- **Cache-friendly**: contiguous memory turns tile traversal into sequential reads, benefiting lighting, liquid simulation, and map rendering.
- **Zero-copy in-place edits**: `Main.tile[x, y].type = …` writes straight into the storage slot; hot paths should use `ref TileData tile = ref Main.tile[x, y];`.

> Full technical details: [Ref Struct Tile documentation](https://github.com/UnrealMultiple/Ref-Tile-Open-Terraria-API/blob/upcoming/docs/ref-struct-tile.zh-CN.md).

### ⚙️ All of TShock's features

Anti-cheat, server-side characters, groups & permissions, item bans, the command system… everything is identical to upstream TShock, and the plugin API stays compatible — existing TShock plugins build and run as-is.

### 📦 NuGet package

- [`Ref-Tile-TShock`](https://www.nuget.org/packages/Ref-Tile-TShock) — the TShock API package on nuget.org
- Published automatically via **NuGet trusted publishing** (OIDC, no long-lived API keys)

### 🌍 Multi-platform

`win-x64`, `osx-x64`, `linux-x64`, `linux-arm64`, `linux-arm`, publishable as single-file executables.

## Building from source

Requires the [.NET 9 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/9.0).

```bash
git clone https://github.com/UnrealMultiple/Ref-Tile-TShock.git --recurse-submodules
cd Ref-Tile-TShock
dotnet build
```

Run the server (TShockLauncher):

```bash
dotnet run --project TShockLauncher
```

Produce a packaged release:

```bash
cd TShockLauncher
dotnet publish -r win-x64 -f net9.0 -c Release -p:PublishSingleFile=true --self-contained false
```

The output lands in `TShockLauncher/bin/Release/net9.0/<arch>/`.

## Plugin development

Same as upstream TShock: because of OTAPI, every field in the `Terraria` namespace is public, so plugins can touch the game directly (the equivalent of CraftBukkit's NMS).

Note that this project runs the **Ref Struct Tile** mode, so:

- Tiles are the value type `Terraria.TileData` (no longer the reference class `Terraria.Tile`)
- Hot paths should hold a local reference: `ref TileData tile = ref Main.tile[x, y];`
- Null checks become `tile.IsNull` / `tile.IsNotNull` (structs can't be null)
- To keep a tile reference beyond the current scope (fields, collections, closures, async), wrap it in `RefTileData`

## License

[GPL-3.0-or-later](https://www.gnu.org/licenses/gpl-3.0.html) (same as upstream TShock).
