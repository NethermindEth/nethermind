// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;

namespace Nethermind.Consensus.Processing;

public interface IReadOnlyTxProcessingEnvFactory
{
    public IReadOnlyTxProcessorSource Create();

    /// <param name="forReadOnlyQueries">When true the source only serves read-only queries whose results never feed
    /// block processing, so the world state may tune its reads for them. Implementations that do not distinguish
    /// fall back to <see cref="Create()"/>.</param>
    public IReadOnlyTxProcessorSource Create(bool forReadOnlyQueries) => Create();
}
