// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Autofac.Core;
using Nethermind.Api.Extensions;
using Nethermind.Api.Steps;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Evm;
using Nethermind.Evm.GasPolicy;
using Nethermind.Init.Steps;
using Nethermind.Logging;

namespace Nethermind.IlEvm;

/// <summary>Compiles hot EVM contracts to native code through the core's frame code runner hook.</summary>
public sealed class IlEvmPlugin(IIlEvmConfig config) : INethermindPlugin
{
    public string Name => "IL-EVM";

    public string Description => "Compiles hot EVM contracts to native code for faster read-only calls.";

    public string Author => "Nethermind";

    /// <remarks>
    /// On whenever configured, so that a core the plugin cannot run on gets a warning at startup
    /// (<see cref="IlEvmUnsupportedStep"/>) rather than silence; the module then attaches nothing.
    /// </remarks>
    public bool Enabled => config.Enabled;

    public IModule Module => new IlEvmModule(config);
}

/// <summary>Attaches the compiler's frame runner where <see cref="IIlEvmConfig.Scope"/> says.</summary>
public sealed class IlEvmModule(IIlEvmConfig config) : Module
{
    /// <summary>Why this process cannot run the compiler, or null when it can.</summary>
    /// <remarks>
    /// Fails closed: a core with another frame code runner contract than <see cref="ContractVersion"/>, read from the
    /// core loaded, keeps the interpreter.
    /// </remarks>
    public static string? Unsupported()
    {
        if (!RuntimeFeature.IsDynamicCodeSupported)
            return "dynamic code is not supported";
        if (FrameCodeRunner<EthereumGasPolicy>.ContractVersion != ContractVersion)
            return $"the core's frame code runner contract is version {FrameCodeRunner<EthereumGasPolicy>.ContractVersion}, the plugin was built for {ContractVersion}";
        return null;
    }

    /// <summary>The frame code runner contract version the plugin is built for.</summary>
    public const int ContractVersion = 1;

    protected override void Load(ContainerBuilder builder)
    {
        if (Unsupported() is not null)
        {
            // Nothing attached; the step says why.
            builder.AddStep(typeof(IlEvmUnsupportedStep));
            return;
        }

        IlEvmScope scope = Configure(config);

        // Block processing and everything else get the block runner; the read-only query environments get their own,
        // so calls keep their rules - run-time compiles under Legacy, live profiling under Adaptive, live promotion under
        // Simple - whatever block processing does. With Scope=All every kind of read-only query the core has gets it, as before the block runner existed.
        // A measurement run gets the timed runners instead, so the production ones carry nothing of the timing.
        ReadOnlyQueryKind surfaces = scope == IlEvmScope.All ? AllQueryKinds : config.Surfaces;
        FrameCodeRunner<EthereumGasPolicy> blockRunner = config.Stats ? IlEvmTimedFrameRunner.Block : IlEvmFrameRunner.ForBlocks;
        FrameCodeRunner<EthereumGasPolicy> queryRunner = config.Stats ? IlEvmTimedFrameRunner.Instance : IlEvmFrameRunner.ForQueries;
        if (scope == IlEvmScope.All)
            builder.RegisterInstance(blockRunner).As<FrameCodeRunner<EthereumGasPolicy>>();
        if (surfaces != ReadOnlyQueryKind.None)
            builder.AddSingleton<IReadOnlyQueryModule>(new IlEvmQueryModule(surfaces, queryRunner));
        builder.AddStep(typeof(IlEvmStartupStep));
    }

    /// <summary>Every kind of read-only query the core has: Call alone, since the hook's API dropped Simulate and Trace.</summary>
    public const ReadOnlyQueryKind AllQueryKinds = ReadOnlyQueryKind.Call;

