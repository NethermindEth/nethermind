// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#if WINDOWS
using Microsoft.Maui.Platform;
using Nethermind.Torrent.Maui.Platforms.Windows;
#endif

namespace Nethermind.Torrent.Maui;

public partial class App : Application
{
#if WINDOWS
    private WindowsTrayController? _tray;
#endif

    public App() => InitializeComponent();

    protected override Window CreateWindow(IActivationState? activationState)
    {
        Window window = new(new AppShell())
        {
            Title = "Nethermind Torrent Client",
            MinimumHeight = 560,
            MinimumWidth = 600,
        };
        window.Destroying += (_, _) =>
        {
#if WINDOWS
            _tray?.Dispose();
            _tray = null;
#endif
            Nethermind.Torrent.Maui.MainPage.Active?.StopAllForShutdown(TimeSpan.FromSeconds(10));
        };
#if WINDOWS
        window.HandlerChanged += (_, _) =>
        {
            if (_tray is null && window.Handler?.PlatformView is MauiWinUIWindow nativeWindow)
            {
                _tray = new WindowsTrayController(nativeWindow,
                    () => Nethermind.Torrent.Maui.MainPage.Active?.MinimizeToTrayEnabled == true);
            }
        };
#endif
        return window;
    }
}
