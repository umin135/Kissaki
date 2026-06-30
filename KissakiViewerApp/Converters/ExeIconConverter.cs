using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace KissakiViewer.Converters;

/// <summary>Converts an exe file path to its embedded icon as a BitmapSource.</summary>
[ValueConversion(typeof(string), typeof(BitmapSource))]
public sealed class ExeIconConverter : IValueConverter
{
    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr ExtractIcon(IntPtr hInst, string lpszExeFileName, int nIconIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string path || !File.Exists(path)) return null;
        IntPtr hIcon = IntPtr.Zero;
        try
        {
            hIcon = ExtractIcon(IntPtr.Zero, path, 0);
            // ExtractIcon returns IntPtr(1) when the file has no icons
            if (hIcon == IntPtr.Zero || hIcon == new IntPtr(1)) return null;

            var src = Imaging.CreateBitmapSourceFromHIcon(
                hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        catch { return null; }
        finally
        {
            if (hIcon != IntPtr.Zero && hIcon != new IntPtr(1))
                DestroyIcon(hIcon);
        }
    }

    public object? ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => null;
}
