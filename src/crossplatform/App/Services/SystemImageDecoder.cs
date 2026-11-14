using System.Runtime.InteropServices;

namespace GitExtensions.Avalonia.Services;

/// <summary>An image decoded to 8-bit, non-premultiplied RGBA rows of <c>Width * 4</c> bytes.</summary>
public sealed record DecodedImage(int Width, int Height, byte[] Rgba);

/// <summary>
///  AVIF through the system's gdk-pixbuf, for the one format upstream decodes
///  (e2a7dc3ab) that the port's SkiaSharp 2.88.9 cannot: <c>SKBitmap.Decode</c> has no
///  AV1 codec and returns null.
///
///  <para><b>Why gdk-pixbuf and not libavif.</b> Both are on a typical desktop, but
///  libavif can only be driven through structs (<c>avifImage</c>, <c>avifRGBImage</c>)
///  whose layout has changed between its SONAMEs, so a P/Invoke against it is a guess
///  about the installed version. gdk-pixbuf's API is functions on opaque pointers and
///  has been ABI-stable since 2.0; whether it can read AVIF is decided by the loaders
///  installed (the libheif loader, <c>heif-gdk-pixbuf</c>, or the libavif one,
///  <c>libavif-gdk-pixbuf</c>), which is exactly the system's own answer to "can this
///  desktop open an AVIF".</para>
///
///  <para><b>Fails silently.</b> No library, no loader, a corrupt file: every path
///  returns <see langword="null"/>, and the caller keeps its previous behaviour
///  (the image window says the side could not be decoded). Nothing is added to the
///  package and nothing else in the app touches gdk-pixbuf.</para>
/// </summary>
public static class SystemImageDecoder
{
    private const string PixbufLib = "libgdk_pixbuf-2.0.so.0";
    private const string GObjectLib = "libgobject-2.0.so.0";
    private const string GLibLib = "libglib-2.0.so.0";

    // A cap on what a decode may allocate: 16k × 16k RGBA is already 1 GiB.
    private const long MaxPixels = 16384L * 16384L;

    private static readonly Lazy<bool> Available = new(Probe);

    // An 8×6 AVIF (ffmpeg + libaom, 321 bytes) that the probe decodes once.
    private const string ProbeAvif =
        "AAAAIGZ0eXBhdmlmAAAAAGF2aWZtaWYxbWlhZk1BMUEAAAD5bWV0YQAAAAAAAAAvaGRscgAAAAAAAAAAcGljdAAAAAAAAAAAAAAAAFBpY3R1cmVIYW5kbGVyAAAAAA5waXRtAAAAAAABAAAAHmlsb2MAAAAARAAAAQABAAAAAQAAASEAAAAgAAAAKGlpbmYAAAAAAAEAAAAaaW5mZQIAAAAAAQAAYXYwMUNvbG9yAAAAAGppcHJwAAAAS2lwY28AAAAUaXNwZQAAAAAAAAAIAAAABgAAABBwaXhpAAAAAAMICAgAAAAMYXYxQ4EgAAAAAAATY29scm5jbHgAAQACAACAAAAAF2lwbWEAAAAAAAAAAQABBAECgwQAAAAobWRhdAoIOAi9bQENACAyFBgAAABQAAAABfXvZN1zAeOaG2HY";

    /// <summary>
    ///  Whether gdk-pixbuf is installed AND one of its loaders really decodes AVIF.
    ///  Asked once per process; false off Linux.
    /// </summary>
    public static bool CanDecodeAvif => Available.Value;

    /// <summary>
    ///  Decodes <paramref name="bytes"/> with gdk-pixbuf, or returns <see langword="null"/>
    ///  when it cannot (see the class remarks). Only meant for AVIF; it would read other
    ///  formats too, which is why the callers ask only for that one.
    /// </summary>
    public static DecodedImage? TryDecodeAvif(byte[] bytes)
    {
        if (bytes.Length == 0 || !CanDecodeAvif)
        {
            return null;
        }

        try
        {
            return Decode(bytes);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return null;
        }
    }

