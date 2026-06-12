// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain.Headers;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Consensus.Stateless;

/// <summary>
/// Per-capture recorder of header reads. Lives on the <see cref="WitnessCaptureSession"/> while a
/// capture is armed; the IHeaderFinder decorator ([[witness-capturing-header-finder]]) reports every
/// header lookup here so <see cref="BuildHeaders"/> can emit the contiguous header chain that the
/// stateless verifier needs.
/// </summary>
/// <remarks>
/// The chain runs from <c>_lowestRequestedHeader</c> (a low-water mark of every header touched
/// during execution — e.g. by BLOCKHASH reaching back into the past) to the parent of the recorded
/// block, in ascending block-number order. The caller passes the undecorated
/// <see cref="IHeaderFinder"/> into <see cref="BuildHeaders"/> so walking the chain at build time
/// does not re-enter the capture path.
/// </remarks>
public sealed class WitnessHeaderRecorder
{
    private static readonly HeaderDecoder _decoder = new();
    private long _lowestRequestedHeader = long.MaxValue;

    public void OnHeaderRead(BlockHeader header)
    {
        if (header.Number < _lowestRequestedHeader) _lowestRequestedHeader = header.Number;
    }

    public IOwnedReadOnlyList<byte[]> BuildHeaders(Hash256 parentHash, IHeaderFinder finder)
    {
        Hash256 currentHash = parentHash;
        BlockHeader parentHeader = finder.Get(currentHash) ?? throw new ArgumentException($"Parent {currentHash} is not found");

        int count = _lowestRequestedHeader < long.MaxValue
            ? (int)(parentHeader.Number - _lowestRequestedHeader + 1)
            : 1;
        int index = count - 1;
        ArrayPoolList<byte[]> headers = new(count, count)
        {
            [index--] = _decoder.Encode(parentHeader).Bytes
        };

        if (index >= 0)
        {
            for (long i = parentHeader.Number - 1; i >= _lowestRequestedHeader; i--)
            {
                currentHash = parentHeader.ParentHash!;
                parentHeader = finder.Get(currentHash, i) ?? throw new ArgumentException($"Unable to get requested header at hash {currentHash} and number {i} during witness generation");
                headers[index--] = _decoder.Encode(parentHeader).Bytes;
            }
        }

        return headers;
    }
}
