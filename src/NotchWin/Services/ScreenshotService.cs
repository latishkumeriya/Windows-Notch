using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NotchWin.Native;

namespace NotchWin.Services;

public sealed class ShotItem
{
    public ShotItem(string path)
    {
        Path = path;
        Thumb = ImageUtil.Load(path, 240);
    }

    public string Path { get; }
    public ImageSource? Thumb { get; }
}

public sealed class ScreenshotService
{
    const int MaxShown = 20;

    public ObservableCollection<ShotItem> Items { get; } = new();
    public string Folder { get; }

    public ScreenshotService()
    {
        Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots", "NotchWin");
        Directory.CreateDirectory(Folder);
    }

    /// <summary>Decodes recent thumbnails off the UI thread so startup isn't blocked.</summary>
    public async Task LoadRecentAsync()
    {
        var loaded = await Task.Run(() =>
        {
            var list = new List<ShotItem>();
            foreach (var file in new DirectoryInfo(Folder).GetFiles("*.png").OrderByDescending(f => f.CreationTime).Take(MaxShown))
            {
                try { list.Add(new ShotItem(file.FullName)); } catch { }
            }
            return list;
        });
        foreach (var item in loaded.Where(l => Items.All(i => i.Path != l.Path)))
            Items.Add(item);
    }

    /// <summary>Only files we created in our own folder may be opened or deleted.</summary>
    public bool Owns(ShotItem item)
    {
        var full = Path.GetFullPath(item.Path);
        return Path.GetDirectoryName(full)!.Equals(Path.GetFullPath(Folder), StringComparison.OrdinalIgnoreCase) &&
               full.EndsWith(".png", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Captures the monitor under the cursor in physical pixels.</summary>
    public static (BitmapSource Image, NativeMethods.RECT Bounds) CaptureMonitorUnderCursor()
    {
        var m = NativeMethods.MonitorUnderCursor();
        return (CaptureRect(m.Left, m.Top, m.Width, m.Height), m);
    }

    static BitmapSource CaptureRect(int x, int y, int w, int h)
    {
        IntPtr screen = NativeMethods.GetDC(IntPtr.Zero);
        IntPtr mem = NativeMethods.CreateCompatibleDC(screen);
        IntPtr bmp = NativeMethods.CreateCompatibleBitmap(screen, w, h);
        IntPtr old = NativeMethods.SelectObject(mem, bmp);
        try
        {
            NativeMethods.BitBlt(mem, 0, 0, w, h, screen, x, y, NativeMethods.SRCCOPY | NativeMethods.CAPTUREBLT);
            NativeMethods.SelectObject(mem, old);
            var src = Imaging.CreateBitmapSourceFromHBitmap(bmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        finally
        {
            NativeMethods.DeleteObject(bmp);
            NativeMethods.DeleteDC(mem);
            NativeMethods.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    public ShotItem Save(BitmapSource image)
    {
        var path = Path.Combine(Folder, $"Screenshot {DateTime.Now:yyyy-MM-dd HH.mm.ss}.png");
        ImageUtil.SavePng(image, path);
        var item = new ShotItem(path);
        Items.Insert(0, item);
        while (Items.Count > MaxShown) Items.RemoveAt(Items.Count - 1);
        return item;
    }

    /// <summary>Image + file on the clipboard: chat apps paste the picture, Explorer pastes the file.</summary>
    public static void CopyToClipboard(ShotItem item)
    {
        var data = new DataObject();
        data.SetImage(ImageUtil.Load(item.Path));
        data.SetFileDropList(new StringCollection { item.Path });
        Clipboard.SetDataObject(data, true);
    }

    public void Delete(ShotItem item)
    {
        Items.Remove(item);
        if (!Owns(item)) return;
        try { File.Delete(item.Path); } catch { }
    }
}

public static class ImageUtil
{
    /// <summary>Loads fully into memory so the file is never locked.</summary>
    public static BitmapImage Load(string path, int decodeWidth = 0)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        if (decodeWidth > 0) bmp.DecodePixelWidth = decodeWidth;
        bmp.UriSource = new Uri(path);
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    public static void SavePng(BitmapSource image, string path) => File.WriteAllBytes(path, EncodePng(image));

    public static byte[] EncodePng(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    public static BitmapImage FromBytes(byte[] bytes, int decodeWidth = 0)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        if (decodeWidth > 0) bmp.DecodePixelWidth = decodeWidth;
        bmp.StreamSource = new MemoryStream(bytes);
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    public static BitmapSource Thumbnail(BitmapSource src, int width)
    {
        double k = Math.Min(1.0, (double)width / Math.Max(1, src.PixelWidth));
        var t = new TransformedBitmap(src, new ScaleTransform(k, k));
        // Materialise so the full-size source can be collected.
        var cached = new CachedBitmap(t, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        cached.Freeze();
        return cached;
    }
}