    private static DecodedImage? Decode(byte[] bytes)
    {
        IntPtr loader = gdk_pixbuf_loader_new_with_mime_type("image/avif", out IntPtr error);
        if (loader == IntPtr.Zero)
        {
            FreeError(error);
            return null;
        }

        try
        {
            bool written = gdk_pixbuf_loader_write(loader, bytes, (nuint)bytes.Length, out error) != 0;
            FreeError(error);

            // Closed even after a failed write: the loader must be closed before it is
            // released, or GLib warns about an unfinished load.
            bool closed = gdk_pixbuf_loader_close(loader, out error) != 0;
            FreeError(error);
            if (!written || !closed)
            {
                return null;
            }

            // Owned by the loader (transfer none): read before the loader goes.
            IntPtr pixbuf = gdk_pixbuf_loader_get_pixbuf(loader);
            return pixbuf == IntPtr.Zero ? null : Copy(pixbuf);
        }
        finally
        {
            g_object_unref(loader);
        }
    }

    private static DecodedImage? Copy(IntPtr pixbuf)
    {
        int width = gdk_pixbuf_get_width(pixbuf);
        int height = gdk_pixbuf_get_height(pixbuf);
        int channels = gdk_pixbuf_get_n_channels(pixbuf);
        int stride = gdk_pixbuf_get_rowstride(pixbuf);
        int bits = gdk_pixbuf_get_bits_per_sample(pixbuf);
        IntPtr pixels = gdk_pixbuf_read_pixels(pixbuf);

        if (width <= 0 || height <= 0 || (long)width * height > MaxPixels
            || bits != 8 || channels is not (3 or 4) || stride < width * channels || pixels == IntPtr.Zero)
        {
            return null;
        }

        byte[] rgba = new byte[width * height * 4];
        byte[] row = new byte[width * channels];
        for (int y = 0; y < height; y++)
        {
            // The last row may be shorter than the stride (gdk-pixbuf documents it),
            // so exactly width * channels bytes are read from each row.
            Marshal.Copy(pixels + (y * stride), row, 0, row.Length);
            int o = y * width * 4;
            if (channels == 4)
            {
                Buffer.BlockCopy(row, 0, rgba, o, row.Length);
                continue;
            }

            for (int x = 0, i = 0; x < width; x++, i += 3, o += 4)
            {
                rgba[o] = row[i];
                rgba[o + 1] = row[i + 1];
                rgba[o + 2] = row[i + 2];
                rgba[o + 3] = 0xFF;
            }
        }

        return new DecodedImage(width, height, rgba);
    }

    private static bool Probe()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsFreeBSD())
        {
            return false;
        }

        try
        {
            // Decoding a real (tiny) AVIF is the exact question: a loader can claim the
            // MIME type and still lack an AV1 decoder (libheif without its aom/dav1d
            // plugin). Asking with an EMPTY loader instead made GLib print a warning.
            return Decode(Convert.FromBase64String(ProbeAvif)) is { Width: 8, Height: 6 };
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return false;
        }
    }

    private static void FreeError(IntPtr error)
    {
        if (error != IntPtr.Zero)
        {
            g_error_free(error);
        }
    }

    // gboolean is a C int.
    [DllImport(PixbufLib)]
    private static extern IntPtr gdk_pixbuf_loader_new_with_mime_type(string mimeType, out IntPtr error);

    [DllImport(PixbufLib)]
    private static extern int gdk_pixbuf_loader_write(IntPtr loader, byte[] buffer, nuint count, out IntPtr error);

    [DllImport(PixbufLib)]
    private static extern int gdk_pixbuf_loader_close(IntPtr loader, out IntPtr error);

    [DllImport(PixbufLib)]
    private static extern IntPtr gdk_pixbuf_loader_get_pixbuf(IntPtr loader);

    [DllImport(PixbufLib)]
    private static extern int gdk_pixbuf_get_width(IntPtr pixbuf);

    [DllImport(PixbufLib)]
    private static extern int gdk_pixbuf_get_height(IntPtr pixbuf);

    [DllImport(PixbufLib)]
    private static extern int gdk_pixbuf_get_n_channels(IntPtr pixbuf);

    [DllImport(PixbufLib)]
    private static extern int gdk_pixbuf_get_rowstride(IntPtr pixbuf);

    [DllImport(PixbufLib)]
    private static extern int gdk_pixbuf_get_bits_per_sample(IntPtr pixbuf);

    [DllImport(PixbufLib)]
    private static extern IntPtr gdk_pixbuf_read_pixels(IntPtr pixbuf);

    [DllImport(GObjectLib)]
    private static extern void g_object_unref(IntPtr instance);

    [DllImport(GLibLib)]
    private static extern void g_error_free(IntPtr error);
}
