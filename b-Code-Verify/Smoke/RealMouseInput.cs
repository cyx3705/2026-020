using System.ComponentModel;
using System.Runtime.InteropServices;

namespace HistoryJanus.Smoke;

internal static class RealMouseInput
{
    private const uint InputMouse = 0;
    private const uint MouseMove = 0x0001;
    private const uint LeftDown = 0x0002;
    private const uint LeftUp = 0x0004;
    private const uint VirtualDesk = 0x4000;
    private const uint Absolute = 0x8000;
    private const int VirtualLeft = 76;
    private const int VirtualTop = 77;
    private const int VirtualWidth = 78;
    private const int VirtualHeight = 79;

    internal readonly record struct ScreenPoint(int X, int Y);
    internal readonly record struct ScreenRect(int Left, int Top, int Right, int Bottom)
    {
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    public static ScreenPoint CursorPosition
    {
        get
        {
            if (!GetCursorPos(out var point))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return new ScreenPoint(point.X, point.Y);
        }
    }

    public static ScreenRect VirtualScreen => new(
        GetSystemMetrics(VirtualLeft),
        GetSystemMetrics(VirtualTop),
        GetSystemMetrics(VirtualLeft) + GetSystemMetrics(VirtualWidth),
        GetSystemMetrics(VirtualTop) + GetSystemMetrics(VirtualHeight));

    public static ScreenRect GetWindowBounds(nint handle)
    {
        if (!GetWindowRect(handle, out var rect))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return new ScreenRect(rect.Left, rect.Top, rect.Right, rect.Bottom);
    }

    public static bool BringToForeground(nint handle) => SetForegroundWindow(handle);
    public static nint ForegroundWindow => GetForegroundWindow();

    public static void Click(ScreenPoint point)
    {
        Move(point);
        Thread.Sleep(80);
        Send(LeftDown);
        Thread.Sleep(80);
        Send(LeftUp);
    }

    public static void Drag(ScreenPoint start, ScreenPoint end, TimeSpan duration)
    {
        Move(start);
        Thread.Sleep(120);
        Send(LeftDown);
        try
        {
            Thread.Sleep(120);
            var steps = Math.Max(24, (int)Math.Ceiling(duration.TotalMilliseconds / 25));
            var delay = Math.Max(10, (int)(duration.TotalMilliseconds / steps));
            for (var i = 1; i <= steps; i++)
            {
                var t = i / (double)steps;
                var eased = t * t * (3 - (2 * t));
                Move(new ScreenPoint(
                    (int)Math.Round(start.X + ((end.X - start.X) * eased)),
                    (int)Math.Round(start.Y + ((end.Y - start.Y) * eased))));
                Thread.Sleep(delay);
            }

            Thread.Sleep(180);
        }
        finally
        {
            Send(LeftUp);
        }
    }

    public static void Move(ScreenPoint point)
    {
        var screen = VirtualScreen;
        var x = Math.Clamp(point.X, screen.Left, screen.Right - 1);
        var y = Math.Clamp(point.Y, screen.Top, screen.Bottom - 1);
        var normalizedX = (int)Math.Round((x - screen.Left) * 65535d / Math.Max(1, screen.Width - 1));
        var normalizedY = (int)Math.Round((y - screen.Top) * 65535d / Math.Max(1, screen.Height - 1));
        Send(MouseMove | Absolute | VirtualDesk, normalizedX, normalizedY);
    }

    public static void ReleaseLeftButton() => Send(LeftUp);

    private static void Send(uint flags, int dx = 0, int dy = 0)
    {
        var input = new Input
        {
            Type = InputMouse,
            Data = new InputUnion
            {
                Mouse = new MouseInput
                {
                    Dx = dx,
                    Dy = dy,
                    Flags = flags,
                },
            },
        };

        if (SendInput(1, [input], Marshal.SizeOf<Input>()) != 1)
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, Input[] inputs, int inputSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(nint handle, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint handle);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
}
