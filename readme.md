# TxtgSharp

A C# reader and writer for the `TexToGo` (`.txtg`) texture container used by *Tears of the Kingdom*.

Surfaces come out **deswizzled but still block compressed**, so callers can hand them
straight to a compressed GPU upload where the format is supported, or decode them on the
CPU where it is not. The library itself depends only on `ZstdSharp.Port`.

| Project | What it is | Dependencies |
|---|---|---|
| `src/TxtgSharp` | The library: read and write containers | `ZstdSharp.Port` |
| `src/TxtgSharp.Cli` | `txtg-cli`: inspect, decode, convert, verify | `AstcSharp`, `BCnEncoder.Net`, `StbImageSharp` |

Encoders and image decoding belong to the tool, not the library: `TxtgSharp` is the only
project that packs, and it carries one dependency.

## Build

```bash
dotnet build TxtgSharp.sln -c Release
```

## Reading

```csharp
TxtgFile txtg = TxtgFile.FromFile("MaterialAlb.txtg");

Console.WriteLine($"{txtg.Width}x{txtg.Height}, {txtg.LayerCount} layers, {txtg.MipCount} mips");
Console.WriteLine(txtg.FormatName);                   // e.g. "ASTC 8x6 sRGB"

foreach (TxtgSurface s in txtg.LayersOfMip(0))        // ordered by array layer
{
    // s.Data is deswizzled, still in s-format blocks
    Upload(s.ArrayIndex, s.Width, s.Height, s.Data);
}
```

`Format` is an enum whose ASTC members name a footprint the declared format code cannot
actually pin down, so **`BlockInfo` is the authority on block geometry** and `FormatName`
is the string to show a user. The footprint comes from the header's settings word, which
carries `(width - 1) << 4 | (height - 1)` over a constant `0x7F00` — `0x7F33` is 4x4,
`0x7F77` is 8x8. Every ASTC footprint from 4x4 to 12x12 turns up across the game's
containers, several of them under a code that claims otherwise.

## Writing

Edit a container you read and save it. Surfaces you leave alone are written back as the
exact zstd frames they came in as, so **reading a retail file and writing it straight back
reproduces it byte for byte** — all 29,109 `.txtg` the game ships, which is every one of
them: none are packed inside archives and no file carries the magic under another name.
Only the surfaces you replace cost a swizzle and a compression pass.

```csharp
TxtgFile txtg = TxtgFile.FromFile("MaterialAlb.txtg");

TxtgSurface surface = txtg.Surface(layer: 0, mip: 0)!;
surface.Data = MyEncoder(image, txtg.BlockInfo);   // must be surface.DataLength bytes

txtg.Save("MaterialAlb.edited.txtg");
```

Assigned data is linear and still block compressed: `DataLength` bytes of blocks in the
container's own format, left to right and top to bottom. `TxtgSurface.Swizzled()` gives
you the block-linear form instead, for a tool that writes its own container.

To change size, format or mip count while keeping the fields nothing can derive — the
32-byte digest at `0x1C`, the sampler settings at `0x40` and `0x4D`, the channel swizzle —
build on top of the original:

```csharp
TxtgFile.CreateFrom(original, width, height, format, surfaces, blockInfo).Save("New.txtg");
TxtgFile.Create(width, height, format, surfaces).Save("New.txtg");   // no original to borrow
```

`Create` has to guess those fields, so prefer `CreateFrom` whenever an original exists.

## Converting images

Turns images into containers, one at a time or a folder at a time.

```bash
# one file, named explicitly
txtg-cli convert rock.png Rock_A_Alb.txtg --template romfs/TexToGo/Rock_A_Alb.txtg

# a whole folder: each output takes its source file's name
txtg-cli convert edited/ --out build/TexToGo --template-dir romfs/TexToGo

# inputs may be files, directories or wildcards, and --recursive descends
txtg-cli convert "edited/*.dds" --out build --template-dir romfs/TexToGo

txtg-cli convert sign.jpg Sign.txtg --format astc8x8-srgb --mips 1
txtg-cli convert --list-formats
```

Inputs are `.png .jpg .bmp .tga .gif .psd` or `.dds`. Targets are BC1, BC3, BC4, BC5, BC7,
all fourteen ASTC footprints in unorm or sRGB, and the three uncompressed formats. BC4 and
BC1 between them account for nine in ten of the game's containers; BC7 for none.

### Batch

`--out <dir>` switches to batch mode: `Rock_A_Alb.png` becomes `Rock_A_Alb.txtg`. Only the
final extension comes off, so the game's own doubled names survive - `Beard_Alb.05.png`
becomes `Beard_Alb.05.txtg`.

`--template-dir <dir>` is the one to pair it with. Each input is matched to a container of
the same name, so every file gets its own original's format, footprint, mip count and
sampler settings without naming any of it on the command line. A batch across the game's
textures picks up BC1, BC4, BC5 and half a dozen ASTC footprints by itself.

Files are converted in parallel (`--jobs`, defaulting to the processor count) and the
output is identical whatever that is set to. One file failing is reported and the rest
carry on; the exit code is non-zero if anything failed.

### Templates

`--template` is worth using even for one file. Without it the digest at `0x1C` and the
sampler settings at `0x40` and `0x4D` are guesses, and the tool says so. With it, the
output header differs from the original only in the fields the new geometry forces.

An array template - terrain material sets and the like - needs `--layer <n>`, because
writing one image over the whole thing would drop the other layers. With it, the named
layer is re-encoded and **every other layer keeps the exact zstd frame it came in as**.

Mips are box filtered, in linear light for an sRGB target and directly otherwise, so a
normal map or a mask is not dragged through a gamma curve it was never in. A DDS already
in the target block format is copied through with no re-encode at all.

## Inspecting

```bash
# header, format and size checks
txtg-cli info MaterialAlb.txtg

# decode ASTC layers to PNG
txtg-cli dump MaterialAlb.txtg ./out 0 5 97

# verify the writer reproduces a file exactly
txtg-cli roundtrip MaterialAlb.txtg

# and again with the verbatim-payload shortcut disabled, which tests the swizzler itself
txtg-cli roundtrip MaterialAlb.txtg --reswizzle
```

## Credit
This was based on the bones of [EPD-Libraries/txtg](https://github.com/EPD-Libraries/txtg)
