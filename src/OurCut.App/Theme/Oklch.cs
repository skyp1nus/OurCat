using Avalonia.Media;

namespace OurCut.App.Theme;

/// <summary>Converts CSS <c>oklch()</c> colours from the design to sRGB.</summary>
public static class Oklch
{
    public static Color ToColor(double l, double c, double hueDegrees, double alpha = 1)
    {
        double h = hueDegrees * Math.PI / 180;
        double a = c * Math.Cos(h), b = c * Math.Sin(h);
        double l_ = l + 0.3963377774 * a + 0.2158037573 * b;
        double m_ = l - 0.1055613458 * a - 0.0638541728 * b;
        double s_ = l - 0.0894841775 * a - 1.2914855480 * b;
        double lc = l_ * l_ * l_, mc = m_ * m_ * m_, sc = s_ * s_ * s_;
        double r = 4.0767416621 * lc - 3.3077115913 * mc + 0.2309699292 * sc;
        double g = -1.2684380046 * lc + 2.6097574011 * mc - 0.3413193965 * sc;
        double bl = -0.0041960863 * lc - 0.7034186147 * mc + 1.7076147010 * sc;
        return Color.FromArgb(ToByte(alpha), Encode(r), Encode(g), Encode(bl));
    }

    /// <summary>CSS <c>grayscale(amount)</c> filter.</summary>
    public static Color Grayscale(Color color, double amount)
    {
        double k = 1 - amount;
        double r = color.R, g = color.G, b = color.B;
        double nr = (0.2126 + 0.7874 * k) * r + (0.7152 - 0.7152 * k) * g + (0.0722 - 0.0722 * k) * b;
        double ng = (0.2126 - 0.2126 * k) * r + (0.7152 + 0.2848 * k) * g + (0.0722 - 0.0722 * k) * b;
        double nb = (0.2126 - 0.2126 * k) * r + (0.7152 - 0.7152 * k) * g + (0.0722 + 0.9278 * k) * b;
        return Color.FromArgb(color.A, Clamp(nr), Clamp(ng), Clamp(nb));
    }

    private static byte Encode(double x)
    {
        x = Math.Clamp(x, 0, 1);
        double v = x <= 0.0031308 ? 12.92 * x : 1.055 * Math.Pow(x, 1 / 2.4) - 0.055;
        return (byte)Math.Round(v * 255);
    }

    private static byte ToByte(double unit) => (byte)Math.Round(Math.Clamp(unit, 0, 1) * 255);
    private static byte Clamp(double v) => (byte)Math.Round(Math.Clamp(v, 0, 255));
}
