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
/// Per-capture recorder of header reads. The <see cref="WitnessCapturingHeaderFinder"/> reports every
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
    private ulong _lowestRequestedHeader = ulong.MaxValue;

    /// <summary>Resets the low-water mark so the recorder can be reused across pooled env rents.</summary>
    public void Reset() => _lowestRequestedHeader = ulong.MaxValue;

    public void OnHeaderRead(BlockHeader header)
    {
        if (header.Number < _lowestRequestedHeader) _lowestRequestedHeader = header.Number;
    }

    public IOwnedReadOnlyList<byte[]> BuildHeaders(Hash256 parentHash, IHeaderFinder finder)
    {
        Hash256 currentHash = parentHash;
        BlockHeader parentHeader = finder.Get(currentHash) ?? throw new ArgumentException($"Parent {currentHash} is not found");

        // BLOCKHASH can only reach below the executed block, so a recorded header above the parent
        // means the bookkeeping is broken — fail loudly instead of computing a non-positive count.
        if (_lowestRequestedHeader < ulong.MaxValue && _lowestRequestedHeader > parentHeader.Number)
        {
            throw new InvalidOperationException(
                $"Recorded header {_lowestRequestedHeader} is above the executed-against parent {parentHeader.Number}");
        }

        // Headers in ascending block-number order — any BLOCKHASH-touched ancestor first, recorded block last.
        int count = _lowestRequestedHeader < ulong.MaxValue
            ? (int)(parentHeader.Number - _lowestRequestedHeader + 1)
            : 1;
        int index = count - 1;
        ArrayPoolList<byte[]> headers = new(capacity: count, count);
        try
        {
            headers[index--] = _decoder.EncodeAsBytes(parentHeader);

            for (ulong i = parentHeader.Number - 1; index >= 0; i--)
            {
                currentHash = parentHeader.ParentHash!;
                parentHeader = finder.Get(currentHash, i)
                    ?? throw new ArgumentException($"Unable to get requested header at hash {currentHash} and number {i} during witness generation");
                headers[index--] = _decoder.EncodeAsBytes(parentHeader);
                if (i == 0) break;
            }

            return headers;
        }
        catch
        {
            // A missing ancestor (reorg/prune mid-walk) leaves the partially-filled list holding pooled
            // buffers — return them before propagating.
            headers.Dispose();
            throw;
        }
    }
}
