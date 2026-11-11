using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace NotchWin.Services;

public enum ClipKind { Text, Link, Image, Files }

public sealed partial class ClipItem : ObservableObject
{
    bool _pinned;
    ImageSource? _thumb;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public ClipKind Kind { get; set; }
    public string? Text { get; set; }
    public string[]? Files { get; set; }
    public DateTime Time { get; set; } = DateTime.Now;

    public bool Pinned
    {
        get => _pinned;
        set { if (Set(ref _pinned, value)) Raise(nameof(PinGlyph)); }
    }

    /// <summary>
    /// Derived from the validated Id, never read from disk, so a tampered history file
    /// can't make us read or delete arbitrary files.
    /// </summary>
    [JsonIgnore] public string ImagePath => Path.Combine(ClipboardService.ImageDir, Id + ".bin");
    [JsonIgnore] public bool HasValidId => IdPattern().IsMatch(Id ?? "");

    [JsonIgnore] public string PinGlyph => Pinned ? "\uE842" : "\uE718";
    [JsonIgnore] public bool IsImage => Kind == ClipKind.Image;
    [JsonIgnore] public bool IsLink => Kind == ClipKind.Link;

    [JsonIgnore]
    public string Glyph => Kind switch
    {
        ClipKind.Link => "\uE71B",
        ClipKind.Image => "\uE91B",
        ClipKind.Files => "\uE8B7",
        _ => "\uE8A5",
    };

    [JsonIgnore]
    public string Preview => Kind switch
    {
        ClipKind.Image => "Image",
        ClipKind.Files when Files is { Length: 1 } => Path.GetFileName(Files[0]),
        ClipKind.Files => $"{Files?.Length ?? 0} files — {string.Join(", ", (Files ?? []).Take(3).Select(Path.GetFileName))}",
        _ => OneLine(Text),
    };

    [JsonIgnore]
    public string TimeText
    {
        get
        {
            var age = DateTime.Now - Time;
            if (age.TotalMinutes < 1) return "now";
            if (age.TotalHours < 1) return $"{(int)age.TotalMinutes}m";
            if (age.TotalDays < 1) return $"{(int)age.TotalHours}h";
            return Time.ToString("MMM d");
        }
    }

    [JsonIgnore]
    public ImageSource? Thumb
    {
        get
        {
            if (_thumb == null && IsImage && HasValidId && File.Exists(ImagePath))
            {
                try { _thumb = ImageUtil.FromBytes(SecureStore.Unprotect(File.ReadAllBytes(ImagePath)), 80); }
                catch { }
            }
            return _thumb;
        }
        set => _thumb = value;
    }

    [JsonIgnore] public string Key => Kind == ClipKind.Files ? string.Join("|", Files ?? []) : Text ?? Id;

    static string OneLine(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        // Only the start is ever visible; avoid splitting megabytes of text for a preview.
        var head = s.Length > 400 ? s[..400] : s;
        var t = string.Join(' ', head.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return t.Length > 200 ? t[..200] + "…" : t;
    }

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex IdPattern();
}

/// <summary>
/// Clipboard history fed by WM_CLIPBOARDUPDATE. Persisted under %AppData%\NotchWin, encrypted
/// with Windows DPAPI so only the current Windows user can read it.
/// </summary>
public sealed class ClipboardService
{
    const int MaxUnpinned = 60;
    const int MaxTextChars = 256 * 1024;
    const long MaxImagePixels = 40_000_000;

    public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NotchWin");
    public static readonly string ImageDir = Path.Combine(Dir, "clips");
    static readonly string HistoryFile = Path.Combine(Dir, "clipboard.dat");
    static readonly string LegacyHistoryFile = Path.Combine(Dir, "clipboard.json");

    readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly object _writeLock = new();
    string _filter = "";
    DateTime _ignoreUntil;

    public ObservableCollection<ClipItem> Items { get; } = new();
    public ICollectionView View { get; }

