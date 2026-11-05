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
using Nethermind.Core;
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
            if (exception.InnerException is null) Environment.Exit(2);
        }
        // An unusable backend must fault verification: a false verdict would mark a valid block INVALID.
        if (mode is "missing" or "abi")
        {
            NativeLeanProofVerifier verifier = NativeLeanProofVerifier.Instance;
            ExpectFault("VerifyRecursiveStark", () => verifier.VerifyRecursiveStark(default, new byte[32], new byte[1]));
            ExpectFault("VerifyLeanSphincs", () => verifier.VerifyLeanSphincs(default, default, new byte[Eip8288Constants.LeanSphincsWitnessBytes]));
            ExpectFault("VerifyLeanStark", () => verifier.VerifyLeanStark(default, default, new byte[1]));
        }
        Environment.Exit(0);
    }

    private static void ExpectFault(string name, Func<bool> verify)
    {
        try
        {
            Console.Error.WriteLine($"{name} returned a verdict ({verify()}) instead of faulting");
            Environment.Exit(3);
        }
        catch (InvalidOperationException) { }
    }
}

[NonParallelizable]
public class NativeBackendStartupTests
{
    [TestCase("missing", "DllNotFoundException")]
    [TestCase("abi", "Lean verification key unavailable")]
    [TestCase("key", "recursive guest key does not match")]
    [TestCase("old-key", "recursive guest key does not match")]
    [TestCase("bounds", "acceptance bounds do not match")]
    // The runtime switches hide features as a CPU without them would, and the check must fail before any native call. On x64,
    // EnableAES also covers PCLMULQDQ and EnableAVX2 the rest of x86-64-v3 (BMI1/2, FMA, LZCNT); arm64 has no v3 counterpart.
    [TestCase("cpu", "needs x86-64-v3 with PCLMULQDQ", "EIP-8288 requires a CPU the native Lean backend supports", "DOTNET_EnableAES", "DOTNET_EnableArm64Aes")]
    [TestCase("cpu-v3", "needs x86-64-v3 with PCLMULQDQ", "EIP-8288 requires a CPU the native Lean backend supports", "DOTNET_EnableAVX2", null)]
    public async Task Actual_backend_startup_fails_closed_in_a_fresh_process(string mode, string expected,
        string reason = "EIP-8288 requires the pinned native Lean backend", string x64Switch = null, string arm64Switch = null)
    {
        string cpuSwitch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? arm64Switch : x64Switch;
        if (mode.StartsWith("cpu", StringComparison.Ordinal) && cpuSwitch is null) Assert.Ignore("No such CPU feature on this architecture.");
        ProcessStartInfo start = new("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.Environment[NativeStartupProbe.Variable] = mode;
        if (cpuSwitch is not null) start.Environment[cpuSwitch] = "0";
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
                Assert.That(diagnostics, Does.Contain(reason));
                Assert.That(diagnostics, Does.Contain(expected));
            }
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }
}
