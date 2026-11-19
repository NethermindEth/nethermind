// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

// Derived from https://github.com/dotnet/BenchmarkDotNet
// Licensed under the MIT License

using System;
using System.Diagnostics;
using Nethermind.Logging;

namespace Nethermind.Core.Cpu;

internal class ConsoleExitHandler : IDisposable
{
    private readonly Process process;

    internal ConsoleExitHandler(Process process)
    {
        this.process = process;

        Attach();
    }

    public void Dispose() => Detach();

    private void Attach()
    {
        process.Exited += ProcessOnExited;
        try
        {
            Console.CancelKeyPress += CancelKeyPressHandlerCallback;
        }
        catch (PlatformNotSupportedException e)
        {
            // Thrown when running in Xamarin
            LogSkipped("Cannot handle Ctrl+C", e);
        }
        AppDomain.CurrentDomain.ProcessExit += ProcessExitEventHandlerHandlerCallback;
    }

    private void Detach()
    {
        process.Exited -= ProcessOnExited;
        try
        {
            Console.CancelKeyPress -= CancelKeyPressHandlerCallback;
        }
        catch (PlatformNotSupportedException e)
        {
            // Thrown when running in Xamarin
            LogSkipped("Cannot detach the Ctrl+C handler", e);
        }
        AppDomain.CurrentDomain.ProcessExit -= ProcessExitEventHandlerHandlerCallback;
    }

    // the process has exited, so we detach the events
    private void ProcessOnExited(object? sender, EventArgs e) => Detach();

    // the user has clicked Ctrl+C so we kill the entire process tree
    private void CancelKeyPressHandlerCallback(object? sender, ConsoleCancelEventArgs e) => KillProcessTree();

    // the user has closed the console window so we kill the entire process tree
    private void ProcessExitEventHandlerHandlerCallback(object? sender, EventArgs e) => KillProcessTree();

    internal void KillProcessTree()
    {
        try
        {
            process.KillTree(); // we need to kill entire process tree, not just the process itself
        }
        catch (Exception e)
        {
            // we don't care about exceptions here, it's shutdown and we just try to cleanup whatever we can
            LogSkipped($"Failed to kill the process tree of {process.Id}", e);
        }
    }

    private static void LogSkipped(string message, Exception e)
    {
        ILogger logger = Static.LogManager.GetClassLogger<ConsoleExitHandler>();
        if (logger.IsDebug) logger.Debug($"{message}: {e.Message}");
    }
}
