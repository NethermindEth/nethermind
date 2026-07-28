// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Receipts;
using Nethermind.Core;
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
/// Registered as an override of <see cref="IReceiptFinder"/> rather than as a decorator: an Autofac decorator also
/// applies to keyed registrations of the same service, which would pull peer-facing serving — resolving
/// <see cref="FullInfoReceiptFinder.StoredOnlyKey"/> — into regeneration and let any peer drive a block execution.
/// </para>
/// </remarks>
public class ReceiptRegenerationModule : Module
{
    protected override void Load(ContainerBuilder builder) => builder
        .AddSingleton<RegeneratingReceiptsEnvSourceFactory>()
        .AddSingleton<IShareableOverridableEnvSource<ReceiptsRegenerationEnv>, RegeneratingReceiptsEnvSourceFactory>(
            factory => factory.Create(Environment.ProcessorCount))
        .AddSingleton<ReceiptsRegenerator>()
        .AddSingleton<IReceiptFinder>(ctx => new RegeneratingReceiptFinder(
            ctx.Resolve<FullInfoReceiptFinder>(),
            ctx.Resolve<IBlockFinder>(),
            ctx.Resolve<ReceiptsRegenerator>()));
}
