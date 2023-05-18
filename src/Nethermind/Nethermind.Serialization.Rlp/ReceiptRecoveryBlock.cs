// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Serialization.Rlp;

/// <summary>
/// A Block specifically for receipt recovery. Does not contain any of the fields that are not needed for that.
/// Retain span from DB as memory and must be explicitly disposed.
/// </summary>
[DebuggerDisplay("{Hash} ({Number})")]
public class ReceiptRecoveryBlock
{
    public ReceiptRecoveryBlock(Block block)
    {
        Header = block.Header;
        _transactions = block.Transactions;
        TransactionCount = _transactions.Length;
    }

    public ReceiptRecoveryBlock(IMemoryOwner<byte> memoryOwner, BlockHeader header, Memory<byte> transactionData, int transactionCount)
    {
        Header = header;
        _memoryOwner = memoryOwner;
        _transactionData = transactionData;
        TransactionCount = transactionCount;
    }

    private IMemoryOwner<byte>? _memoryOwner;
    private Memory<byte> _transactionData { get; set; }
    private int _currentTransactionPosition = 0;

    private Transaction[]? _transactions = null;
    private int _currentTransactionIndex = 0;

    public BlockHeader Header { get; }
    public int TransactionCount { get; }

    public Transaction GetNextTransaction()
    {
        if (_transactions != null)
        {
            return _transactions[_currentTransactionIndex++];
        }

        Rlp.ValueDecoderContext decoderContext = new(_transactionData.Span);
        decoderContext.Position = _currentTransactionPosition;
        Transaction tx = TxDecoder.Instance.Decode(ref decoderContext, RlpBehaviors.AllowUnsigned);
        _currentTransactionPosition = decoderContext.Position;

        return tx;
    }

    public Keccak? Hash => Header.Hash; // do not add setter here
    public long Number => Header.Number; // do not add setter here

    public void Dispose()
    {
        _memoryOwner?.Dispose();
    }
}
