// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp.Eip7928;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// Finalises the block by merging the logs of its indexes and stamping the generated BAL + its
/// encoded RLP + its hash onto the produced block.
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
        _mergedBlockAccessList.Merge(_txProcessorWithWorldStateManager.SealLogs());
        if (_validateBlockAccessList) ValidateMergedBlockAccessList(block);

        if (!VerifyOnly)
        {
            GeneratedBlockAccessList = _mergedBlockAccessList.ToGeneratedBlockAccessList();
            block.GeneratedBlockAccessList = GeneratedBlockAccessList;
        }

        if (_validateBlockAccessList)
        {
            if (VerifyOnly) return;

            // A match encodes to the suggested canonical wire bytes, as the decoder accepts nothing else, so their hash
            // stands in for the re-encode.
            if (block.BlockAccessList!.WireHash is { } wireHash)
            {
                block.Header.BlockAccessListHash = wireHash;
                return;
            }
        }

        block.EncodedBlockAccessList = BlockAccessListDecoder.EncodeToBytes(_mergedBlockAccessList);
        block.Header.BlockAccessListHash = new(ValueKeccak.Compute(block.EncodedBlockAccessList).Bytes);
    }
}
