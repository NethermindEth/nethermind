// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Rlp;
using Org.BouncyCastle.Utilities;
using Bytes = Nethermind.Core.Extensions.Bytes;

namespace Nethermind.Optimism;

public class OptimismPayloadAttributes : PayloadAttributes
{
    private byte[][]? _encodedTransactions;

    public byte[][]? Transactions
    {
        get { return _encodedTransactions; }
        set
        {
            _encodedTransactions = value;
            _transactions = null;
        }
    }
    public bool NoTxPool { get; set; }
    public long GasLimit { get; set; }
    public override long? GetGasLimit() => GasLimit;
    private int TransactionsLength => Transactions?.Length ?? 0;

    private Transaction[]? _transactions;

    /// <summary>
    /// Decodes and returns an array of <see cref="Transaction"/> from <see cref="Transactions"/>.
    /// </summary>
    /// <returns>An RLP-decoded array of <see cref="Transaction"/>.</returns>
    public Transaction[]? GetTransactions() => _transactions ??= Transactions?
        .Select((t, i) =>
        {
            try
            {
                return Rlp.Decode<Transaction>(t, RlpBehaviors.SkipTypedWrapping);
            }
            catch (RlpException e)
            {
                throw new RlpException($"Transaction {i} is not valid", e);
            }
        }).ToArray();

    /// <summary>
    /// RLP-encodes and sets the transactions specified to <see cref="Transactions"/>.
    /// </summary>
    /// <param name="transactions">An array of transactions to encode.</param>
    public void SetTransactions(params Transaction[] transactions)
    {
        Transactions = transactions
            .Select(t => Rlp.Encode(t, RlpBehaviors.SkipTypedWrapping).Bytes)
            .ToArray();
        _transactions = transactions;
    }

    [SkipLocalsInit]
    protected override string ComputePayloadId(BlockHeader parentHeader)
    {
        Span<byte> inputSpan = stackalloc byte[
            Keccak.Size + // parent hash
            sizeof(long) + // timestamp
            Keccak.Size + // prev randao
            Address.Size + // suggested fee recipient
            Keccak.Size + // withdrawals root hash
            sizeof(bool) + // no tx pool
            Keccak.Size * TransactionsLength + // tx hashes
            sizeof(long)]; // gas limit

        WritePayloadIdMembers(parentHeader, inputSpan);

        return ComputePayloadId(inputSpan);
    }

    protected override int WritePayloadIdMembers(BlockHeader parentHeader, Span<byte> inputSpan)
    {
        int offset = base.WritePayloadIdMembers(parentHeader, inputSpan);
        inputSpan[offset] = NoTxPool ? (byte)1 : (byte)0;
        offset += sizeof(bool);
        Transaction[]? transactions = GetTransactions();
        if (transactions is not null)
        {
            foreach (Transaction tx in transactions)
            {
                tx.Hash!.Bytes.CopyTo(inputSpan.Slice(offset, Keccak.Size));
                offset += Keccak.Size;
            }
        }
        BinaryPrimitives.WriteInt64BigEndian(inputSpan.Slice(offset, sizeof(long)), GasLimit);
        return offset + sizeof(long);
    }

    public override string ToString()
    {
        StringBuilder sb = new StringBuilder($"{nameof(PayloadAttributes)} {{")
            .Append($"{nameof(Timestamp)}: {Timestamp}, ")
            .Append($"{nameof(PrevRandao)}: {PrevRandao}, ")
            .Append($"{nameof(SuggestedFeeRecipient)}: {SuggestedFeeRecipient}, ")
            .Append($"{nameof(GasLimit)}: {GasLimit}, ")
            .Append($"{nameof(NoTxPool)}: {NoTxPool}, ")
            .Append($"{nameof(Transactions)}: {Transactions?.Length ?? 0}");

        if (Withdrawals is not null)
        {
            sb.Append($", {nameof(Withdrawals)} count: {Withdrawals.Count}");
        }

        sb.Append('}');
        // sb.AppendLine();
        //
        // sb.AppendLine("--------");
        // Transaction[] txs = GetTransactions()?.ToArray() ?? Array.Empty<Transaction>();
        // for (int i =0; i<txs.Length; i++)
        // {
        //     Transaction tx = txs[i];
        //     sb.AppendLine(Transactions?[i].ToHexString());
        //     sb.AppendLine("[");
        //     sb.AppendLine($"  {tx.SourceHash}");
        //     sb.AppendLine($"  {tx.SenderAddress}");
        //     sb.AppendLine($"  {tx.To}");
        //     sb.AppendLine($"  {tx.Mint}");
        //     sb.AppendLine($"  {tx.Value}");
        //     sb.AppendLine($"  {tx.GasLimit}");
        //     sb.AppendLine($"  {tx.IsOPSystemTransaction}");
        //     sb.AppendLine($"  {tx.DataLength.ToHexString()}");
        //     sb.AppendLine("]");
        // }
        // sb.AppendLine("--------");

        return sb.ToString();
    }
}