    /// <summary>
    /// Sets the plugin's rules and switches from <paramref name="config"/>, as a node's load does; tools that attach the
    /// runners themselves, as the replay harness, call it too.
    /// </summary>
    /// <returns>Where compiled code runs: the config's scope, or All where <see cref="IlEvmPrefer.Blocks"/> needs it.</returns>
    internal static IlEvmScope Configure(IIlEvmConfig config)
    {
        IlEvmSettings.Enabled = true;
        IlEvmSettings.Threshold = Math.Max(1, config.Threshold);
        // 0 never compiles on the opcode count, which a long counter only reaches at its end.
        IlEvmSettings.OpcodeThreshold = config.OpcodeThreshold > 0 ? config.OpcodeThreshold : long.MaxValue;
        IlEvmSettings.HotOpcodesPerSecond = Math.Max(0, config.HotOpcodesPerSecond);
        IlEvmSettings.CompileSynchronously = config.CompileSynchronously;
        IlEvmSettings.Profile = config.Profile;
        IlEvmSettings.Stats = config.Stats;
        // A measurement run times compiled code on every frame it runs; no sample of it may go to the interpreter.
        if (config.Stats)
            IlEvmSettings.SelfCheck = false;
        IlEvmSettings.BlockRuntimeCompile = config.BlockRuntimeCompile;
        IlEvmSettings.BlockSelfCheck = config.BlockSelfCheck;

        // One set of rules per process. Measurement and profiling keep their own; the diagnostics' counters exist only
        // on the Legacy path.
        IlMode mode = config.Stats ? IlMode.Stats
            : config.Profile ? IlMode.Profile
            : config.Selection == IlEvmSelection.Legacy || IlEvmDiagnostics.Enabled ? IlMode.Legacy
            : config.Selection == IlEvmSelection.Simple ? IlMode.Simple
            : IlMode.Adaptive;
        IlEvmSettings.Mode = mode;
        // Live profiling of blocks needs block processing's runner attached.
        IlEvmScope scope = config.Scope;
        IlEvmSettings.ScopeForcedToAll = mode == IlMode.Adaptive && config.Prefer == IlEvmPrefer.Blocks && scope != IlEvmScope.All;
        if (IlEvmSettings.ScopeForcedToAll)
            scope = IlEvmScope.All;
        IlEvmSettings.Scope = scope;
        IlEvmSettings.Prefer = config.Prefer;
        IlRuntime.QueryCounting = mode == IlMode.Adaptive && config.Prefer == IlEvmPrefer.Rpc;
        IlRuntime.BlocksCounting = false;
        IlRuntime.BlockThreadsMask = config.BlockThreads switch
        {
            IlEvmBlockThreads.Main => IlRuntime.RunMain,
            IlEvmBlockThreads.Workers => IlRuntime.RunWorkers,
            _ => IlRuntime.RunMain | IlRuntime.RunWorkers,
        };
        IlEvmSettings.Gate = config.Gate;
        IlLive.BudgetBytes = (long)Math.Max(0, config.LiveBudgetMiB) << 20;
        IlEvmSettings.QueryCheck = config.Check && IlEvmSettings.SelfCheck;
        IlEvmSettings.BlockCheck = config.BlockCheck && IlEvmSettings.SelfCheck;
        IlEvmSettings.BlockDeclineAll = config.BlockDeclineAll;
        // The Simple rules promote from the read-only queries only; Prefer=Blocks counts as None, which the startup step logs.
        IlSimple.Promote = mode == IlMode.Simple && config.Prefer == IlEvmPrefer.Rpc;
        IlSimple.PromoteMs = Math.Max(100, config.PromoteMs);
        IlSimple.PromoteOps = Math.Max(1, config.PromoteOps);
        IlSimple.CompileThreads = Math.Max(1, config.CompileThreads);
        IlSimple.BudgetBytes = (long)Math.Max(0, config.LiveBudgetMiB) << 20;
        return scope;
    }
}

/// <summary>Warns, at startup, that the plugin is enabled on a core it cannot run on, and attaches nothing.</summary>
[RunnerStepDependencies(typeof(InitializeBlockchain))]
public sealed class IlEvmUnsupportedStep(ILogManager logManager) : IStep
{
    public Task Execute(CancellationToken cancellationToken)
    {
        ILogger logger = logManager.GetClassLogger<IlEvmUnsupportedStep>();
        if (IlEvmModule.Unsupported() is { } why && logger.IsWarn)
            logger.Warn($"IL-EVM: not attached, {why}; every frame runs on the interpreter");
        return Task.CompletedTask;
    }
}

/// <summary>Attaches the compiler's frame runner to the machines of the read-only query environments.</summary>
/// <param name="runner">The runner to attach: <see cref="IlEvmFrameRunner.Instance"/> unless a measurement run times frames.</param>
public sealed class IlEvmQueryModule(ReadOnlyQueryKind surfaces, FrameCodeRunner<EthereumGasPolicy> runner) : Module, IReadOnlyQueryModule
{
    /// <summary>Attaches the production runner for the rules set now, <see cref="IlEvmFrameRunner.ForQueries"/>.</summary>
    public IlEvmQueryModule(ReadOnlyQueryKind surfaces) : this(surfaces, IlEvmFrameRunner.ForQueries)
    {
    }

    public ReadOnlyQueryKind AppliesTo => surfaces;

    /// <summary>The runner the module attaches.</summary>
    public FrameCodeRunner<EthereumGasPolicy> Runner { get; } = runner;

    protected override void Load(ContainerBuilder builder) =>
        builder.RegisterInstance(Runner).As<FrameCodeRunner<EthereumGasPolicy>>();
}
