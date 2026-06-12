// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Headers;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.Stateless;

/// <summary>
/// Transparent <see cref="IHeaderFinder"/> decorator that, when a capture is armed on the
/// <see cref="WitnessCaptureSession"/>, side-channels every successful header lookup into the
/// session's <see cref="WitnessHeaderRecorder"/> recorder. Catches BLOCKHASH lookups
/// during EVM execution so the witness headers chain extends back to whatever the EVM touched.
/// </summary>
public sealed class WitnessCapturingHeaderFinder(IHeaderFinder inner, WitnessCaptureSession session) : IHeaderFinder
{
    /// <summary>
    /// The undecorated inner header finder. Exposed so witness-build code can walk ancestor headers
    /// without re-entering the capture path — see <see cref="WitnessHeaderRecorder.BuildHeaders"/>.
    /// </summary>
    internal IHeaderFinder Inner => inner;

    public BlockHeader? Get(Hash256 blockHash, long? blockNumber = null)
    {
        BlockHeader? header = inner.Get(blockHash, blockNumber);
        if (header is not null && session.HeaderRecorder is { } recorder) recorder.OnHeaderRead(header);
        return header;
    }
}
