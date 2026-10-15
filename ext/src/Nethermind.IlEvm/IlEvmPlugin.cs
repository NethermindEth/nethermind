// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited

using System;
using System.Runtime.CompilerServices;
using Autofac;
using Autofac.Core;
using Nethermind.Api.Extensions;
using Nethermind.Api.Steps;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Evm;
using Nethermind.Evm.GasPolicy;

namespace Nethermind.IlEvm;

/// <summary>Compiles hot EVM contracts to native code through the core's frame code runner hook.</summary>
public sealed class IlEvmPlugin(IIlEvmConfig config) : INethermindPlugin
{
    public string Name => "IL-EVM";

    public string Description => "Compiles hot EVM contracts to native code for faster read-only calls.";

    public string Author => "Nethermind";

    public bool Enabled => config.Enabled && IlEvmModule.Unsupported() is null;

    public IModule Module => new IlEvmModule(config);
}

/// <summary>Attaches the compiler's frame runner where <see cref="IIlEvmConfig.Scope"/> says.</summary>
public sealed class IlEvmModule(IIlEvmConfig config) : Module
{
    /// <summary>Why this process cannot run the compiler, or null when it can.</summary>
    /// <remarks>Fails closed: a core with another runner contract, or without the members the generated code binds, keeps the interpreter.</remarks>
    public static string? Unsupported()
    {
        if (!RuntimeFeature.IsDynamicCodeSupported)
            return "dynamic code is not supported";
        if (FrameCodeRunner<EthereumGasPolicy>.ContractVersion != 1)
            return $"frame code runner contract {FrameCodeRunner<EthereumGasPolicy>.ContractVersion}, built for 1";
        return IlEvm.MissingBindings() is { } missing ? $"core members not found: {missing}" : null;
    }

    protected override void Load(ContainerBuilder builder)
    {
        if (Unsupported() is not null)
            return;

        IlEvmScope scope = Configure(config);

        // Block processing and everything else get the block runner; the read-only query environments get their own,
        // so calls keep their rules - run-time compiles under Legacy, live profiling under Adaptive - whatever block
        // processing does. With Scope=All every kind of read-only query gets it, as before the block runner existed.
        // A measurement run gets the timed runners instead, so the production ones carry nothing of the timing.
        ReadOnlyQueryKind surfaces = scope == IlEvmScope.All ? ReadOnlyQueryKind.All : config.Surfaces;
        FrameCodeRunner<EthereumGasPolicy> blockRunner = config.Stats ? IlEvmTimedFrameRunner.Block : IlEvmFrameRunner.Block;
        FrameCodeRunner<EthereumGasPolicy> queryRunner = config.Stats ? IlEvmTimedFrameRunner.Instance : IlEvmFrameRunner.Instance;
        if (scope == IlEvmScope.All)
            builder.RegisterInstance(blockRunner).As<FrameCodeRunner<EthereumGasPolicy>>();
        if (surfaces != ReadOnlyQueryKind.None)
            builder.AddSingleton<IReadOnlyQueryModule>(new IlEvmQueryModule(surfaces, queryRunner));
        builder.AddStep(typeof(IlEvmStartupStep));
    }

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
        return scope;
    }
}

/// <summary>Attaches the compiler's frame runner to the machines of the read-only query environments.</summary>
/// <param name="runner">The runner to attach: <see cref="IlEvmFrameRunner.Instance"/> unless a measurement run times frames.</param>
public sealed class IlEvmQueryModule(ReadOnlyQueryKind surfaces, FrameCodeRunner<EthereumGasPolicy> runner) : Module, IReadOnlyQueryModule
{
    /// <summary>Attaches the production runner, <see cref="IlEvmFrameRunner.Instance"/>.</summary>
    public IlEvmQueryModule(ReadOnlyQueryKind surfaces) : this(surfaces, IlEvmFrameRunner.Instance)
    {
    }

    public ReadOnlyQueryKind AppliesTo => surfaces;

    /// <summary>The runner the module attaches.</summary>
    public FrameCodeRunner<EthereumGasPolicy> Runner { get; } = runner;

    protected override void Load(ContainerBuilder builder) =>
        builder.RegisterInstance(Runner).As<FrameCodeRunner<EthereumGasPolicy>>();
}
