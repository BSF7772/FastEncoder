using System.Runtime.InteropServices;

namespace FastEncoder.Core;

/// <summary>Native taskbar progress (green bar on the taskbar button) + attention flash.</summary>
public sealed class Taskbar
{
    public enum State { None = 0, Indeterminate = 1, Normal = 2, Error = 4, Paused = 8 }

    [ComImport, Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);
        void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
        void SetProgressValue(IntPtr hwnd, ulong completed, ulong total);
        void SetProgressState(IntPtr hwnd, State state);
    }

    [ComImport, Guid("56fdf344-fd6d-11d0-958a-006097c9a090"), ClassInterface(ClassInterfaceType.None)]
    private class TaskbarInstance { }

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO { public uint cbSize; public IntPtr hwnd; public uint dwFlags; public uint uCount; public uint dwTimeout; }

    [DllImport("user32.dll")] private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

    private readonly IntPtr _hwnd;
    private readonly ITaskbarList3? _tb;

    public Taskbar(IntPtr hwnd)
    {
        _hwnd = hwnd;
        try { _tb = (ITaskbarList3)new TaskbarInstance(); _tb.HrInit(); } catch { _tb = null; }
    }

    public void Set(State state, double fraction = 0)
    {
        try
        {
            _tb?.SetProgressState(_hwnd, state);
            if (state is State.Normal or State.Error or State.Paused)
                _tb?.SetProgressValue(_hwnd, (ulong)(Math.Clamp(fraction, 0, 1) * 1000), 1000);
        }
        catch { }
    }

    public void Flash()
    {
        var f = new FLASHWINFO { cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(), hwnd = _hwnd, dwFlags = 3 | 12, uCount = 3 };
        FlashWindowEx(ref f);
    }
}
