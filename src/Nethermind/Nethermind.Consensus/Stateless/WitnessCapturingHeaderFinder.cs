// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Headers;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.Stateless;

/// <summary>
/// Transparent <see cref="IHeaderFinder"/> decorator that side-channels every successful header lookup
/// into a <see cref="WitnessHeaderRecorder"/>, so BLOCKHASH lookups during EVM execution extend the
/// witness header chain back to whatever the EVM touched.
/// </summary>
/// <remarks>
/// Installed only inside the dedicated witness processing graph, so it records unconditionally — there
/// is no armed/disarmed state to consult.
/// </remarks>
public sealed class WitnessCapturingHeaderFinder(IHeaderFinder inner, WitnessHeaderRecorder recorder) : IHeaderFinder
{
    public BlockHeader? Get(Hash256 blockHash, ulong? blockNumber = null)
    {
        BlockHeader? header = inner.Get(blockHash, blockNumber);
        if (header is not null) recorder.OnHeaderRead(header);
        return header;
    }
}
