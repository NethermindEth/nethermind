// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Eez.Execution.Settlement.RollupX;
using Nethermind.Int256;
using Nethermind.Facade.Eth;

namespace Nethermind.Eez.Rpc;

public sealed class EezNativeHistoryTransactionForRpc : EezSystemTransactionForRpc, IFromTransaction<EezNativeHistoryTransactionForRpc>
{
    public new static TxType TxType => (TxType)NativeHistoryEnvelope.Type;
    public override TxType? Type => TxType;
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public UInt256 YParity => UInt256.Zero;

    public ulong Version => NativeHistoryEnvelope.Version;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ValueHash256[]? Hashes { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HistoryBlock[]? Blocks { get; set; }

    public readonly record struct HistoryBlock(ulong Number, ValueHash256 BlockHash);

    [JsonConstructor]
    public EezNativeHistoryTransactionForRpc() { }

    public EezNativeHistoryTransactionForRpc(Transaction transaction, in TransactionForRpcContext context) : base(transaction, in context)
    {
        NativeHistoryEnvelope envelope = ((NativeHistoryTransaction)transaction).Envelope;
        if (envelope.HasExplicitBlocks)
        {
            ReadOnlySpan<NativeHistoryBlock> blocks = envelope.Blocks;
            Blocks = new HistoryBlock[blocks.Length];
            for (int index = 0; index < blocks.Length; index++)
                Blocks[index] = new HistoryBlock(blocks[index].Number, blocks[index].Hash);
        }
        else Hashes = envelope.Hashes.ToArray();
    }

    public new static EezNativeHistoryTransactionForRpc FromTransaction(Transaction transaction, in TransactionForRpcContext context) =>
        new(transaction, in context);
}
