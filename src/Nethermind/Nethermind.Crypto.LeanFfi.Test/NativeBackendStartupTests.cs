// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace Nethermind.Crypto.LeanFfi.Test;

internal static class NativeStartupProbe
{
    internal const string Variable = "NETHERMIND_LEAN_STARTUP_PROBE";

    [ModuleInitializer]
    internal static void Run()
    {
        string mode = Environment.GetEnvironmentVariable(Variable);
        if (mode is null) return;
        NativeLibrary.SetDllImportResolver(typeof(NativeLeanProofVerifier).Assembly, (name, _, _) =>
        {
            if (name != "nethermind_lean") return IntPtr.Zero;
            if (mode == "missing") return NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "absent-lean-startup-backend"));
            string library = OperatingSystem.IsWindows() ? "lean_startup_stub.dll"
                : OperatingSystem.IsMacOS() ? "liblean_startup_stub.dylib" : "liblean_startup_stub.so";
            return NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "lean-vectors", library));
        });
        try
        {
            NativeLeanProofVerifier.Instance.EnsureAvailable();
            Console.Error.WriteLine("Incompatible backend unexpectedly passed startup");
            Environment.Exit(1);
        }
        catch (InvalidOperationException exception)
        {
            Console.WriteLine($"{exception.Message}: {exception.InnerException?.GetType().Name}: {exception.InnerException?.Message}");
            Environment.Exit(exception.InnerException is null ? 2 : 0);
        }
    }
}

[NonParallelizable]
public class NativeBackendStartupTests
{
    [TestCase("missing", "DllNotFoundException")]
    [TestCase("abi", "Lean verification key unavailable")]
    [TestCase("key", "recursive guest key does not match")]
    [TestCase("bounds", "acceptance bounds do not match")]
    public async Task Actual_backend_startup_fails_closed_in_a_fresh_process(string mode, string expected)
    {
        ProcessStartInfo start = new("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.Environment[NativeStartupProbe.Variable] = mode;
        using Process process = Process.Start(start);
        Assert.That(process, Is.Not.Null);
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            string diagnostics = await output + await error;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(process.ExitCode, Is.Zero, diagnostics);
                Assert.That(diagnostics, Does.Contain("EIP-8288 requires the pinned native Lean backend"));
                Assert.That(diagnostics, Does.Contain(expected));
            }
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }
}
