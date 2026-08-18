// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Receipts;
using Nethermind.Core;
using Nethermind.JsonRpc;
using Nethermind.State.OverridableEnv;

namespace Nethermind.Init.Modules;

/// <summary>
/// Wires receipt regeneration onto the read path: an archive node that dropped stored receipt bodies re-executes a
/// block on demand to answer receipt queries. Added only when <see cref="IReceiptConfig.DeriveFromState"/> is set.
/// </summary>
/// <remarks>
/// Kept out of <c>BlockTreeModule</c> on purpose — receipt storage is a chain-data concern, whereas regeneration pulls
/// in the block-execution stack. Composing it here keeps that dependency off the storage module and confined to nodes
/// that opt in.
/// <para>
/// Overrides only <see cref="IReceiptFinder.RegenerableKey"/>, never the unkeyed registration. Regeneration costs a
/// block execution, so it must stay unreachable from anything that is not a read-only query: peer-facing serving,
/// which any peer can drive, and consensus components that read receipts on the processing path (the AuRa validator
/// contract, Shutter) — the latter also tolerate absent receipts, which this decorator deliberately does not.
/// A decorator would not do: an Autofac decorator applies to keyed registrations of the same service too.
/// The wrapped inner is the unkeyed finder, so a plugin's override of <see cref="IReceiptFinder"/> keeps applying.
/// </para>
/// </remarks>
public class ReceiptRegenerationModule : Module
{
    protected override void Load(ContainerBuilder builder) => builder
        .AddSingleton<RegeneratingReceiptsEnvSourceFactory>()
        // Each retained environment holds a world state and a full processing chain, and a regenerating query is as
        // expensive as executing a block — so it is bounded by the same knob that bounds the eth module itself.
        .AddSingleton<IShareableOverridableEnvSource<ReceiptsRegenerationEnv>>(ctx =>
            ctx.Resolve<RegeneratingReceiptsEnvSourceFactory>()
               .Create(ctx.Resolve<IJsonRpcConfig>().EthModuleConcurrentInstances ?? 2 * Environment.ProcessorCount))
        .AddSingleton<ReceiptsRegenerator>()
        .AddKeyedSingleton<IReceiptFinder>(IReceiptFinder.RegenerableKey, ctx => new RegeneratingReceiptFinder(
            ctx.Resolve<IReceiptFinder>(),
            ctx.Resolve<IReceiptStorage>(),
            ctx.Resolve<IBlockFinder>(),
            ctx.Resolve<ReceiptsRegenerator>()));
}
