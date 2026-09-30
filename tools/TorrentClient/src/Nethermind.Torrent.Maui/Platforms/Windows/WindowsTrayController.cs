// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.Maui.Platform;

namespace Nethermind.Torrent.Maui.Platforms.Windows;

internal sealed class WindowsTrayController : IDisposable
{
    private const uint TrayMessage = 0x8001;
    private const uint IconId = 1;
    private const uint NimAdd = 0;
    private const uint NimDelete = 2;
    private const uint NimSetVersion = 4;
    private const uint NifMessage = 1;
    private const uint NifIcon = 2;
    private const uint NifTip = 4;
    private const uint NifShowTip = 0x80;
    private const uint NinSelect = 0x400;
    private const uint NinKeySelect = 0x401;
    private const uint WmLeftButtonUp = 0x202;
    private const uint WmLeftButtonDoubleClick = 0x203;

    private readonly AppWindow _appWindow;
    private readonly IntPtr _handle;
    private readonly Func<bool> _enabled;
    private readonly SubclassProcedure _procedure;
    private readonly IntPtr _icon;
    private readonly bool _ownsIcon;
    private readonly uint _taskbarCreatedMessage;
    private readonly bool _subclassed;
    private bool _iconVisible;
    private bool _collapsed;

    public WindowsTrayController(MauiWinUIWindow window, Func<bool> enabled)
    {
        _appWindow = window.AppWindow;
        _handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        _enabled = enabled;
        _procedure = OnWindowMessage;
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");

        using Process process = Process.GetCurrentProcess();
        string? executable = process.MainModule?.FileName;
        if (executable is not null && ExtractIconEx(executable, 0, out IntPtr large, out IntPtr small, 1) > 0)
        {
            if (large != IntPtr.Zero) DestroyIcon(large);
            _icon = small;
            _ownsIcon = small != IntPtr.Zero;
        }

        if (_icon == IntPtr.Zero)
        {
            _icon = LoadIcon(IntPtr.Zero, (IntPtr)32512);
        }

        _subclassed = SetWindowSubclass(_handle, _procedure, (UIntPtr)IconId, IntPtr.Zero);
        if (_subclassed)
        {
            _appWindow.Changed += OnWindowChanged;
        }
    }

    private void OnWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidPresenterChange || _collapsed || !_subclassed || !_enabled() ||
            sender.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Minimized })
        {
            return;
        }

        if (!AddIcon()) return;

        try
        {
            _collapsed = true;
            sender.Hide();
        }
        catch
        {
            _collapsed = false;
            RemoveIcon();
        }
    }

    private bool AddIcon()
    {
        NotifyIconData data = CreateIconData();
        if (!ShellNotifyIcon(NimAdd, ref data)) return false;

        _iconVisible = true;
        data.Version = 4;
        ShellNotifyIcon(NimSetVersion, ref data);
        return true;
    }

    private void Restore()
    {
        if (!_collapsed) return;

        _collapsed = false;
        _appWindow.IsShownInSwitchers = true;
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.Restore();
        }

        _appWindow.Show();
        SetForegroundWindow(_handle);
        RemoveIcon();
    }

    private IntPtr OnWindowMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam, UIntPtr subclassId, IntPtr data)
    {
        if (message == TrayMessage)
        {
            uint eventId = (uint)lParam.ToInt64() & 0xffff;
            if (eventId is NinSelect or NinKeySelect or WmLeftButtonUp or WmLeftButtonDoubleClick)
            {
                Restore();
                return IntPtr.Zero;
            }
        }
        else if (message == _taskbarCreatedMessage && _collapsed)
        {
            _iconVisible = false;
            if (!AddIcon()) Restore();
        }

        return DefSubclassProc(handle, message, wParam, lParam);
    }

    private NotifyIconData CreateIconData() => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(),
        Window = _handle,
        Id = IconId,
        Flags = NifMessage | NifIcon | NifTip | NifShowTip,
        CallbackMessage = TrayMessage,
        Icon = _icon,
        Tooltip = "Nethermind Torrent Client",
        Info = string.Empty,
        InfoTitle = string.Empty,
    };

    private void RemoveIcon()
    {
        if (!_iconVisible) return;

        NotifyIconData data = CreateIconData();
        ShellNotifyIcon(NimDelete, ref data);
        _iconVisible = false;
    }

    public void Dispose()
    {
        _appWindow.Changed -= OnWindowChanged;
        RemoveIcon();
        if (_subclassed) RemoveWindowSubclass(_handle, _procedure, (UIntPtr)IconId);
        if (_ownsIcon) DestroyIcon(_icon);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public IntPtr Window;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tooltip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid Guid;
        public IntPtr BalloonIcon;
    }

    private delegate IntPtr SubclassProcedure(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam,
        UIntPtr subclassId, IntPtr data);

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("shell32.dll", EntryPoint = "ExtractIconExW", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, out IntPtr large, out IntPtr small, uint count);

    [DllImport("user32.dll", EntryPoint = "LoadIconW")]
    private static extern IntPtr LoadIcon(IntPtr instance, IntPtr resource);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr handle);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(IntPtr handle, SubclassProcedure procedure, UIntPtr subclassId, IntPtr data);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(IntPtr handle, SubclassProcedure procedure, UIntPtr subclassId);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);
}
