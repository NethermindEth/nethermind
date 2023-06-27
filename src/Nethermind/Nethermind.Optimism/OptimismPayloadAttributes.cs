// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Drawing;
using System.Linq;
using System.Runtime.CompilerServices;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

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
}
