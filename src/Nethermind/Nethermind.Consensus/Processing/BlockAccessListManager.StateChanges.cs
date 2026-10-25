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

        uint postExecutionIndex = (uint)(block.Transactions.Length + 1);
        if (VerifyOnly)
        {
            // IncrementalValidation only covered indices 0..txCount; the post-execution row
            // (txCount + 1) was just merged but not yet compared.
            ValidateBlockAccessList(block, postExecutionIndex);
            ValidateStructuralEquivalence(block);
            return;
        }

        block.GeneratedBlockAccessList = GeneratedBlockAccessList;

        if (_validatedRowCount == postExecutionIndex
            && _generatedValidationIndex is not null
            && block.BlockAccessList?.WireHash is { } wireHash)
        {
            // Rows 0..txCount already matched the suggested BAL. Once the post-execution row and the structure match
            // too, the generated BAL encodes to the suggested canonical wire bytes, so their hash stands in for the re-encode.
            // This holds only because the decoder accepts nothing but the canonical encoding.
            ValidateBlockAccessList(block, postExecutionIndex);
            ValidateGeneratedStructuralEquivalence(block);
            block.Header.BlockAccessListHash = wireHash;
            return;
        }

        block.EncodedBlockAccessList = BlockAccessListDecoder.EncodeToBytes(GeneratedBlockAccessList);
        block.Header.BlockAccessListHash = new(ValueKeccak.Compute(block.EncodedBlockAccessList).Bytes);
    }
}
