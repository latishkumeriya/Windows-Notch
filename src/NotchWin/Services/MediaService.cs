using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace NotchWin.Services;

/// <summary>
/// Wraps Windows' global media session (the same source as the Win11 volume flyout),
/// so it works with Spotify, browsers, Apple Music, VLC, etc.
/// </summary>
public sealed class MediaService : ObservableObject
{
    GlobalSystemMediaTransportControlsSessionManager? _manager;
    GlobalSystemMediaTransportControlsSession? _session;
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(500) };
    TimeSpan _basePosition;
    DateTimeOffset _baseTime;

    bool _hasSession, _isPlaying, _isSeeking;
    string _title = "", _artist = "", _source = "";
    ImageSource? _art;
    Brush _accent = Brushes.White;
    double _position, _duration;

    public bool HasSession { get => _hasSession; private set => Set(ref _hasSession, value); }
    public bool IsPlaying
    {
        get => _isPlaying;
        private set { if (Set(ref _isPlaying, value)) Raise(nameof(PlayGlyph)); }
    }
    public string Title { get => _title; private set => Set(ref _title, value); }
    public string Artist { get => _artist; private set => Set(ref _artist, value); }
    public string Source { get => _source; private set => Set(ref _source, value); }
    public ImageSource? Art { get => _art; private set => Set(ref _art, value); }
    public Brush Accent { get => _accent; private set => Set(ref _accent, value); }
    public string PlayGlyph => IsPlaying ? "\uE769" : "\uE768";

    /// <summary>Seconds. Two-way bound to the seek slider; ignored by the ticker while seeking.</summary>
    public double Position
    {
        get => _position;
        set { if (Set(ref _position, value)) Raise(nameof(PositionText)); }
    }
    public double Duration
    {
        get => _duration;
        private set { if (Set(ref _duration, value)) Raise(nameof(DurationText)); }
    }
    public string PositionText => Format(Position);
    public string DurationText => Format(Duration);
    public bool IsSeeking { get => _isSeeking; set => _isSeeking = value; }

    public MediaService() => _tick.Tick += (_, _) => UpdatePosition();

    public async Task InitAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.CurrentSessionChanged += (_, _) => OnUi(ChooseSession);
            _manager.SessionsChanged += (_, _) => OnUi(ChooseSession);
            ChooseSession();
        }
        catch
        {
            HasSession = false;
        }
    }

    /// <summary>The seek-bar ticker only runs while the panel is visible.</summary>
    public bool Live
    {
        set
        {
            if (value) { UpdatePosition(); _tick.Start(); }
            else _tick.Stop();
        }
    }

    public void TogglePlay() => _ = _session?.TryTogglePlayPauseAsync();
    public void Next() => _ = _session?.TrySkipNextAsync();
    public void Previous() => _ = _session?.TrySkipPreviousAsync();

    public void Seek(double seconds)
    {
        if (_session == null) return;
        _ = _session.TryChangePlaybackPositionAsync(TimeSpan.FromSeconds(seconds).Ticks);
        _basePosition = TimeSpan.FromSeconds(seconds);
        _baseTime = DateTimeOffset.Now;
    }

    /// <summary>Switch to the next app that is exposing a media session.</summary>
    public void CycleSource()
    {
        if (_manager == null) return;
        var sessions = _manager.GetSessions();
        if (sessions.Count < 2) return;
        int index = 0;
        for (int i = 0; i < sessions.Count; i++)
            if (_session != null && sessions[i].SourceAppUserModelId == _session.SourceAppUserModelId) index = i;
        Attach(sessions[(index + 1) % sessions.Count]);
    }

    void ChooseSession()
    {
        if (_manager == null) return;
        var current = _manager.GetCurrentSession();
        // Prefer whatever is actually playing; Windows' "current" can be a paused tab.
        if (current == null || !IsSessionPlaying(current))
            current = _manager.GetSessions().FirstOrDefault(IsSessionPlaying) ?? current;
        Attach(current);
    }

    static bool IsSessionPlaying(GlobalSystemMediaTransportControlsSession s)
    {
        try { return s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing; }
        catch { return false; }
    }

    void Attach(GlobalSystemMediaTransportControlsSession? session)
    {
        if (_session != null)
        {
            _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            _session.TimelinePropertiesChanged -= OnTimelineChanged;
        }

        _session = session;
        HasSession = session != null;
        if (session == null)
        {
            Title = Artist = Source = "";
            Art = null;
            IsPlaying = false;
            Position = Duration = 0;
            return;
        }

        session.MediaPropertiesChanged += OnMediaPropertiesChanged;
        session.PlaybackInfoChanged += OnPlaybackInfoChanged;
        session.TimelinePropertiesChanged += OnTimelineChanged;
        Source = PrettifyApp(session.SourceAppUserModelId);
        _ = RefreshMediaAsync();
        RefreshPlayback();
        RefreshTimeline();
    }

    void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs e) => OnUi(() => _ = RefreshMediaAsync());
    void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e) => OnUi(RefreshPlayback);
    void OnTimelineChanged(GlobalSystemMediaTransportControlsSession s, TimelinePropertiesChangedEventArgs e) => OnUi(RefreshTimeline);

    static void OnUi(Action action) => System.Windows.Application.Current?.Dispatcher.InvokeAsync(action);

    async Task RefreshMediaAsync()
    {
        var session = _session;
        if (session == null) return;
        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            if (session != _session) return;
            Title = props.Title ?? "";
            Artist = string.IsNullOrEmpty(props.Artist) ? props.AlbumTitle ?? "" : props.Artist;
            var art = props.Thumbnail != null ? await LoadThumbnailAsync(props.Thumbnail) : null;
            if (session != _session) return;
            Art = art;
            var accent = new SolidColorBrush(art != null ? AverageColor(art) : Colors.White);
            accent.Freeze();
            Accent = accent;
        }
        catch
        {
            // Some apps briefly expose half-initialised sessions; the next change event retries.
        }
    }

    void RefreshPlayback()
    {
        if (_session == null) return;
        try
        {
            var info = _session.GetPlaybackInfo();
            IsPlaying = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        }
        catch { }
    }

    void RefreshTimeline()
    {
        if (_session == null) return;
        try
        {
            var t = _session.GetTimelineProperties();
            Duration = Math.Max(0, (t.EndTime - t.StartTime).TotalSeconds);
            _basePosition = t.Position;
            _baseTime = t.LastUpdatedTime;
            UpdatePosition();
        }
        catch { }
    }

    void UpdatePosition()
    {
        if (_session == null || IsSeeking) return;
        var pos = _basePosition;
        if (IsPlaying && _baseTime.Year > 2000)
            pos += DateTimeOffset.Now - _baseTime;
        Position = Math.Clamp(pos.TotalSeconds, 0, Duration > 0 ? Duration : double.MaxValue);
    }

    static async Task<BitmapSource?> LoadThumbnailAsync(IRandomAccessStreamReference reference)
    {
        using var ras = await reference.OpenReadAsync();
        using var stream = ras.AsStreamForRead();
        var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        ms.Position = 0;
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.DecodePixelWidth = 256;
        bmp.StreamSource = ms;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    static Color AverageColor(BitmapSource src)
    {
        try
        {
            var small = new TransformedBitmap(
                new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0),
                new ScaleTransform(8.0 / src.PixelWidth, 8.0 / src.PixelHeight));
            int w = small.PixelWidth, h = small.PixelHeight;
            var px = new byte[w * h * 4];
            small.CopyPixels(px, w * 4, 0);
            double r = 0, g = 0, b = 0;
            int n = w * h;
            for (int i = 0; i < px.Length; i += 4) { b += px[i]; g += px[i + 1]; r += px[i + 2]; }
            r /= n; g /= n; b /= n;
            // Lift dark covers so the visualizer stays visible on the black notch.
            double max = Math.Max(r, Math.Max(g, b));
            if (max < 170) { double k = 170 / Math.Max(max, 1); r *= k; g *= k; b *= k; }
            return Color.FromRgb((byte)Math.Min(255, r), (byte)Math.Min(255, g), (byte)Math.Min(255, b));
        }
        catch { return Colors.White; }
    }

    static string PrettifyApp(string aumid)
    {
        var s = aumid ?? "";
        int bang = s.LastIndexOf('!');
        if (bang >= 0) s = s[(bang + 1)..];
        if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s = s[..^4];
        return s.ToLowerInvariant() switch
        {
            "msedge" => "Edge",
            "chrome" => "Chrome",
            "firefox" or "308046b0af4a39cb" => "Firefox",
            "" => "Media",
            _ => char.ToUpperInvariant(s[0]) + s[1..]
        };
    }

    static string Format(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }
}
