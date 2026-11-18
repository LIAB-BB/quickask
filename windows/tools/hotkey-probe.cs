using System;
using System.Runtime.InteropServices;

class HotkeyProbe
{
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    static int Main()
    {
        bool ok = RegisterHotKey(IntPtr.Zero, 4242, 0x0003 /* Ctrl+Alt */, 0x20 /* Space */);
        int err = Marshal.GetLastWin32Error();
        Console.WriteLine(ok ? "hotkey free" : ("hotkey taken err=" + err));
        if (ok) UnregisterHotKey(IntPtr.Zero, 4242);
        return 0;
    }
}
