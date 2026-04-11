using System.Windows;
using System.Runtime.InteropServices;

namespace WpfAppTest.Utilities;

public static class ApplicationUtilities
{
    public static bool GetMousePosition(out Point mousePosition)
    {
        if (GetCursorPos(out POINT point))
        {
            mousePosition = new Point(point.X, point.Y);
            return true;
        }
        mousePosition = default;
        return false;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);
    public static String GetTimestamp(DateTime value)
    {
        return value.ToString("yyyyMMddHHmmssffff");
    }
}

public struct POINT
{
    public int X;
    public int Y;

    public POINT(int x, int y)
    {
        this.X = x;
        this.Y = y;
    }
}

public static class CursorClipper
{
    /// <summary>
    /// Constrain mouse cursor to the area of the specified UI element.
    /// </summary>
    /// <param name="element">Target UI element.</param>
    /// <returns>True on success.</returns>
    public static bool ClipCursor(FrameworkElement element)
    {
        const double dpi96 = 96.0;

        Point topLeft = element.PointToScreen(new Point(0, 0));

        PresentationSource source = PresentationSource.FromVisual(element);
        if (source?.CompositionTarget == null)
        {
            return false;
        }

        double dpiX = dpi96 * source.CompositionTarget.TransformToDevice.M11;
        double dpiY = dpi96 * source.CompositionTarget.TransformToDevice.M22;

        int width = (int)((element.ActualWidth + 1) * dpiX / dpi96);
        int height = (int)((element.ActualHeight + 1) * dpiY / dpi96);

        OSInterop.RECT rect = new()
        {
            left = (int)topLeft.X,
            top = (int)topLeft.Y,
            right = (int)topLeft.X + width,
            bottom = (int)topLeft.Y + height
        };

        return OSInterop.ClipCursor(ref rect);
    }

    /// <summary>
    /// Remove any mouse cursor constraint.
    /// </summary>
    /// <returns>True on success.</returns>
    public static bool UnClipCursor()
    {
        return OSInterop.ClipCursor(IntPtr.Zero);
    }
}
