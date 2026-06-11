// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Consensus;

public interface IBlockProducerFactory
{
    /// <summary>
    /// Creates a block producer.
    /// </summary>
    /// <returns>A block producer, or <c>null</c> when the consensus engine does not provide one.</returns>
    /// <remarks>
    /// Can be called many times, with different parameters, each time should create a new instance. Example usage in MEV plugin.
    /// </remarks>
    IBlockProducer? InitBlockProducer();
}
