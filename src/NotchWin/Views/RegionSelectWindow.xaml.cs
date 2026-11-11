using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NotchWin.Native;

namespace NotchWin.Views;

/// <summary>
/// Shows a frozen capture of one monitor and lets the user drag a rectangle.
/// Selection is mapped back to pixels by ratio, so it is correct at any DPI.
/// </summary>
public partial class RegionSelectWindow : Window
{
    readonly BitmapSource _shot;
    Point _start;
    bool _selecting;

    public BitmapSource? Result { get; private set; }

    public RegionSelectWindow(BitmapSource shot, NativeMethods.RECT monitor)
    {
        InitializeComponent();
        _shot = shot;
        Shot.Source = shot;

        // Move onto the target monitor first, then maximize there so WPF handles that monitor's DPI.
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, monitor.Left + 20, monitor.Top + 20, 200, 200, NativeMethods.SWP_NOACTIVATE);
        };
        Loaded += (_, _) =>
        {
            WindowState = WindowState.Maximized;
            Activate();
            Focus();
        };
        Root.SizeChanged += (_, _) => UpdateOverlay(Rect.Empty);

        MouseLeftButtonDown += OnDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnUp;
        MouseRightButtonUp += (_, _) => Close();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    void OnDown(object sender, MouseButtonEventArgs e)
    {
        _start = e.GetPosition(Root);
        _selecting = true;
        Hint.Visibility = Visibility.Collapsed;
        CaptureMouse();
    }

    void OnMove(object sender, MouseEventArgs e)
    {
        if (!_selecting) return;
        UpdateOverlay(new Rect(_start, e.GetPosition(Root)));
    }

    void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (!_selecting) return;
        _selecting = false;
        ReleaseMouseCapture();

        var r = new Rect(_start, e.GetPosition(Root));
        if (r.Width < 4 || r.Height < 4)
        {
            Result = _shot;
        }
        else
        {
            var px = ToPixels(r);
            if (px.Width <= 0 || px.Height <= 0) { Close(); return; }
            var crop = new CroppedBitmap(_shot, px);
            crop.Freeze();
            Result = crop;
        }
        DialogResult = true;
    }

    Int32Rect ToPixels(Rect r)
    {
        double sx = _shot.PixelWidth / Root.ActualWidth;
        double sy = _shot.PixelHeight / Root.ActualHeight;
        int x = Math.Clamp((int)Math.Round(r.X * sx), 0, _shot.PixelWidth);
        int y = Math.Clamp((int)Math.Round(r.Y * sy), 0, _shot.PixelHeight);
        int w = Math.Clamp((int)Math.Round(r.Width * sx), 0, _shot.PixelWidth - x);
        int h = Math.Clamp((int)Math.Round(r.Height * sy), 0, _shot.PixelHeight - y);
        return new Int32Rect(x, y, w, h);
    }

    void UpdateOverlay(Rect selection)
    {
        var full = new RectangleGeometry(new Rect(0, 0, Root.ActualWidth, Root.ActualHeight));
        if (selection.IsEmpty)
        {
            Dim.Data = full;
            Selection.Visibility = SizeTag.Visibility = Visibility.Collapsed;
            return;
        }

        Dim.Data = new CombinedGeometry(GeometryCombineMode.Exclude, full, new RectangleGeometry(selection));
        Selection.Visibility = SizeTag.Visibility = Visibility.Visible;
        Canvas.SetLeft(Selection, selection.X);
        Canvas.SetTop(Selection, selection.Y);
        Selection.Width = selection.Width;
        Selection.Height = selection.Height;

        var px = ToPixels(selection);
        SizeText.Text = $"{px.Width} × {px.Height}";
        Canvas.SetLeft(SizeTag, selection.X);
        Canvas.SetTop(SizeTag, selection.Bottom + 6 + 24 > Root.ActualHeight ? selection.Y - 28 : selection.Bottom + 6);
    }
}
