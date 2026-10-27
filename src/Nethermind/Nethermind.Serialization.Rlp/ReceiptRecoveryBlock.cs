// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
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
        if (_transactions is not null)
        {
            return _transactions[_currentTransactionIndex++];
        }

        RlpReader decoderContext = new(_transactionData)
        {
            Position = _currentTransactionPosition
        };
        TxDecoder.Instance.Decode(ref decoderContext, ref _txBuffer, RlpBehaviors.AllowUnsigned);
        Transaction tx = _txBuffer ?? throw new RlpException("Transaction decoding returned null.");
        Hash256? _ = tx.Hash; // Force Hash evaluation
        _currentTransactionPosition = decoderContext.Position;

        return tx;
    }

    /// <summary>Gets the transaction at <paramref name="index"/> as the block decoder decodes it, holding no reference to the block's memory.</summary>
    /// <remarks>Does not move the position the other readers advance.</remarks>
    public readonly Transaction GetTransaction(int index)
    {
        if ((uint)index >= (uint)TransactionCount) ThrowNoTransactionRemaining();
        if (_transactions is not null) return _transactions[index];

        // Span-backed, so the decoded fields are copies rather than slices of the database buffer.
        RlpReader reader = new(_transactionData.Span);
        for (int i = 0; i < index; i++) reader.SkipItem();
        Transaction? transaction = new();
        TxDecoder.Instance.Decode(ref reader, ref transaction);
        return transaction ?? throw new RlpException("Transaction decoding returned null.");
    }

    /// <summary>Returns the hash of the next transaction without decoding its fields.</summary>
    /// <remarks>
    /// Legacy transactions hash their complete RLP list, while typed transactions hash the contents
    /// of the enclosing RLP string. The database-backed path hashes those slices directly.
    /// </remarks>
    public Hash256 GetNextTransactionHash()
    {
        if (_transactions is null)
        {
            return new Hash256(GetNextTransactionValueHash());
        }

        if (_currentTransactionIndex >= TransactionCount)
        {
            ThrowNoTransactionRemaining();
        }

        Transaction transaction = _transactions[_currentTransactionIndex++];
        Hash256? transactionHash = transaction.Hash;
        if (transactionHash is null)
        {
            transaction.Hash = transactionHash = TxDecoder.Instance.CalculateHash(transaction, RlpBehaviors.SkipTypedWrapping);
        }

        return transactionHash;
    }

    /// <summary>Returns the hash of the next transaction without decoding its fields or allocating a <see cref="Hash256"/>.</summary>
    /// <remarks><inheritdoc cref="GetNextTransactionHash" path="/remarks"/></remarks>
    public ValueHash256 GetNextTransactionValueHash()
    {
        if (_transactions is not null)
        {
            return GetNextTransactionHash().ValueHash256;
        }

        ReadOnlySpan<byte> hashInput = ReadNextTransactionHashInput(out int encodedLength);
        ValueHash256 hash = ValueKeccak.Compute(hashInput);
        _currentTransactionPosition += encodedLength;
        _currentTransactionIndex++;
        return hash;
    }

    /// <summary>Skips one transaction while checking its envelope bounds and type, without computing its hash.</summary>
    public void SkipNextTransaction()
    {
        if (_transactions is not null)
        {
            if (_currentTransactionIndex >= TransactionCount) ThrowNoTransactionRemaining();
            _currentTransactionIndex++;
            return;
        }

        _ = ReadNextTransactionHashInput(out int encodedLength);
        _currentTransactionPosition += encodedLength;
        _currentTransactionIndex++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private readonly ReadOnlySpan<byte> ReadNextTransactionHashInput(out int encodedLength)
    {
        if (_currentTransactionIndex >= TransactionCount)
        {
            ThrowNoTransactionRemaining();
        }

        if ((uint)_currentTransactionPosition >= (uint)_transactionData.Length)
        {
            RlpHelpers.ThrowRlpDataTruncated();
        }

        RlpReader decoderContext = new(_transactionData)
        {
            Position = _currentTransactionPosition
        };

        bool isLegacy = decoderContext.IsSequenceNext();
        (int prefixLength, int contentLength) = decoderContext.PeekPrefixAndContentLength();
        int bytesRemaining = decoderContext.Length - decoderContext.Position;
        if (prefixLength > bytesRemaining || contentLength > bytesRemaining - prefixLength)
        {
            RlpHelpers.ThrowRlpDataTruncated();
        }

        encodedLength = prefixLength + contentLength;
        ReadOnlySpan<byte> encodedTransaction = decoderContext.Peek(encodedLength);
        ReadOnlySpan<byte> hashInput;
        if (isLegacy)
        {
            if (contentLength == 0)
            {
                ThrowEmptyLegacyTransaction();
            }

            hashInput = encodedTransaction;
        }
        else
        {
            if (contentLength < 2)
            {
                ThrowIncompleteTypedTransaction();
            }

            byte transactionType = encodedTransaction[prefixLength];
            if (transactionType is (byte)TxType.Legacy || transactionType > Transaction.MaxTxType)
            {
                ThrowInvalidTypedTransactionType(transactionType);
            }

            hashInput = encodedTransaction.Slice(prefixLength, contentLength);
        }

        return hashInput;
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowNoTransactionRemaining()
        => throw new RlpException("No transaction remains in the receipt recovery block.");

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowEmptyLegacyTransaction()
        => throw new RlpException("An empty RLP list is not a valid legacy transaction.");

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowIncompleteTypedTransaction()
        => throw new RlpException("A typed transaction envelope must contain a type and payload.");

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowInvalidTypedTransactionType(byte transactionType)
        => throw new RlpException($"Invalid typed transaction type {transactionType}.");

    public readonly Hash256? Hash => Header.Hash; // do not add setter here
    public readonly ulong Number => Header.Number; // do not add setter here

    public readonly void Dispose() => ((IMemoryOwner<byte>?)_memoryOwner)?.Dispose();
}
