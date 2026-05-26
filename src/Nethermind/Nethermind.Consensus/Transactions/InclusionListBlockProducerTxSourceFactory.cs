// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Producers;

namespace Nethermind.Consensus.Transactions;

/// <summary>
/// Prepends FOCIL (EIP-7805) inclusion-list transactions to the block-producer tx source.
/// The CL injects IL bytes via <see cref="InclusionListTxSource.Set"/> through
/// PayloadAttributesV5; they are drained ahead of the mempool on the next block-production
/// cycle so a full block from the pool cannot displace the IL (which would otherwise
/// trivially satisfy the IL by gas-exhaustion — review feedback on the original PR).
/// </summary>
public class InclusionListBlockProducerTxSourceFactory(
    IBlockProducerTxSourceFactory baseFactory,
    InclusionListTxSource inclusionListTxSource) : IBlockProducerTxSourceFactory
{
    public ITxSource Create() => inclusionListTxSource.Then(baseFactory.Create());
}
