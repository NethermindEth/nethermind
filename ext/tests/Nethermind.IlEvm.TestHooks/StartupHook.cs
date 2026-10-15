// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited

using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

/// <summary>Attaches the plugin's frame runner to every machine the stock EVM test suite builds through its test base.</summary>
/// <remarks>
/// Every contract compiles on its first frame, on the frame's own thread, so the suite runs compiled code wherever
/// it runs without an instruction tracer (NETHERMIND_EVM_TEST_UNTRACED=1 makes that most of it).
/// </remarks>
internal class StartupHook
{
    public static void Initialize()
    {
        // The plugin is not in the test host's dependency manifest; resolve it next to the host.
        AssemblyLoadContext.Default.Resolving += static (context, name) =>
        {
            string path = Path.Combine(AppContext.BaseDirectory, name.Name + ".dll");
            return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        };
        // Every contract compiles on its first frame by the plugin's Legacy rules, unless NETHERMIND_ILEVM_MODE names
        // others; set before anything reads the plugin's settings.
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_MODE")))
            Environment.SetEnvironmentVariable("NETHERMIND_ILEVM_MODE", "Legacy");
        Attach();
    }

    /// <remarks>Separate, so its assemblies load after the resolver is in place.</remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Attach()
    {
        Nethermind.IlEvm.IlEvmSettings.Enabled = true;
        Nethermind.IlEvm.IlEvmSettings.Threshold = 1;
        Nethermind.IlEvm.IlEvmSettings.CompileSynchronously = true;
        Nethermind.Evm.Test.VirtualMachineTestsBase.FrameCodeRunnerFactory = static () => Nethermind.IlEvm.IlEvmFrameRunner.Instance;
        Console.WriteLine($"il-evm test hooks: runner attached ({Nethermind.IlEvm.IlEvmModule.Unsupported() ?? "supported"})");
        AppDomain.CurrentDomain.ProcessExit += static (_, _) => Console.WriteLine(
            $"il-evm test hooks: compiled contracts {Nethermind.IlEvm.IlEvmSettings.CompiledContracts}, failed {Nethermind.IlEvm.IlEvmSettings.FailedContracts}, deferred {Nethermind.IlEvm.IlEvmSettings.DeferredCompiles}" +
            (Nethermind.IlEvm.IlEvmDiagnostics.Enabled
                ? $", runs {Nethermind.IlEvm.IlEvmDiagnostics.Runs}, chunk calls {Nethermind.IlEvm.IlEvmDiagnostics.ChunkCalls}, hand-backs {Nethermind.IlEvm.IlEvmDiagnostics.Deopts}, failed segments {Nethermind.IlEvm.IlEvmDiagnostics.FailedSegments}"
                : ""));
        AppDomain.CurrentDomain.ProcessExit += static (_, _) =>
        {
            foreach (System.Collections.Generic.KeyValuePair<string, long> failure in Nethermind.IlEvm.IlEvmSettings.FailuresByReason)
                Console.WriteLine($"il-evm test hooks: declined {failure.Value,6} x {failure.Key}");
        };
    }
}
