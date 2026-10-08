// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
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
        MergeAndReturnBal(uint.MaxValue);

        if (VerifyOnly)
        {
            // IncrementalValidation only covered indices 0..txCount; the post-execution row
            // (txCount + 1) was just merged but not yet compared.
            ValidateBlockAccessList(block, (uint)(block.Transactions.Length + 1));
            ValidateStructuralEquivalence(block);
            return;
        }

        block.GeneratedBlockAccessList = GeneratedBlockAccessList;
        block.EncodedBlockAccessList = BlockAccessListDecoder.EncodeToBytes(GeneratedBlockAccessList);
        block.Header.BlockAccessListHash = new(ValueKeccak.Compute(block.EncodedBlockAccessList).Bytes);
    }
}
