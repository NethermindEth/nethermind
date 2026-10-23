// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Eez.Execution.Settlement.RollupX;
using Nethermind.Eez.Execution.Stateless;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Eez.Attester;

internal sealed class PreparedOrdinaryBlock
{
    private readonly byte[] _block;
    private readonly byte[] _parent;
    private readonly byte[][] _receipts;
    private readonly Address[] _senders;

    private PreparedOrdinaryBlock(EezStatelessBlockResult result)
    {
        Number = result.Block.Number;
        Hash = result.Hash;
        ParentHash = result.Block.ParentHash!;
        PreStateRoot = result.PreStateRoot;
        PostStateRoot = result.PostStateRoot;
        _block = Rlp.Encode(result.Block).Bytes;
        _parent = Rlp.Encode(result.Parent).Bytes;
        _receipts = new byte[result.Receipts.Length][];
        _senders = new Address[result.Block.Transactions.Length];
        RetainedBytes = 1024L + _block.Length + _parent.Length + 96L * _senders.Length;
        for (int i = 0; i < _receipts.Length; i++)
        {
            _receipts[i] = Rlp.Encode(result.Receipts[i], RlpBehaviors.Eip658Receipts).Bytes;
            RetainedBytes += 32L + _receipts[i].Length;
        }
        for (int i = 0; i < _senders.Length; i++)
            _senders[i] = result.Block.Transactions[i].SenderAddress
                ?? throw new InvalidOperationException("Validated ordinary transaction has no sender.");
    }

    internal ulong Number { get; }
    internal Hash256 Hash { get; }
    internal Hash256 ParentHash { get; }
    internal Hash256 PreStateRoot { get; }
    internal Hash256 PostStateRoot { get; }
    internal long RetainedBytes { get; }

    internal static PreparedOrdinaryBlock Create(EezStatelessBlockResult result)
    {
        RollupXOperationsCheck.VerifyIntermediate(result);
        return new PreparedOrdinaryBlock(result);
    }

    internal EezStatelessBlockResult Materialize()
    {
        Block block = Rlp.Decode<Block>(_block)!;
        BlockHeader parent = Rlp.Decode<BlockHeader>(_parent)!;
        TxReceipt[] receipts = new TxReceipt[_receipts.Length];
        for (int i = 0; i < receipts.Length; i++) receipts[i] = Rlp.Decode<TxReceipt>(_receipts[i], RlpBehaviors.Eip658Receipts)!;
        for (int i = 0; i < _senders.Length; i++) block.Transactions[i].SenderAddress = _senders[i];
        return new(block, parent, receipts, []);
    }
}
