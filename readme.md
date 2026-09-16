# TxtgSharp

A C# reader and writer for the `TexToGo` (`.txtg`) texture container used by *Tears of the Kingdom*.

Surfaces come out **deswizzled but still block compressed**, so callers can hand them
straight to a compressed GPU upload where the format is supported, or decode them on the
CPU where it is not. The library itself depends only on `ZstdSharp.Port`.

## Build

```bash
dotnet build TxtgSharp.sln -c Release
```

## Reading

```csharp
TxtgFile txtg = TxtgFile.FromFile("MaterialAlb.txtg");

Console.WriteLine($"{txtg.Width}x{txtg.Height}, {txtg.LayerCount} layers, {txtg.MipCount} mips");
Console.WriteLine($"{txtg.Format}");                  // e.g. Astc8x8Unorm
Console.WriteLine($"{txtg.BlockInfo}");               // e.g. { Width = 8, Height = 8, BytesPerBlock = 16 }

foreach (TxtgSurface s in txtg.LayersOfMip(0))        // ordered by array layer
{
    // s.Data is deswizzled, still in s-format blocks
    Upload(s.ArrayIndex, s.Width, s.Height, s.Data);
}
```

`Format` is a family-and-colour-space label. **`BlockInfo` is the authority on block
geometry**: the declared format code does not pin an ASTC footprint down, so it can name
4x4 for a container whose surfaces are really 8x6. The footprint comes from the header's
settings word instead, which carries `(width - 1) << 4 | (height - 1)` over a constant
`0x7F00` - `0x7F33` is 4x4, `0x7F77` is 8x8. Every ASTC footprint from 4x4 to 12x12 turns
up across the game's containers.

## Writing

Edit a container you read and save it. Surfaces you leave alone are written back as the
exact zstd frames they came in as, so **reading a retail file and writing it straight back
reproduces it byte for byte** - all 29,109 of the ones TotK ships. Only the surfaces you
replace cost a swizzle and a compression pass.

```csharp
TxtgFile txtg = TxtgFile.FromFile("MaterialAlb.txtg");

TxtgSurface surface = txtg.Surface(layer: 0, mip: 0)!;
surface.Data = MyEncoder(image, txtg.BlockInfo);   // must be surface.DataLength bytes

txtg.Save("MaterialAlb.edited.txtg");
```

Assigned data is linear and still block compressed: `DataLength` bytes of blocks in the
container's own format, left to right and top to bottom. `TxtgSurface.Swizzled()` gives
you the block-linear form instead, for a tool that writes its own container.

Building one from nothing works too, though a handful of header words - the 32-byte digest
at `0x1C`, the sampler settings at `0x40` and `0x4D` - carry values that cannot be derived
and get the value retail files use most often. Editing a real container keeps the real
ones, so prefer that whenever an original exists.

```csharp
TxtgFile.Create(width, height, TxtgFormat.Bc4Unorm, surfaces).Save("New.txtg");
```

## CLI

```bash
# header, format and size checks
TxtgSharp.Cli info MaterialAlb.txtg

# decode ASTC layers to PNG
TxtgSharp.Cli dump MaterialAlb.txtg ./out 0 5 97

# encode a PNG back in, rebuilding the whole mip chain of that layer (ASTC only)
TxtgSharp.Cli replace MaterialAlb.txtg edited.png MaterialAlb.out.txtg 0

# verify the writer reproduces a file exactly; --reswizzle also puts the swizzler under test
TxtgSharp.Cli roundtrip MaterialAlb.txtg --reswizzle
```

`replace` encodes with `AstcSharp`. BC formats have no encoder wired up, so feed those
pre-encoded blocks through `TxtgSurface.Data` instead.

## Credit
This was based on the bones of [EPD-Libraries/txtg](https://github.com/EPD-Libraries/txtg)
