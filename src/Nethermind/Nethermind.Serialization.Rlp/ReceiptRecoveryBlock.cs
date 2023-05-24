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
public struct ReceiptRecoveryBlock
{
    private readonly MemoryManager<byte>? _memoryOwner; // Can be null if loaded without span
    private readonly Memory<byte> _transactionData;
    private int _currentTransactionPosition = 0;

    private readonly Transaction[]? _transactions = null;
    private int _currentTransactionIndex = 0;

    public BlockHeader Header { get; }
    public int TransactionCount { get; }

    // Use a buffer to avoid reallocation. Surprisingly significant. May produce incorrect transaction, but for recovery, it is correct.
    private Transaction? _txBuffer;

    public ReceiptRecoveryBlock(Block block)
    {
        Header = block.Header;
        _transactions = block.Transactions;
        TransactionCount = _transactions.Length;
    }

    public ReceiptRecoveryBlock(MemoryManager<byte>? memoryOwner, BlockHeader header, Memory<byte> transactionData, int transactionCount)
    {
        Header = header;
        _memoryOwner = memoryOwner;
        _transactionData = transactionData;
        TransactionCount = transactionCount;
    }

    public Transaction GetNextTransaction()
    {
        if (_transactions != null)
        {
            return _transactions[_currentTransactionIndex++];
        }

        Rlp.ValueDecoderContext decoderContext = new(_transactionData, true);
        decoderContext.Position = _currentTransactionPosition;
        TxDecoder.InstanceWithoutLazyHash.Decode(ref decoderContext, ref _txBuffer, RlpBehaviors.AllowUnsigned);
        _currentTransactionPosition = decoderContext.Position;

        return _txBuffer;
    }

    public Keccak? Hash => Header.Hash; // do not add setter here
    public long Number => Header.Number; // do not add setter here

    public void Dispose()
    {
        ((IMemoryOwner<byte>?)_memoryOwner)?.Dispose();
    }
}
