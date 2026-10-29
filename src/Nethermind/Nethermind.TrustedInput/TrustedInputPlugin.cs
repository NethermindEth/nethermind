// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Autofac.Core;
using Nethermind.Api.Extensions;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.JsonRpc.Modules;

namespace Nethermind.TrustedInput;

/// <summary>Captures the trusted zkVM guest input of every block the node processes and serves it over JSON-RPC.</summary>
/// <remarks>The input is the format <c>Nethermind.Stateless.Execution.IO.TrustedInput</c> defines, which the guests built
/// with <c>GuestInput=Trusted</c> validate.</remarks>
public class TrustedInputPlugin(ITrustedInputConfig config) : INethermindPlugin
{
    public const string ModuleType = "TrustedInput";

    public string Name => "TrustedInput";
    public string Description => "Captures trusted zkVM guest inputs while processing blocks";
    public string Author => "Nethermind";
    public bool Enabled => config.Enabled;

    public IModule Module => new TrustedInputModule();
}

public class TrustedInputModule : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        builder
            .AddSingleton<TrustedInputStore>()
            .AddSingleton<IMainProcessingModule, TrustedInputMainProcessingModule>()
            .RegisterSingletonJsonRpcModule<ITrustedInputRpcModule, TrustedInputRpcModule>();
    }
}

/// <summary>Wraps the main pipeline's block processor in <see cref="TrustedInputCapturingBlockProcessor"/>.</summary>
public sealed class TrustedInputMainProcessingModule : Module, IMainProcessingModule
{
    protected override void Load(ContainerBuilder builder) =>
        builder.AddDecorator<IBlockProcessor, TrustedInputCapturingBlockProcessor>();
}
