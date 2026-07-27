// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Receipts;
using Nethermind.Core;
using Nethermind.State.OverridableEnv;

namespace Nethermind.Init.Modules;

/// <summary>
/// Wires receipt regeneration onto the read path: an archive node that dropped stored receipt bodies re-executes a
/// block on demand to answer receipt queries. Added only when <see cref="IReceiptConfig.RecoverReceiptsFromState"/> is set.
/// </summary>
/// <remarks>
/// Kept out of <c>BlockTreeModule</c> on purpose — receipt storage is a chain-data concern, whereas regeneration pulls
/// in the block-execution stack. Composing it here keeps that dependency off the storage module and confined to nodes
/// that opt in.
/// </remarks>
public class ReceiptRegenerationModule : Module
{
    protected override void Load(ContainerBuilder builder) => builder
        .AddSingleton<RegeneratingReceiptsEnvSourceFactory>()
        .AddSingleton<IShareableOverridableEnvSource<ReceiptsRegenerationEnv>, RegeneratingReceiptsEnvSourceFactory>(
            factory => factory.Create(Environment.ProcessorCount))
        .AddSingleton<ReceiptsRegenerator>()
        .AddDecorator<IReceiptFinder, RegeneratingReceiptFinder>();
}
