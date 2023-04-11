// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

// Derived from https://github.com/dotnet/BenchmarkDotNet
// Licensed under the MIT License

using System;
using System.Diagnostics;

namespace Nethermind.Init.Cpu;

public class ConsoleExitHandler : IDisposable
{
    private readonly Process process;

    public ConsoleExitHandler(Process process)
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
        catch (PlatformNotSupportedException)
        {
            // Thrown when running in Xamarin
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
        catch (PlatformNotSupportedException)
        {
            // Thrown when running in Xamarin
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
        catch
        {
            // we don't care about exceptions here, it's shutdown and we just try to cleanup whatever we can
        }
    }
}
