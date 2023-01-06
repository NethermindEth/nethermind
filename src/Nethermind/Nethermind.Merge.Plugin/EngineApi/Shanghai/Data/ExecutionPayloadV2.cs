// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Merge.Plugin.EngineApi.Paris.Data;
using Nethermind.State.Proofs;

namespace Nethermind.Merge.Plugin.EngineApi.Shanghai.Data;

/// <summary>
/// Represents an object mapping the <c>ExecutionPayload</c> structure of the beacon chain spec.
/// <see href="https://github.com/ethereum/execution-apis/blob/main/src/engine/paris.md#executionpayloadv1"/>
/// </summary>
public class ExecutionPayloadV2 : ExecutionPayloadV1
{
    public ExecutionPayloadV2() { } // Needed for tests

    public ExecutionPayloadV2(Block block) : base(block)
    {
    }

    protected override void SetBlock(Block block)
    {
        base.SetBlock(block);
        Withdrawals = block.Withdrawals?.Cast<WithdrawalV1>();
    }

    /// <summary>
    /// Gets or sets a collection of <see cref="WithdrawalV1"/> as defined in
    /// <see href="https://eips.ethereum.org/EIPS/eip-4895">EIP-4895</see>.
    /// </summary>
    public IEnumerable<WithdrawalV1>? Withdrawals { get; set; }

    public override bool TryGetBlock(out Block? block, UInt256? totalDifficulty = null)
    {
        try
        {
            var transactions = GetTransactions();
            var header = new BlockHeader(
                ParentHash,
                Keccak.OfAnEmptySequenceRlp,
                FeeRecipient,
                UInt256.Zero,
                BlockNumber,
                GasLimit,
                Timestamp,
                ExtraData)
            {
                Hash = BlockHash,
                ReceiptsRoot = ReceiptsRoot,
                StateRoot = StateRoot,
                Bloom = LogsBloom,
                GasUsed = GasUsed,
                BaseFeePerGas = BaseFeePerGas,
                Nonce = 0,
                MixHash = PrevRandao,
                Author = FeeRecipient,
                IsPostMerge = true,
                TotalDifficulty = totalDifficulty,
                TxRoot = new TxTrie(transactions).RootHash,
                WithdrawalsRoot = Withdrawals is null ? null : new WithdrawalTrie(Withdrawals).RootHash,
            };

            block = new(header, transactions, Array.Empty<BlockHeader>(), Withdrawals);

            return true;
        }
        catch (Exception)
        {
            block = null;

            return false;
        }
    }
}
