using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Screenline
{
    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] internal struct POINT { public int X, Y; }
        [DllImport("user32.dll")] internal static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(IntPtr hwnd, int id);
        [DllImport("user32.dll")] internal static extern bool GetCursorPos(out POINT point);
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern IntPtr GetDesktopWindow();
        [DllImport("user32.dll")] internal static extern IntPtr GetShellWindow();
        [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll", CharSet=CharSet.Unicode)] internal static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder text, int count);
        [DllImport("gdi32.dll")] internal static extern bool DeleteObject(IntPtr handle);
        [DllImport("user32.dll")] internal static extern bool SetProcessDPIAware();

        internal static bool IsFullscreen(System.Windows.Forms.Screen screen)
        {
            IntPtr handle = GetForegroundWindow();
            if (handle == IntPtr.Zero || handle == GetDesktopWindow() || handle == GetShellWindow()) return false;
            var className = new System.Text.StringBuilder(128);
            GetClassName(handle, className, className.Capacity);
            string name = className.ToString();
            if (name == "Progman" || name == "WorkerW" || name == "Shell_TrayWnd" || name == "Shell_SecondaryTrayWnd") return false;
            RECT rect;
            if (!GetWindowRect(handle, out rect)) return false;
            var bounds = screen.Bounds;
            return rect.Left <= bounds.Left && rect.Top <= bounds.Top && rect.Right >= bounds.Right && rect.Bottom >= bounds.Bottom;
        }

        internal static System.Windows.Media.Imaging.BitmapSource BitmapSource(System.Drawing.Bitmap bitmap)
        {
            IntPtr handle = bitmap.GetHbitmap();
            try
            {
                var image = Imaging.CreateBitmapSourceFromHBitmap(handle, IntPtr.Zero, Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                image.Freeze();
                return image;
            }
            finally { DeleteObject(handle); }
        }
    }
}
