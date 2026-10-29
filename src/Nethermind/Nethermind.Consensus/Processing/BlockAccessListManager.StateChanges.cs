// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp.Eip7928;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// Finalises the block by stamping the generated BAL + its encoded RLP + its hash onto the
/// produced block.
/// </summary>
public partial class BlockAccessListManager
{
    public void SetBlockAccessList(Block block)
    {
        if (!_blockAccessListsEnabled)
        {
            return;
        }

        if (block.IsGenesis)
        {
            block.Header.BlockAccessListHash = Keccak.OfAnEmptySequenceRlp;
            return;
        }

        CheckInitialized();
        ReadOnlySpan<BlockAccessListLog> logs = _txProcessorWithWorldStateManager.SealLogs();
        if (VerifyOnly)
        {
            ValidateLogs(block, logs);
            return;
        }

        GeneratedBlockAccessList generated = GeneratedBlockAccessList.Merge(logs);
        // Set before validating, so a block rejected for its access list keeps the generated one.
        GeneratedBlockAccessList = generated;
        block.GeneratedBlockAccessList = generated;

        if (_validateBlockAccessList)
        {
            ValidateMergedBlockAccessList(block, generated);

            // A match encodes to the suggested canonical wire bytes, as the decoder accepts nothing else, so their hash
            // stands in for the re-encode.
            if (block.BlockAccessList!.WireHash is { } wireHash)
            {
                block.Header.BlockAccessListHash = wireHash;
                return;
            }
        }

        block.EncodedBlockAccessList = BlockAccessListDecoder.EncodeToBytes(generated);
        block.Header.BlockAccessListHash = new(ValueKeccak.Compute(block.EncodedBlockAccessList).Bytes);
    }
}
