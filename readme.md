# TxtgSharp
A C# lib + CLI for working with Txtg files

## Decoding and DDS

With [TexSharp](../TexSharp), a container can be decoded or handed to an image editor:

```csharp
TxtgFile txtg = TxtgFile.FromFile("texture.txtg");

byte[] rgba = txtg.ToRgba8();                        // layer 0, mip 0
File.WriteAllBytes("texture.dds", txtg.ToDds().ToBytes());

txtg.ReplaceFromDds(DdsImage.Parse(File.ReadAllBytes("texture.dds")));
```

`ReplaceFromDds` needs the same format and size as the file. ASTC takes its block footprint from the
file's header, not from the format name, since the two often disagree.