    /// <summary>When true, nothing new is recorded (e.g. while handling sensitive data).</summary>
    public bool Paused { get; set; }

    public ClipboardService()
    {
        Directory.CreateDirectory(ImageDir);
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveAsync(); };

        View = CollectionViewSource.GetDefaultView(Items);
        View.Filter = o => o is ClipItem c && (_filter.Length == 0 ||
            c.Preview.Contains(_filter, StringComparison.OrdinalIgnoreCase) ||
            (c.Text?.Contains(_filter, StringComparison.OrdinalIgnoreCase) ?? false));
        Load();
    }

    public string Filter
    {
        get => _filter;
        set { _filter = value?.Trim() ?? ""; View.Refresh(); }
    }

    public ClipItem? FirstVisible => View.Cast<ClipItem>().FirstOrDefault();

    public static bool TryGetWebUrl(string? text, out Uri uri)
    {
        uri = null!;
        var t = text?.Trim();
        return !string.IsNullOrEmpty(t) && t.Length < 4096 && !t.Any(char.IsWhiteSpace) &&
               Uri.TryCreate(t, UriKind.Absolute, out uri!) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    public void OnClipboardChanged()
    {
        if (Paused || DateTime.UtcNow < _ignoreUntil) return;
        try
        {
            var data = Retry(Clipboard.GetDataObject);
            if (data == null || IsPrivate(data)) return;

            if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
            {
                Add(new ClipItem { Kind = ClipKind.Files, Files = files.Take(500).ToArray() });
            }
            else if (data.GetDataPresent(DataFormats.UnicodeText) && data.GetData(DataFormats.UnicodeText) is string text && !string.IsNullOrWhiteSpace(text))
            {
                if (text.Length > MaxTextChars) return;
                Add(new ClipItem { Kind = TryGetWebUrl(text, out _) ? ClipKind.Link : ClipKind.Text, Text = text });
            }
            else if (data.GetDataPresent(DataFormats.Bitmap))
            {
                var image = Retry(Clipboard.GetImage);
                if (image == null || (long)image.PixelWidth * image.PixelHeight > MaxImagePixels) return;
                image.Freeze();
                var item = new ClipItem { Kind = ClipKind.Image };
                item.Thumb = ImageUtil.Thumbnail(image, 80);
                var path = item.ImagePath;
                // PNG encoding + encryption of big images is slow; keep it off the UI thread.
                Task.Run(() =>
                {
                    try { SecureStore.WriteAtomic(path, SecureStore.Protect(ImageUtil.EncodePng(image))); }
                    catch { }
                });
                Add(item);
            }
        }
        catch (Exception ex) when (ex is COMException or ExternalException or InvalidOperationException or IOException or OutOfMemoryException)
        {
            // Another app is holding the clipboard, or the data is unusable; skip this change.
        }
    }

    /// <summary>
    /// Honours the standard formats password managers (1Password, Bitwarden, KeePass…) and
    /// Windows itself use to mark content that must not be kept in clipboard history.
    /// </summary>
    static bool IsPrivate(IDataObject data)
    {
        if (data.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing") ||
            data.GetDataPresent("Clipboard Viewer Ignore"))
            return true;
        return IsZeroFlag(data, "CanIncludeInClipboardHistory");
    }

    static bool IsZeroFlag(IDataObject data, string format)
    {
        if (!data.GetDataPresent(format)) return false;
        try
        {
            if (data.GetData(format) is MemoryStream ms && ms.Length >= 4)
            {
                var buf = new byte[4];
                ms.Position = 0;
                ms.ReadExactly(buf);
                return BitConverter.ToInt32(buf) == 0;
            }
        }
        catch { }
        return true; // Present but unreadable: err on the side of privacy.
    }

    /// <summary>For our own clipboard writes (e.g. screenshots) that shouldn't become history entries.</summary>
    public void SuppressNextChange() => _ignoreUntil = DateTime.UtcNow.AddMilliseconds(700);

    /// <summary>Puts the item back on the clipboard and moves it to the top.</summary>
    public void Copy(ClipItem item)
    {
        SuppressNextChange();
        switch (item.Kind)
        {
            case ClipKind.Files:
                var list = new StringCollection();
                list.AddRange(item.Files ?? []);
                Retry(() => { Clipboard.SetFileDropList(list); return true; });
                break;
            case ClipKind.Image:
                if (!item.HasValidId || !File.Exists(item.ImagePath)) return;
                var bmp = ImageUtil.FromBytes(SecureStore.Unprotect(File.ReadAllBytes(item.ImagePath)));
                Retry(() => { Clipboard.SetImage(bmp); return true; });
                break;
            default:
                Retry(() => { Clipboard.SetText(item.Text ?? ""); return true; });
                break;
        }
        item.Time = DateTime.Now;
        MoveToTop(item);
        ScheduleSave();
    }

    public void TogglePin(ClipItem item)
    {
        item.Pinned = !item.Pinned;
        MoveToTop(item);
        ScheduleSave();
    }

    public void Remove(ClipItem item)
    {
        Items.Remove(item);
        DeleteFile(item);
        ScheduleSave();
    }

    public void ClearUnpinned()
    {
        foreach (var item in Items.Where(i => !i.Pinned).ToList())
        {
            Items.Remove(item);
            DeleteFile(item);
        }
        ScheduleSave();
    }

    void Add(ClipItem item)
    {
        if (item.Kind != ClipKind.Image)
        {
            var dup = Items.FirstOrDefault(i => i.Kind == item.Kind && i.Key == item.Key);
            if (dup != null)
            {
                dup.Time = DateTime.Now;
                MoveToTop(dup);
                ScheduleSave();
                return;
            }
        }

        Items.Insert(Items.Count(i => i.Pinned), item);
        foreach (var old in Items.Where(i => !i.Pinned).Skip(MaxUnpinned).ToList())
        {
            Items.Remove(old);
            DeleteFile(old);
        }
        ScheduleSave();
    }

    // Pinned items stay above everything else; within each group newest first.
    void MoveToTop(ClipItem item)
    {
        int from = Items.IndexOf(item);
        if (from < 0) return;
        Items.RemoveAt(from);
        Items.Insert(item.Pinned ? 0 : Items.Count(i => i.Pinned), item);
    }

    static void DeleteFile(ClipItem item)
    {
        if (!item.IsImage || !item.HasValidId) return;
        var path = item.ImagePath;
        Task.Run(() => { try { File.Delete(path); } catch { } });
    }

    void Load()
    {
        List<ClipItem>? items = null;
        try
        {
            if (File.Exists(HistoryFile))
                items = JsonSerializer.Deserialize<List<ClipItem>>(SecureStore.Unprotect(File.ReadAllBytes(HistoryFile)));
        }
        catch { }

        // Earlier versions stored unencrypted data; drop it rather than keep plaintext on disk.
        try { File.Delete(LegacyHistoryFile); } catch { }
        try { foreach (var f in Directory.GetFiles(ImageDir, "*.png")) File.Delete(f); } catch { }

        foreach (var i in (items ?? []).Where(i => i.HasValidId && (i.Kind != ClipKind.Image || File.Exists(i.ImagePath))))
            Items.Add(i);
    }

    void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    void SaveAsync()
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(Items.ToList());
        Task.Run(() => Write(bytes));
    }

    /// <summary>Synchronous save for shutdown.</summary>
    public void Flush()
    {
        _saveTimer.Stop();
        Write(JsonSerializer.SerializeToUtf8Bytes(Items.ToList()));
    }

    void Write(byte[] json)
    {
        lock (_writeLock)
        {
            try { SecureStore.WriteAtomic(HistoryFile, SecureStore.Protect(json)); } catch { }
        }
    }

    static T? Retry<T>(Func<T?> action)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { return action(); }
            catch (COMException) when (attempt < 5) { Thread.Sleep(40); }
        }
    }
}
