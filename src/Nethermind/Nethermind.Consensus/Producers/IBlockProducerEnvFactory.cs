// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Consensus.Producers
{
    public interface IBlockProducerEnvFactory
    {
        /// <summary>
        /// Creates a block producer environment registered with the root lifetime for disposal on shutdown.
        /// </summary>
        IBlockProducerEnv CreatePersistent();

        /// <summary>
        /// Creates a block producer environment owned by the caller. The caller must dispose it after use.
        /// </summary>
        ScopedBlockProducerEnv CreateTransient();
    }
}
