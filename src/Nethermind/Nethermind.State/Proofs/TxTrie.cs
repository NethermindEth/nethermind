// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Cpu;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;

namespace Nethermind.State.Proofs;

/// <summary>
/// Represents a Patricia trie built of a collection of <see cref="Transaction"/>.
/// </summary>
public sealed class TxTrie : PatriciaTrie<Transaction>
{
    private static readonly TxDecoder _txDecoder = TxDecoder.Instance;

    /// <inheritdoc/>
    /// <param name="transactions">The transactions to build the trie of.</param>
    public TxTrie(ReadOnlySpan<Transaction> transactions, bool canBuildProof = false, ICappedArrayPool? bufferPool = null, bool canBeParallel = true)
        : base(transactions, canBuildProof, bufferPool: bufferPool, canBeParallel: canBeParallel) { }

    protected override void Initialize(ReadOnlySpan<Transaction> list)
    {
        int key = 0;

        foreach (Transaction transaction in list)
        {
            ref readonly Memory<byte> rlp = ref transaction.PreHash;
            CappedArray<byte> buffer = (rlp.Length > 0) ?
                CopyExistingRlp(rlp.Span, _bufferPool) :
                _txDecoder.EncodeToCappedArray(transaction, rlpBehaviors: RlpBehaviors.SkipTypedWrapping, bufferPool: _bufferPool);
            CappedArray<byte> keyBuffer = Rlp.EncodeToCappedArray(key, _bufferPool);
            key++;

            Set(keyBuffer.AsSpan(), buffer);
        }

        static CappedArray<byte> CopyExistingRlp(ReadOnlySpan<byte> rlp, ICappedArrayPool? bufferPool)
        {
            CappedArray<byte> buffer = bufferPool.SafeRent(rlp.Length);
            rlp.CopyTo(buffer.AsSpan());
            return buffer;
        }
    }

    public static byte[][] CalculateProof(ReadOnlySpan<Transaction> transactions, int index)
    {
        bool canBeParallel = transactions.Length > MinItemsForParallelRootHash;
        using TrackingCappedArrayPool cappedArray = new(transactions.Length * 4, canBeParallel: canBeParallel);
        byte[][] rootHash = new TxTrie(transactions, canBuildProof: true, bufferPool: cappedArray, canBeParallel: canBeParallel).BuildProof(index);
        return rootHash;
    }

    public static Hash256 CalculateRoot(ReadOnlySpan<Transaction> transactions) =>
        CalculateRoot(transactions, canBeParallel: true);

    internal static Hash256 CalculateRoot(ReadOnlySpan<Transaction> transactions, bool canBeParallel) =>
        !canBeParallel || RuntimeInformation.IsSingleProcessor || transactions.Length <= MinItemsForParallelRootHash
            ? new IndexedTrieRoot.Calculator<Transaction, TransactionEncoder>(transactions, default).Calculate(canBeParallel: false)
            : CalculateParallelRoot(transactions);

    private static Hash256 CalculateParallelRoot(ReadOnlySpan<Transaction> transactions)
    {
        using ArrayPoolList<ReadOnlyMemory<byte>> encoded = new(transactions.Length, transactions.Length);
        using ArrayPoolList<int> lengths = new(transactions.Length, transactions.Length);
        int totalLength = 0;
        for (int i = 0; i < transactions.Length; i++)
        {
            Transaction transaction = transactions[i];
            ReadOnlyMemory<byte> value = transaction.PreHash;
            int length = value.IsEmpty ? _txDecoder.GetLength(transaction, RlpBehaviors.SkipTypedWrapping) : value.Length;
            if (length > Array.MaxLength - totalLength)
                return new IndexedTrieRoot.Calculator<Transaction, TransactionEncoder>(transactions, default).Calculate(canBeParallel: false);
            lengths[i] = length;
            totalLength += length;
        }

        using ArrayPoolDisposableReturn rental = ArrayPoolDisposableReturn.Rent(totalLength, out byte[] buffer);
        int offset = 0;
        for (int i = 0; i < transactions.Length; i++)
        {
            int length = lengths[i];
            Memory<byte> value = buffer.AsMemory(offset, length);
            // An earlier codec can release this transaction's cached RLP by reading its Hash.
            ReadOnlySpan<byte> cached = transactions[i].PreHash.Span;
            if (cached.IsEmpty)
            {
                RlpWriter writer = new(value.Span);
                // Registered transaction codecs retain caller-thread encoding; only hashing fans out.
                _txDecoder.Encode(ref writer, transactions[i], RlpBehaviors.SkipTypedWrapping);
                Debug.Assert(writer.Position == length);
            }
            else
            {
                // Reading Transaction.Hash can release PreHash's owner before the workers finish.
                cached.CopyTo(value.Span);
            }
            encoded[i] = value;
            offset += length;
        }
        return new IndexedTrieRoot.Calculator<ReadOnlyMemory<byte>, EncodedMemoryEncoder>(encoded.AsSpan(), default).Calculate();
    }

