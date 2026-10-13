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

        IlEvmSettings.Enabled = true;
        IlEvmSettings.Threshold = Math.Max(1, config.Threshold);
        IlEvmSettings.OpcodeThreshold = config.OpcodeThreshold > 0 ? config.OpcodeThreshold : int.MaxValue;
        IlEvmSettings.HotOpcodesPerSecond = Math.Max(0, config.HotOpcodesPerSecond);
        IlEvmSettings.CompileSynchronously = config.CompileSynchronously;
        IlEvmSettings.Profile = config.Profile;
        IlEvmSettings.BlockRuntimeCompile = config.BlockRuntimeCompile;
        IlEvmSettings.BlockSelfCheck = config.BlockSelfCheck;

        // Block processing and everything else get the block runner; the read-only query environments get their own,
        // which compiles at run time, so calls keep doing so whatever block processing does.
        // With Scope=All every kind of read-only query compiles at run time, as before the block runner existed.
        ReadOnlyQueryKind surfaces = config.Scope == IlEvmScope.All ? ReadOnlyQueryKind.All : config.Surfaces;
        if (config.Scope == IlEvmScope.All)
            builder.RegisterInstance(IlEvmFrameRunner.Block).As<FrameCodeRunner<EthereumGasPolicy>>();
        if (surfaces != ReadOnlyQueryKind.None)
            builder.AddSingleton<IReadOnlyQueryModule>(new IlEvmQueryModule(surfaces));
        builder.AddStep(typeof(IlEvmStartupStep));
    }
}

/// <summary>Attaches the compiler's frame runner to the machines of the read-only query environments.</summary>
public sealed class IlEvmQueryModule(ReadOnlyQueryKind surfaces) : Module, IReadOnlyQueryModule
{
    public ReadOnlyQueryKind AppliesTo => surfaces;

    protected override void Load(ContainerBuilder builder) =>
        builder.RegisterInstance(IlEvmFrameRunner.Instance).As<FrameCodeRunner<EthereumGasPolicy>>();
}
