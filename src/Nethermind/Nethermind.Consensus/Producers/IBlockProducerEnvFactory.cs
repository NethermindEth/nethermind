// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Consensus.Producers
{
    public enum BlockProducerEnvLifetime
    {
        /// <summary>
        /// Scope is registered with the root lifetime for disposal on shutdown.
        /// Suitable for long-lived block producers.
        /// </summary>
        Persistent,

        /// <summary>
        /// Caller owns the scope and must dispose the returned env (which implements <see cref="System.IAsyncDisposable"/>).
        /// Suitable for per-request use.
        /// </summary>
        Transient
    }

    public interface IBlockProducerEnvFactory
    {
        IBlockProducerEnv Create(BlockProducerEnvLifetime lifetime = BlockProducerEnvLifetime.Persistent);
    }
}
