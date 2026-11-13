using System.Drawing;
using System.Drawing.Text;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace AGLPTZ;

internal static class InterTypography
{
    private static readonly PrivateFontCollection FontCollection = new();
    // GDI+ reads these font buffers for as long as the collection is in use.
    private static readonly List<byte[]> FontBuffers = new();
    private static readonly Lazy<FontFamily> Family = new(LoadFamily);

    internal static Font Face(float size, bool strong) =>
        new(Family.Value, size, strong ? FontStyle.Bold : FontStyle.Regular);

    private static FontFamily LoadFamily()
    {
        Register("Inter-Regular.ttf.gz");
        Register("Inter-Bold.ttf.gz");
        return FontCollection.Families.Single(family => family.Name == "Inter");
    }

    private static void Register(string name)
    {
        using var embedded = typeof(InterTypography).Assembly.GetManifestResourceStream("AGLPTZ.Fonts." + name)
            ?? throw new InvalidOperationException($"Missing embedded font {name}.");
        using var compressed = new GZipStream(embedded, CompressionMode.Decompress);
        using var decompressed = new MemoryStream();
        compressed.CopyTo(decompressed);

        int size = checked((int)decompressed.Length);
        byte[] buffer = GC.AllocateUninitializedArray<byte>(size, pinned: true);
        decompressed.GetBuffer().AsSpan(0, size).CopyTo(buffer);
        IntPtr address = Marshal.UnsafeAddrOfPinnedArrayElement(buffer, 0);
        // WinForms uses GDI for TextRenderer and standard edit controls.
        if (AddFontMemResourceEx(address, (uint)size, IntPtr.Zero, out uint count) == IntPtr.Zero || count == 0)
            throw new InvalidOperationException($"Windows could not load {name}.");
        FontCollection.AddMemoryFont(address, size);
        FontBuffers.Add(buffer);
    }

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern IntPtr AddFontMemResourceEx(IntPtr buffer, uint size, IntPtr reserved, out uint count);
}
