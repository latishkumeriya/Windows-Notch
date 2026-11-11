using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using NotchWin.Native;
using NotchWin.Services;
using NotchWin.Views;

namespace NotchWin;

public partial class MainWindow : Window
{
    const int HkRegion = 1, HkFull = 2, HkClipboard = 3, HkToggle = 4;

    // Visible sizes in DIPs. The window is HiddenTop taller and pushed that far above the screen
    // edge so only the bottom rounded corners show, giving the notch shape.
    const double HiddenTop = 10;
    const double CollapsedHeight = 34;
    const double IdleWidth = 180, MediaWidth = 250, ToastWidth = 320;
    const double ExpandedWidth = 536, ExpandedHeight = 204;

    public MediaService Media { get; } = new();
    public ClipboardService Clips { get; } = new();
    public ScreenshotService Shots { get; } = new();

    readonly DispatcherTimer _enterTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    readonly DispatcherTimer _leaveTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(2.2) };
    readonly DispatcherTimer _watchTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };
    readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(10) };

    IntPtr _hwnd;
    IntPtr _previousForeground;
    bool _expanded, _toastActive, _dragging, _capturing, _hiddenForFullscreen;
    Point _dragStart;
    ShotItem? _pressedShot;

    readonly ThemeService _theme = new();
    bool _glass;

    // Spring-animated window size (the acrylic covers the whole window, so the window itself must morph).
    double _width = IdleWidth, _height = CollapsedHeight, _velW, _velH, _targetW = IdleWidth, _targetH = CollapsedHeight;
    bool _animating;
    TimeSpan _lastFrame;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;

        _enterTimer.Tick += (_, _) => { _enterTimer.Stop(); Expand(); };
        _leaveTimer.Tick += (_, _) =>
        {
            _leaveTimer.Stop();
            if (!Island.IsMouseOver && !_dragging && !SearchBox.IsKeyboardFocusWithin) Collapse();
        };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); _toastActive = false; UpdateCompact(); };
        _watchTimer.Tick += (_, _) => WatchFullscreen();
        _clockTimer.Tick += (_, _) => Clock.Text = DateTime.Now.ToString("ddd h:mm tt");

        Media.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MediaService.IsPlaying) or nameof(MediaService.HasSession))
            {
                UpdateBars();
                UpdateCompact();
            }
        };

        Deactivated += (_, _) => { if (_expanded && !Island.IsMouseOver && !_capturing) Collapse(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Collapse(); };
        SourceInitialized += OnSourceInitialized;
        Loaded += async (_, _) =>
        {
            Clock.Text = DateTime.Now.ToString("ddd h:mm tt");
            _clockTimer.Start();
            _watchTimer.Start();
            UpdateCompact();
            await Task.WhenAll(Media.InitAsync(), Shots.LoadRecentAsync());
        };
        Closed += OnClosed;
        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        StartupItem.IsChecked = StartupService.IsEnabled;

        _theme.Apply(Resources, glass: _theme.TransparencyEnabled);
        _theme.Changed += ApplyTheme;
        DpiChanged += (_, _) => Dispatcher.InvokeAsync(ApplyBounds);
    }

    // ---------------------------------------------------------------- window plumbing

    void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;

        // Tool window: hidden from Alt+Tab and the taskbar.
        int ex = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE, (ex | NativeMethods.WS_EX_TOOLWINDOW) & ~NativeMethods.WS_EX_APPWINDOW);

        HwndSource.FromHwnd(_hwnd)!.AddHook(WndProc);
        NativeMethods.AddClipboardFormatListener(_hwnd);

        const uint mods = NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT;
        NativeMethods.RegisterHotKey(_hwnd, HkRegion, mods, 'S');
        NativeMethods.RegisterHotKey(_hwnd, HkFull, mods, 'F');
        NativeMethods.RegisterHotKey(_hwnd, HkClipboard, mods, 'V');
        NativeMethods.RegisterHotKey(_hwnd, HkToggle, mods, 'N');

        NativeMethods.SetDwmAttribute(_hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, NativeMethods.DWMWCP_ROUND);
        NativeMethods.SetDwmAttribute(_hwnd, NativeMethods.DWMWA_SYSTEMBACKDROP_TYPE, NativeMethods.DWMSBT_NONE);
        ApplyTheme();
        ApplyBounds();
    }

    /// <summary>Re-reads nothing itself; pushes the current Windows theme into DWM and the XAML brushes.</summary>
    void ApplyTheme()
    {
        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.SetDwmAttribute(_hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, _theme.IsDark ? 1 : 0);
            // Respect Settings > Personalization > Colors > Transparency effects.
            _glass = _theme.TransparencyEnabled && NativeMethods.SetAccent(_hwnd, true, _theme.TintAbgr);
            if (!_glass) NativeMethods.SetAccent(_hwnd, false, 0);
        }
        _theme.Apply(Resources, _glass);
    }

    void ApplyBounds()
    {
        if (_hwnd == IntPtr.Zero) return;
        var monitor = NativeMethods.PrimaryMonitor();
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int w = (int)Math.Round(_width * scale);
        int h = (int)Math.Round((_height + HiddenTop) * scale);
        int x = monitor.Left + (monitor.Width - w) / 2;
        int y = monitor.Top - (int)Math.Round(HiddenTop * scale);
        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, x, y, w, h, NativeMethods.SWP_NOACTIVATE);
    }

    void OnDisplayChanged(object? sender, EventArgs e) => Dispatcher.InvokeAsync(ApplyBounds);

    void OnClosed(object? sender, EventArgs e)
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        NativeMethods.RemoveClipboardFormatListener(_hwnd);
        foreach (var id in new[] { HkRegion, HkFull, HkClipboard, HkToggle })
            NativeMethods.UnregisterHotKey(_hwnd, id);
        Clips.Flush();
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case NativeMethods.WM_CLIPBOARDUPDATE:
                Dispatcher.InvokeAsync(Clips.OnClipboardChanged, DispatcherPriority.Background);
                break;

            // Clicking the notch must not steal focus from the app you're working in,
            // otherwise "click to paste" would paste into the notch itself.
            case NativeMethods.WM_MOUSEACTIVATE when !SearchBox.IsMouseOver:
                handled = true;
                return new IntPtr(NativeMethods.MA_NOACTIVATE);

            case NativeMethods.WM_MOUSEACTIVATE:
                // Clicking into search activates us; remember where to paste back to.
                var fg = NativeMethods.GetForegroundWindow();
                if (fg != _hwnd) _previousForeground = fg;
                break;

            case NativeMethods.WM_HOTKEY:
                handled = true;
                OnHotkey(wParam.ToInt32());
                break;
        }
        return IntPtr.Zero;
    }

    void OnHotkey(int id)
    {
        switch (id)
        {
            case HkRegion: _ = CaptureAsync(region: true); break;
            case HkFull: _ = CaptureAsync(region: false); break;
            case HkClipboard:
                if (_expanded && TabClip.IsChecked == true) { Collapse(); break; }
                _previousForeground = NativeMethods.GetForegroundWindow();
                ShowNotch();
                Expand(TabClip);
                Activate();
                SearchBox.Focus();
                SearchBox.SelectAll();
                break;
            case HkToggle:
                if (_expanded) Collapse();
                else { ShowNotch(); Expand(TabMedia); }
                break;
        }
    }

    void WatchFullscreen()
    {
        if (_capturing) return;
        // QUNS_BUSY (2), QUNS_RUNNING_D3D_FULL_SCREEN (3), QUNS_PRESENTATION_MODE (4)
        if (NativeMethods.SHQueryUserNotificationState(out int state) == 0)
        {
            bool fullscreen = state is 2 or 3 or 4;
            if (fullscreen && IsVisible && !_expanded) { _hiddenForFullscreen = true; Hide(); }
            else if (!fullscreen && _hiddenForFullscreen) ShowNotch();
        }
        // Other topmost windows (e.g. the taskbar) can jump above us; reassert.
        if (IsVisible)
            NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    void ShowNotch()
    {
        _hiddenForFullscreen = false;
        if (!IsVisible) Show();
    }

    // ---------------------------------------------------------------- expand / collapse

    void Island_MouseEnter(object sender, MouseEventArgs e)
    {
        _leaveTimer.Stop();
        if (!_expanded) _enterTimer.Start();
    }

    void Island_MouseLeave(object sender, MouseEventArgs e)
    {
        _enterTimer.Stop();
        if (_expanded) _leaveTimer.Start();
    }

    void Island_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_expanded) { _enterTimer.Stop(); Expand(); }
    }

    void Expand(RadioButton? tab = null)
    {
        if (tab != null) tab.IsChecked = true;
        if (_expanded) return;
        _expanded = true;
        Media.Live = true;
        _leaveTimer.Stop();
        if (TabClip.IsChecked == true) Clips.View.Refresh(); // refresh relative times

        Compact.Visibility = Visibility.Collapsed;
        Expanded.Visibility = Visibility.Visible;
        AnimateIsland(ExpandedWidth, ExpandedHeight);
        Fade(Expanded, 1, 220, delay: 110);
    }

    void Collapse()
    {
        if (!_expanded) return;
        _expanded = false;
        Media.Live = false;
        if (SearchBox.IsKeyboardFocusWithin) Keyboard.ClearFocus();
        Fade(Expanded, 0, 90, then: () => { if (!_expanded) Expanded.Visibility = Visibility.Collapsed; });
        Compact.Visibility = Visibility.Visible;
        UpdateCompact();
    }

    void UpdateCompact()
    {
        if (_expanded) return;
        bool playing = Media.HasSession && Media.IsPlaying;
        CompactToast.Visibility = _toastActive ? Visibility.Visible : Visibility.Collapsed;
        CompactMedia.Visibility = !_toastActive && playing ? Visibility.Visible : Visibility.Collapsed;
        AnimateIsland(_toastActive ? ToastWidth : playing ? MediaWidth : IdleWidth, CollapsedHeight);
    }

    void AnimateIsland(double width, double height)
    {
        _targetW = width;
        _targetH = height;
        if (_animating) return;
        _animating = true;
        _lastFrame = TimeSpan.Zero;
        CompositionTarget.Rendering += OnAnimationFrame;
    }

    void OnAnimationFrame(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (now == _lastFrame) return;
        double dt = _lastFrame == TimeSpan.Zero ? 1 / 60.0 : Math.Min((now - _lastFrame).TotalSeconds, 1 / 30.0);
        _lastFrame = now;

        Spring(ref _width, ref _velW, _targetW, dt);
        Spring(ref _height, ref _velH, _targetH, dt);

        bool settled = Math.Abs(_targetW - _width) < 0.5 && Math.Abs(_targetH - _height) < 0.5 &&
                       Math.Abs(_velW) < 5 && Math.Abs(_velH) < 5;
        if (settled)
        {
            _width = _targetW;
            _height = _targetH;
            _velW = _velH = 0;
            _animating = false;
            CompositionTarget.Rendering -= OnAnimationFrame;
        }
        ApplyBounds();
    }

    // Slightly under-damped (ratio ~0.77) for the small overshoot of the Dynamic Island.
    static void Spring(ref double x, ref double v, double target, double dt)
    {
        const double stiffness = 380, damping = 30;
        v += (stiffness * (target - x) - damping * v) * dt;
        x += v * dt;
    }

    static void Fade(UIElement element, double to, int ms, int delay = 0, Action? then = null)
    {
        var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { BeginTime = TimeSpan.FromMilliseconds(delay) };
        if (then != null) anim.Completed += (_, _) => then();
        element.BeginAnimation(OpacityProperty, anim);
    }

    /// <summary>Briefly widens the collapsed notch to show a status, like a live activity.</summary>
    void Flash(string text, string glyph, ImageSource? image = null)
    {
        ToastText.Text = text;
        ToastGlyph.Text = glyph;
        ToastImage.ImageSource = image;
        ToastImageHost.Visibility = image != null ? Visibility.Visible : Visibility.Collapsed;
        ToastGlyph.Visibility = image != null ? Visibility.Collapsed : Visibility.Visible;
        _toastActive = true;
        _toastTimer.Stop();
        _toastTimer.Start();
        UpdateCompact();
    }

    void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (MediaPanel == null) return; // fires during InitializeComponent
        MediaPanel.Visibility = TabMedia.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ClipPanel.Visibility = TabClip.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ShotsPanel.Visibility = TabShots.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (TabClip.IsChecked == true) Clips.View.Refresh();
    }

    // ---------------------------------------------------------------- media

    void UpdateBars()
    {
        var bars = new[] { Bar0, Bar1, Bar2, Bar3 };
        var periods = new[] { 380, 520, 300, 450 };
        for (int i = 0; i < bars.Length; i++)
        {
            var scale = (ScaleTransform)bars[i].RenderTransform;
            if (Media.IsPlaying)
            {
                var anim = new DoubleAnimation(0.25, 1.0, TimeSpan.FromMilliseconds(periods[i]))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                };
                // Every frame forces the acrylic window to recompose; 30fps looks the same and halves the cost.
                Timeline.SetDesiredFrameRate(anim, 30);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
            }
            else
            {
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                scale.ScaleY = 0.3;
            }
        }
    }

    void PlayPause_Click(object sender, RoutedEventArgs e) => Media.TogglePlay();
    void Next_Click(object sender, RoutedEventArgs e) => Media.Next();
    void Prev_Click(object sender, RoutedEventArgs e) => Media.Previous();
    void Source_Click(object sender, RoutedEventArgs e) => Media.CycleSource();
    void Seek_Down(object sender, MouseButtonEventArgs e) => Media.IsSeeking = true;

    void Seek_Up(object sender, MouseButtonEventArgs e)
    {
        // Let the slider apply the click position before reading it.
        Dispatcher.InvokeAsync(() =>
        {
            Media.Seek(Seek.Value);
            Media.IsSeeking = false;
        }, DispatcherPriority.Input);
    }

    // ---------------------------------------------------------------- clipboard

    void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Clips.Filter = SearchBox.Text;
    }

    void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Clips.FirstVisible is { } first)
        {
            e.Handled = true;
            PasteClip(first, copyOnly: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        }
    }

    void ClipItem_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ClipItem item)
            PasteClip(item, copyOnly: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
    }

    void PasteClip(ClipItem item, bool copyOnly)
    {
        Clips.Copy(item);
        var target = _previousForeground;
        _previousForeground = IntPtr.Zero;
        SearchBox.Clear();
        Collapse();

        if (copyOnly)
        {
            Flash("Copied", "\uE8C8");
            return;
        }

        // If we took focus via the hotkey, hand it back before pasting.
        if (IsActive && target != IntPtr.Zero) NativeMethods.SetForegroundWindow(target);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (NativeMethods.GetForegroundWindow() != _hwnd) NativeMethods.SendCtrlV();
            else Flash("Copied", "\uE8C8");
        };
        timer.Start();
    }

    void Pin_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ClipItem item) Clips.TogglePin(item);
    }

    void DeleteClip_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ClipItem item) Clips.Remove(item);
    }

    void OpenLink_Click(object sender, RoutedEventArgs e)
    {
        // Re-validate: only ever hand http(s) URLs to the shell, never paths or other protocols.
        if ((sender as FrameworkElement)?.DataContext is ClipItem item && ClipboardService.TryGetWebUrl(item.Text, out var uri))
        {
            Open(uri.AbsoluteUri);
            Collapse();
        }
    }

    void ClearClips_Click(object sender, RoutedEventArgs e) => Clips.ClearUnpinned();

    // ---------------------------------------------------------------- screenshots

    void Region_Click(object sender, RoutedEventArgs e) => _ = CaptureAsync(region: true);
    void FullScreen_Click(object sender, RoutedEventArgs e) => _ = CaptureAsync(region: false);

    async Task CaptureAsync(bool region)
    {
        if (_capturing) return;
        _capturing = true;
        bool wasVisible = IsVisible;
        try
        {
            Collapse();
            // Hide the notch so it isn't in the picture.
            Hide();
            await Task.Delay(250);
            var (image, bounds) = ScreenshotService.CaptureMonitorUnderCursor();
            if (wasVisible) Show();

            var result = image;
            if (region)
            {
                var picker = new RegionSelectWindow(image, bounds);
                result = picker.ShowDialog() == true ? picker.Result : null;
            }
            if (result == null) return;

            var shot = Shots.Save(result);
            Clips.SuppressNextChange();
            ScreenshotService.CopyToClipboard(shot);
            Flash("Screenshot copied", "\uE722", shot.Thumb);
        }
        catch (Exception ex)
        {
            Flash("Screenshot failed: " + ex.Message, "\uE783");
        }
        finally
        {
            if (wasVisible && !IsVisible) Show();
            _capturing = false;
        }
    }

    void Shot_PreviewDown(object sender, MouseButtonEventArgs e)
    {
        _pressedShot = (sender as FrameworkElement)?.DataContext as ShotItem;
        _dragStart = e.GetPosition(this);
    }

    void Shot_MouseMove(object sender, MouseEventArgs e)
    {
        if (_pressedShot == null || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(this);
        if (Math.Abs(p.X - _dragStart.X) < 6 && Math.Abs(p.Y - _dragStart.Y) < 6) return;

        var shot = _pressedShot;
        _pressedShot = null;
        _dragging = true;
        try
        {
            var data = new DataObject(DataFormats.FileDrop, new[] { shot.Path });
            DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Copy);
        }
        finally
        {
            _dragging = false;
            if (!Island.IsMouseOver) _leaveTimer.Start();
        }
    }

    void Shot_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ShotItem shot && shot == _pressedShot)
        {
            _pressedShot = null;
            if (Shots.Owns(shot)) Open(shot.Path);
            Collapse();
        }
    }

    void CopyShot_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ShotItem shot) return;
        Clips.SuppressNextChange();
        ScreenshotService.CopyToClipboard(shot);
        Collapse();
        Flash("Copied", "\uE8C8", shot.Thumb);
    }

    void EditShot_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ShotItem shot) return;
        if (!Shots.Owns(shot)) return;
        // "edit" verb opens the user's registered image editor (Paint by default) without
        // resolving an executable name ourselves.
        try { Process.Start(new ProcessStartInfo(shot.Path) { Verb = "edit", UseShellExecute = true }); }
        catch { Open(shot.Path); }
        Collapse();
    }

    void DeleteShot_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ShotItem shot) Shots.Delete(shot);
    }

    void ShotScroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // Vertical wheel scrolls the horizontal strip.
        var sv = (ScrollViewer)sender;
        sv.ScrollToHorizontalOffset(sv.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    // ---------------------------------------------------------------- menu

    void Startup_Click(object sender, RoutedEventArgs e) => StartupService.SetEnabled(StartupItem.IsChecked);
    void OpenFolder_Click(object sender, RoutedEventArgs e) => Open(Shots.Folder);
    void Pause_Click(object sender, RoutedEventArgs e)
    {
        Clips.Paused = PauseItem.IsChecked;
        Flash(Clips.Paused ? "Clipboard history paused" : "Clipboard history on", Clips.Paused ? "\uE769" : "\uE77F");
    }

    void Exit_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();

    static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { }
    }
}
