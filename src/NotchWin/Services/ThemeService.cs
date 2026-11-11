using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using Windows.UI.ViewManagement;

namespace NotchWin.Services;

/// <summary>
/// Follows Windows' light/dark mode, accent colour and "Transparency effects" setting,
/// and maps them onto Fluent (Windows 11) colour tokens used by the notch's XAML.
/// </summary>
public sealed class ThemeService
{
    const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    readonly UISettings _ui = new();

    public bool IsDark { get; private set; }
    public bool TransparencyEnabled { get; private set; }
    public Color Accent { get; private set; }

    /// <summary>Raised on the UI thread after the theme has been re-read.</summary>
    public event Action? Changed;

    public ThemeService()
    {
        Read();
        _ui.ColorValuesChanged += (_, _) => Refresh();
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
                Refresh();
        };
    }

    /// <summary>Acrylic tint as 0xAABBGGRR (neutral greys, so channel order doesn't matter).</summary>
    public uint TintAbgr => IsDark ? 0x80202020u : 0x80F3F3F3u;

    void Refresh() => Application.Current?.Dispatcher.InvokeAsync(() =>
    {
        Read();
        Changed?.Invoke();
    });

    void Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
        IsDark = (key?.GetValue("AppsUseLightTheme") as int? ?? 1) == 0;
        TransparencyEnabled = (key?.GetValue("EnableTransparency") as int? ?? 1) != 0;
        try
        {
            // Same accent shades Windows 11 uses for controls in each mode.
            var c = _ui.GetColorValue(IsDark ? UIColorType.AccentLight2 : UIColorType.AccentDark1);
            Accent = Color.FromRgb(c.R, c.G, c.B);
        }
        catch
        {
            Accent = IsDark ? Color.FromRgb(0x60, 0xCD, 0xFF) : Color.FromRgb(0x00, 0x5F, 0xB8);
        }
    }

    public void Apply(ResourceDictionary r, bool glass)
    {
        void Set(string key, uint argb) => r[key] = Frozen(new SolidColorBrush(FromArgb(argb)));

        if (IsDark)
        {
            Set("SurfaceBrush", glass ? 0x00000000 : 0xFF202020);
            Set("SurfaceSolidBrush", 0xFF2C2C2C);
            Set("TextBrush", 0xFFFFFFFF);
            Set("SubtleBrush", 0xC5FFFFFF);
            Set("FaintBrush", 0x87FFFFFF);
            Set("CardBrush", 0x0FFFFFFF);
            Set("CardHoverBrush", 0x15FFFFFF);
            Set("CardPressedBrush", 0x08FFFFFF);
            Set("StrokeBrush", 0x12FFFFFF);
            Set("TrackBrush", 0x8BFFFFFF);
            Set("OnAccentBrush", 0xFF000000);
        }
        else
        {
            Set("SurfaceBrush", glass ? 0x00000000 : 0xFFF3F3F3);
            Set("SurfaceSolidBrush", 0xFFFFFFFF);
            Set("TextBrush", 0xE4000000);
            Set("SubtleBrush", 0x9E000000);
            Set("FaintBrush", 0x72000000);
            Set("CardBrush", 0xB3FFFFFF);
            Set("CardHoverBrush", 0x80F9F9F9);
            Set("CardPressedBrush", 0x4DF9F9F9);
            Set("StrokeBrush", 0x0F000000);
            Set("TrackBrush", 0x72000000);
            Set("OnAccentBrush", 0xFFFFFFFF);
        }

        r["AccentBrush"] = Frozen(new SolidColorBrush(Accent));

        // Soft top-down highlight that gives the acrylic a glassy sheen.
        var sheen = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        sheen.GradientStops.Add(new GradientStop(FromArgb(glass ? (IsDark ? 0x1AFFFFFFu : 0x59FFFFFFu) : 0u), 0));
        sheen.GradientStops.Add(new GradientStop(FromArgb(0x00FFFFFF), 0.6));
        r["SheenBrush"] = Frozen(sheen);
    }

    static Color FromArgb(uint v) => Color.FromArgb((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);

    static T Frozen<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}
