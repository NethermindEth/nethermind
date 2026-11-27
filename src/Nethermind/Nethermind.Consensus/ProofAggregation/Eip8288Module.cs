// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Logging;

namespace Nethermind.Consensus.ProofAggregation;

/// <summary>Registers EIP-8288 proof verification and builder aggregation.</summary>
/// <remarks>The native Lean backend verifies EIP-8141 leanSPHINCS claims only on chains with EIP-8288 scheduled, where
/// startup checks it is available; elsewhere such claims stay unverifiable, as without this module.</remarks>
public sealed class Eip8288Module : Module
{
    protected override void Load(ContainerBuilder builder) => builder
        .AddSingleton(NativeLeanProofVerifier.Instance)
        .AddSingleton<DependencyProofCache, NativeLeanProofVerifier, ILogManager>(static (native, logManager) => new DependencyProofCache(native, logManager))
        .Bind<ILeanProofVerifier, DependencyProofCache>()
        .AddSingleton<ILeanSphincsVerifier, ISpecProvider, NativeLeanProofVerifier>(static (specProvider, native) =>
            specProvider.GetFinalSpec().IsEip8288Enabled ? native : UnavailableLeanSphincsVerifier.Instance)
        .AddDecorator<IBlockProcessor, AggregatingBlockProcessor>();
}
