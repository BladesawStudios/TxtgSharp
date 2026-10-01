# TxtgSharp
A C# lib + CLI for working with Txtg files

## Decoding and DDS

With [TexSharp](../TexSharp), a container can be decoded or handed to an image editor:

```csharp
TxtgFile txtg = TxtgFile.FromFile("texture.txtg");

byte[] rgba = txtg.ToRgba8();                        // layer 0, mip 0
File.WriteAllBytes("texture.dds", txtg.ToDds().ToBytes());

TxtgFile edited = txtg.WithDds(DdsImage.Parse(File.ReadAllBytes("texture.dds")));
edited.Save("edited.txtg");
```

`WithDds` returns a new file with the original's header fields kept. The DDS's size and mip count can differ,
and so can its format: a different one changes the file's format and, for ASTC, its footprint, without
converting the pixels. `ToDds(editable: true)` expands R8 and RG8 to RGBA8 for image editors, and `WithDds`
collapses an RGBA8 DDS back for those two. Single-layer files only.

ASTC takes its block footprint from the file's header, not from the format name, since the two often disagree.
