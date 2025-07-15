// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Transactions;

namespace Nethermind.Consensus;

public interface IBlockProducerFactory
{
    /// <summary>
    /// Creates a block producer.
    /// </summary>
    /// <param name="additionalTxSource">Optional parameter. If present this transaction source should be used before any other transaction sources, except consensus ones. Plugin still should use their own transaction sources.</param>
    /// <remarks>
    /// Can be called many times, with different parameters, each time should create a new instance. Example usage in MEV plugin.
    /// </remarks>
    IBlockProducer InitBlockProducer();
}
