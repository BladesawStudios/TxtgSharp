using BCnEncoder.Shared;
namespace TxtgSharp.Cli;

internal enum EncoderKind
{
    Bcn,

    Astc,

    Raw
}

internal readonly record struct TargetFormat(
    string Name, TxtgFormat Format, TxtgBlockInfo Block, EncoderKind Encoder, bool Srgb, CompressionFormat Bcn)
{
    private static readonly (int W, int H)[] AstcFootprints =
    [
        (4, 4), (5, 4), (5, 5), (6, 5), (6, 6), (8, 5), (8, 6), (8, 8),
        (10, 5), (10, 6), (10, 8), (10, 10), (12, 10), (12, 12)
    ];

    private static readonly TargetFormat[] All =
    [
        Bc("bc1",       TxtgFormat.Bc1Unorm,     4, 4, 8,  CompressionFormat.Bc1, srgb: false),
        Bc("bc1a",      TxtgFormat.Bc1Unorm,     4, 4, 8,  CompressionFormat.Bc1WithAlpha, srgb: false),
        Bc("bc1-srgb",  TxtgFormat.Bc1UnormSrgb, 4, 4, 8,  CompressionFormat.Bc1, srgb: true),
        Bc("bc3",       TxtgFormat.Bc3UnormSrgb, 4, 4, 16, CompressionFormat.Bc3, srgb: true),
        Bc("bc4",       TxtgFormat.Bc4Unorm,     4, 4, 8,  CompressionFormat.Bc4, srgb: false),
        Bc("bc5",       TxtgFormat.Bc5Unorm,     4, 4, 16, CompressionFormat.Bc5, srgb: false),
        Bc("bc7",       TxtgFormat.Bc7Unorm,     4, 4, 16, CompressionFormat.Bc7, srgb: false),

        Raw("r8",    TxtgFormat.R8Unorm,       1),
        Raw("rg8",   TxtgFormat.R8G8Unorm,     2),
        Raw("rgba8", TxtgFormat.R8G8B8A8Unorm, 4),

        ..AstcFootprints.SelectMany(f => new[] { Astc(f.W, f.H, srgb: false), Astc(f.W, f.H, srgb: true) })
    ];

    private static TargetFormat Bc(string name, TxtgFormat format, int w, int h, int bytes,
                                   CompressionFormat bcn, bool srgb) =>
        new(name, format, new TxtgBlockInfo(w, h, bytes), EncoderKind.Bcn, srgb, bcn);

    private static TargetFormat Raw(string name, TxtgFormat format, int bytes) =>
        new(name, format, new TxtgBlockInfo(1, 1, bytes), EncoderKind.Raw, false, CompressionFormat.Unknown);

    private static TargetFormat Astc(int w, int h, bool srgb) =>
        new($"astc{w}x{h}{(srgb ? "-srgb" : "")}",
            srgb ? TxtgFormat.Astc4x4Srgb : TxtgFormat.Astc4x4Unorm,
            new TxtgBlockInfo(w, h, 16), EncoderKind.Astc, srgb, CompressionFormat.Unknown);

    public static IEnumerable<string> Names => All.Select(f => f.Name);

    public static bool TryParse(string name, out TargetFormat format)
    {
        foreach (TargetFormat candidate in All)
        {
            if (string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                format = candidate;
                return true;
            }
        }

        format = default;
        return false;
    }

    public static TargetFormat FromContainer(TxtgFile txtg)
    {
        TxtgBlockInfo block = txtg.BlockInfo;
        bool srgb = txtg.Format.IsSrgb();

        if (txtg.Format.IsAstc())
            return Astc(block.Width, block.Height, srgb) with { Block = block };

        foreach (TargetFormat candidate in All)
        {
            if (candidate.Encoder != EncoderKind.Astc && candidate.Format == txtg.Format && candidate.Name is not "bc1a")
                return candidate with { Block = block };
        }

        throw new NotSupportedException($"No encoder for {txtg.Format}.");
    }

    public static TxtgBlockInfo? BlockOf(string name) => name switch
    {
        "bc1" => new TxtgBlockInfo(4, 4, 8),
        "bc4" => new TxtgBlockInfo(4, 4, 8),
        "bc2" or "bc3" or "bc5" or "bc7" => new TxtgBlockInfo(4, 4, 16),
        _ => null
    };

    public bool AcceptsBlocksFrom(string ddsFormat) =>
        Encoder == EncoderKind.Bcn && string.Equals(BcnName, ddsFormat, StringComparison.OrdinalIgnoreCase);

    private string BcnName => Bcn switch
    {
        CompressionFormat.Bc1 or CompressionFormat.Bc1WithAlpha => "bc1",
        CompressionFormat.Bc2 => "bc2",
        CompressionFormat.Bc3 => "bc3",
        CompressionFormat.Bc4 => "bc4",
        CompressionFormat.Bc5 => "bc5",
        CompressionFormat.Bc7 => "bc7",
        _ => "-"
    };

    public override string ToString() =>
        $"{Name} ({Block.Width}x{Block.Height} blocks, {Block.BytesPerBlock} bytes)";
}