    public static Hash256 CalculateRoot(ReadOnlySpan<byte[]> encodedTransactions)
    {
        foreach (byte[] value in encodedTransactions)
        {
            // Empty values delete keys in PatriciaTree.Set, so they cannot use the dense indexed trie.
            if (value is null || value.Length == 0) return CalculateSparseRoot<byte[], EncodedTransactionEncoder>(encodedTransactions, default);
        }
        return new IndexedTrieRoot.Calculator<byte[], EncodedTransactionEncoder>(encodedTransactions, default).Calculate();
    }

    private static Hash256 CalculateSparseRoot<T, TEncoder>(ReadOnlySpan<T> values, TEncoder encoder)
        where TEncoder : struct, IndexedTrieRoot.IValueEncoder<T>
    {
        bool canBeParallel = values.Length > MinItemsForParallelRootHash;
        using TrackingCappedArrayPool pool = new(values.Length * 4, canBeParallel: canBeParallel);
        TxTrie trie = new(ReadOnlySpan<Transaction>.Empty, bufferPool: pool, canBeParallel: canBeParallel);
        for (int key = 0; key < values.Length; key++)
        {
            ReadOnlySpan<byte> value = encoder.GetEncodedValue(values[key]);
            CappedArray<byte> buffer = pool.Rent(value.Length);
            value.CopyTo(buffer.AsSpan());
            trie.Set(Rlp.EncodeToCappedArray(key, pool).AsSpan(), buffer);
        }
        trie.UpdateRootHash(canBeParallel);
        return trie.RootHash;
    }

    private readonly struct TransactionEncoder : IndexedTrieRoot.IValueEncoder<Transaction>
    {
        public ReadOnlySpan<byte> GetEncodedValue(Transaction item) => item.PreHash.Span;
        public int GetLength(Transaction item) => _txDecoder.GetLength(item, RlpBehaviors.SkipTypedWrapping);
        public void Encode<TWriter>(ref TWriter writer, Transaction item) where TWriter : struct, IRlpWriteBackend, allows ref struct => _txDecoder.Encode(ref writer, item, RlpBehaviors.SkipTypedWrapping);
    }

    private readonly struct EncodedTransactionEncoder : IndexedTrieRoot.IValueEncoder<byte[]>
    {
        public ReadOnlySpan<byte> GetEncodedValue(byte[] item) => item;
        public int GetLength(byte[] item) => item.Length;
        public void Encode<TWriter>(ref TWriter writer, byte[] item) where TWriter : struct, IRlpWriteBackend, allows ref struct => writer.Write(item);
    }

    private readonly struct EncodedMemoryEncoder : IndexedTrieRoot.IValueEncoder<ReadOnlyMemory<byte>>
    {
        public ReadOnlySpan<byte> GetEncodedValue(ReadOnlyMemory<byte> item) => item.Span;
        public int GetLength(ReadOnlyMemory<byte> item) => item.Length;
        public void Encode<TWriter>(ref TWriter writer, ReadOnlyMemory<byte> item) where TWriter : struct, IRlpWriteBackend, allows ref struct => writer.Write(item.Span);
    }
}
