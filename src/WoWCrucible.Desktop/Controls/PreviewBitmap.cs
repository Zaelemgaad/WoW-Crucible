using System.Runtime.InteropServices;
using SkiaSharp;
using WoWCrucible.Core;

namespace WoWCrucible.Desktop.Controls;

// The UI and queued render operations own separate leases on immutable pixels.
internal sealed class PreviewBitmap : IDisposable
{
    private sealed class Storage(SKBitmap bitmap)
    {
        public SKBitmap Bitmap { get; } = bitmap;
        public int References = 1;
    }

    private Storage? _storage;

    private PreviewBitmap(Storage storage) => _storage = storage;
    private PreviewBitmap(SKBitmap bitmap)
    {
        bitmap.SetImmutable();
        _storage = new Storage(bitmap);
    }

    public SKBitmap Bitmap => (Volatile.Read(ref _storage) ?? throw new ObjectDisposedException(nameof(PreviewBitmap))).Bitmap;

    public PreviewBitmap Retain()
    {
        var storage = Volatile.Read(ref _storage) ?? throw new ObjectDisposedException(nameof(PreviewBitmap));
        var count = Volatile.Read(ref storage.References);
        while (count > 0)
        {
            var previous = Interlocked.CompareExchange(ref storage.References, checked(count + 1), count);
            if (previous == count) return new PreviewBitmap(storage);
            count = previous;
        }
        throw new ObjectDisposedException(nameof(PreviewBitmap));
    }

    public static PreviewBitmap? Decode(string path) => SKBitmap.Decode(path) is { } bitmap ? new PreviewBitmap(bitmap) : null;

    public static PreviewBitmap Create(RgbaTexture texture)
    {
        ArgumentNullException.ThrowIfNull(texture);
        if (texture.Width <= 0 || texture.Height <= 0 || texture.Pixels.Length != texture.ByteLength)
            throw new ArgumentException("Texture dimensions must match its RGBA pixels.", nameof(texture));
        var bitmap = new SKBitmap(new SKImageInfo(texture.Width, texture.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        try
        {
            var rowBytes = checked(texture.Width * 4); var address = bitmap.GetPixels();
            if (address == IntPtr.Zero) throw new OutOfMemoryException("Could not allocate preview texture pixels.");
            for (var row = 0; row < texture.Height; row++) Marshal.Copy(texture.Pixels, row * rowBytes, IntPtr.Add(address, row * bitmap.RowBytes), rowBytes);
            return new PreviewBitmap(bitmap);
        }
        catch { bitmap.Dispose(); throw; }
    }

    public void Dispose()
    {
        var storage = Interlocked.Exchange(ref _storage, null);
        if (storage is not null && Interlocked.Decrement(ref storage.References) == 0) storage.Bitmap.Dispose();
    }
}
