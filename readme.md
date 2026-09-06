# TxtgSharp

A C# reader for the `TexToGo` (`.txtg`) texture container used by *Tears of the Kingdom*.

Surfaces come out **deswizzled but still block compressed**, so callers can hand them
straight to a compressed GPU upload where the format is supported, or decode them on the
CPU where it is not. The library itself depends only on `ZstdSharp.Port`.

## Build

```bash
dotnet build TxtgSharp.sln -c Release
```

## Use

```csharp
TxtgFile txtg = TxtgFile.FromFile("MaterialAlb.txtg");

Console.WriteLine($"{txtg.Width}x{txtg.Height}, {txtg.LayerCount} layers, {txtg.MipCount} mips");
Console.WriteLine($"{txtg.Format}");                  // e.g. Astc8x8Unorm

foreach (TxtgSurface s in txtg.LayersOfMip(0))        // ordered by array layer
{
    // s.Data is deswizzled, still in s-format blocks
    Upload(s.ArrayIndex, s.Width, s.Height, s.Data);
}
```

`LayersOfMip(mip)` returns the layers of one mip level in array order, which is the shape a
`GL_TEXTURE_2D_ARRAY` upload wants.

## CLI

```bash
# header, format and per-layer size checks
TxtgSharp.Cli info MaterialAlb.txtg

# decode ASTC layers to PNG, with per-channel statistics
TxtgSharp.Cli dump MaterialAlb.txtg ./out 0 5 97
```

`dump` reports per-channel means and ranges, which is enough to identify what a texture
holds without trusting any shader — a two-channel tangent normal map sits at 0.500 in R and
G, while albedo does not.

## Format

### Header

All little endian. The header is `0x50` bytes and the field offsets below sum exactly to
that.

| Offset | Type      | Description                                       |
|--------|-----------|---------------------------------------------------|
| `0x00` | u16       | Header size (`0x50`)                              |
| `0x02` | u16       | Version (`0x11`)                                  |
| `0x04` | char[4]   | Magic, `6PK0`                                     |
| `0x08` | u16       | Width                                             |
| `0x0A` | u16       | Height                                            |
| `0x0C` | u16       | Array layer count                                 |
| `0x0E` | u8        | Mip count                                         |
| `0x0F` | u8        | Unknown                                           |
| `0x10` | u8        | Unknown                                           |
| `0x11` | u16       | Padding                                           |
| `0x13` | u8        | Format flag                                       |
| `0x14` | u32       | Format setting                                    |
| `0x18` | u8[4]     | Component select R, G, B, A                       |
| `0x1C` | u8[32]    | Hash                                              |
| `0x3C` | u16       | Format code (see below)                           |
| `0x3E` | u16       | Unknown                                           |
| `0x40` | u32[4]    | Texture settings 1-4                              |

### Surface tables

Two tables of `layerCount * mipCount` entries follow the header, then the payloads back to
back. Each payload is one zstd-compressed, swizzled surface.

| Table   | Entry | Fields                                            |
|---------|-------|---------------------------------------------------|
| Index   | 4 B   | u16 array index, u8 mip level, u8 surface count    |
| Sizes   | 8 B   | u32 compressed size, u32 constant                 |

### Format codes

| Code    | Format          |
|---------|-----------------|
| `0x101` | ASTC 8x5 UNORM  |
| `0x102` | ASTC 8x8 UNORM  |
| `0x105` | ASTC 8x8 sRGB   |
| `0x109` | ASTC 4x4 sRGB   |
| `0x10A` | ASTC 4x4 UNORM  |
| `0x202` | BC1 UNORM       |
| `0x203` | BC1 UNORM sRGB  |
| `0x302` | BC1 UNORM       |
| `0x505` | BC3 UNORM sRGB  |
| `0x602` `0x606` `0x607` | BC4 UNORM |
| `0x702` `0x703` `0x707` | BC5 UNORM |
| `0x901` | BC7 UNORM       |

Two things worth knowing:

- **The declared code does not always give the ASTC block size.** Texture setting 2 overrides
  it: `32628` selects 8x5 and `32631` selects 8x8. The terrain arrays declare `0x101` (8x5)
  but are really **8x8**, which the setting resolves.
- **`0x10A` is ASTC 4x4 UNORM**, established from block geometry rather than assumed:
  `MaterialWeightsDetailTexture.txtg` is 256x256, which at 4x4 blocks is 64x64 x 16 bytes =
  65,536 — exactly its payload size.

### Swizzling

Surfaces are stored in Tegra block-linear order. Texels are grouped into 64x8-byte GOBs; a
block stacks `blockHeight` GOBs vertically, and blocks run in column-major order across the
image. `blockHeight` is a power of two derived from the surface height in blocks, capped at
16.

## Terrain arrays

The material arrays the cave and Depths terrain sample from:

| File | Size | Contents |
|---|---|---|
| `MaterialAlb.txtg` | 1024x1024, 121 layers, 11 mips | Albedo |
| `MaterialCmb.txtg` | 512x512, 121 layers, 10 mips | Two-channel tangent normal in R,G; B is a separate low-valued scalar |
| `MaterialWeightsDetailTexture.txtg` | 256x256, 1 layer, 9 mips | Four independent full-range channels |

All three are ASTC 8x8 except the weights map, which is ASTC 4x4.

## Licence

AGPL-3.0-or-later, as the upstream C++ implementation this was ported from.
