// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.Maui.Hosting;
using Platform.Maui.Linux.Gtk4.Platform;

namespace Nethermind.Torrent.Maui;

public sealed class Program : GtkMauiApplication
{
    protected override MauiApp CreateMauiApp() => LinuxMauiProgram.CreateMauiApp();

    public static void Main(string[] args)
    {
        Program app = new();
        app.Run(args);
    }
}
