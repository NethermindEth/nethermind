// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Producers;

namespace Nethermind.Consensus.Transactions;

/// <summary>
/// Prepends FOCIL (EIP-7805) IL transactions to the block-producer tx source so they drain
/// before the mempool — a pool-first order could trivially satisfy the IL by gas exhaustion.
/// </summary>
public class InclusionListBlockProducerTxSourceFactory(
    IBlockProducerTxSourceFactory baseFactory,
    InclusionListTxSource inclusionListTxSource) : IBlockProducerTxSourceFactory
{
    public ITxSource Create() => inclusionListTxSource.Then(baseFactory.Create());
}
