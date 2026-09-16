using StbImageSharp;

namespace TxtgSharp.Cli;

internal sealed class ImageSource
{
    private readonly DdsImage? _dds;
    private readonly byte[]? _rgba;
    private byte[][]? _chain;
    private bool _chainIsSrgb;

    private ImageSource(int width, int height, byte[]? rgba, DdsImage? dds)
    {
        Width = width;
        Height = height;
        _rgba = rgba;
        _dds = dds;
    }

    public int Width { get; }
    public int Height { get; }

    public static ImageSource Load(string path)
    {
        if (Path.GetExtension(path).Equals(".dds", StringComparison.OrdinalIgnoreCase))
        {
            DdsImage dds = DdsImage.Read(path);
            return new ImageSource(dds.Width, dds.Height, null, dds);
        }

        ImageResult image = ImageResult.FromMemory(File.ReadAllBytes(path), ColorComponents.RedGreenBlueAlpha)
                            ?? throw new ConversionException($"could not decode {Path.GetFileName(path)}");

        return new ImageSource(image.Width, image.Height, image.Data, null);
    }

    public string Describe() =>
        _dds is not null
            ? $"{Width}x{Height} DDS, {_dds.FormatName}{(_dds.IsSrgb ? " srgb" : "")}, " +
              $"{_dds.MipCount} mip(s), {_dds.ArraySize} layer(s)"
            : $"{Width}x{Height} RGBA8";

    public byte[]? BlocksFor(TargetFormat target, int mip)
    {
        if (_dds is null || !_dds.IsCompressed || mip >= _dds.MipCount) return null;
        return target.AcceptsBlocksFrom(_dds.FormatName) ? _dds.Surfaces[0][mip] : null;
    }

    public byte[] Rgba(int mip, bool srgb)
    {
        if (_dds is not null && mip < _dds.MipCount)
        {
            return _dds.IsCompressed
                ? SurfaceEncoder.Decode(_dds.Surfaces[0][mip],
                    Math.Max(1, Width >> mip), Math.Max(1, Height >> mip), _dds.FormatName)
                : _dds.ToRgba(0, mip);
        }

        if (_chain is null || _chainIsSrgb != srgb || _chain.Length <= mip)
        {
            byte[] top = _rgba ?? Rgba(0, srgb);
            _chain = MipChain.Build(top, Width, Height, Math.Max(mip + 1, MipChain.FullCount(Width, Height)), srgb);
            _chainIsSrgb = srgb;
        }

        return _chain[mip];
    }
}
