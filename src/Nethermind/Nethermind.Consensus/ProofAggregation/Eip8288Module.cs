// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Consensus.ProofAggregation;

/// <summary>Registers EIP-8288 proof aggregation and its block production checks.</summary>
public sealed class Eip8288Module : Module
{
    protected override void Load(ContainerBuilder builder) => builder
        .AddSingleton<ILeanProofVerifier, NativeLeanProofVerifier>()
        .AddDecorator<ILeanProofVerifier, ProductionProofCache>()
        .AddSingleton<LeanProofStore>(static _ => new LeanProofStore(FrameTxSigHash.ComputeValue))
        .AddSingleton<ProofWrapperService>()
        .AddSingleton<InclusionListProofService>()
        .AddDecorator<BlockProcessor.IBlockProductionTransactionPicker, Eip8288TransactionPicker>();
}
