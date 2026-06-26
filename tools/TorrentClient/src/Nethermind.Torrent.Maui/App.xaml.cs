// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Torrent.Maui;

public partial class App : Application
{
    public App() => InitializeComponent();

    protected override Window CreateWindow(IActivationState? activationState)
    {
        Window window = new(new AppShell())
        {
            Title = "Nethermind Torrent",
            MinimumHeight = 720,
            MinimumWidth = 1120,
        };
        window.Destroying += (_, _) => Nethermind.Torrent.Maui.MainPage.Active?.StopAllForShutdown(TimeSpan.FromSeconds(10));
        return window;
    }
}
