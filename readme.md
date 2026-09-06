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


## CLI

```bash
# header, format and size checks
TxtgSharp.Cli info MaterialAlb.txtg

# decode ASTC layers to PNG
TxtgSharp.Cli dump MaterialAlb.txtg ./out 0 5 97
```
