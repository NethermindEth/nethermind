// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using Autofac.Features.AttributeFilters;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;

namespace Nethermind.JsonRpc.Modules.Eth;

/// <summary>A stored block as a response without full transactions needs it.</summary>
/// <param name="block">The header, uncles and withdrawals, no transactions, and the stored size as <see cref="Block.EncodedSize"/>.</param>
/// <param name="transactionHashes">The hashes of the block's transactions, in block order.</param>
public sealed class HashesOnlyBlock(Block block, Hash256[] transactionHashes)
{
    public Block Block { get; } = block;

    public Hash256[] TransactionHashes { get; } = transactionHashes;
}

/// <summary>Reads blocks for <c>eth_getBlockByNumber</c> and <c>eth_getBlockByHash</c> called without full transactions.</summary>
/// <remarks>
/// <para>
/// Each transaction is hashed straight from its envelope in the stored block, so no transaction is decoded, and the
/// results are kept in a small cache keyed by block hash. Blocks in the block store's head window are left to the
/// block tree, which holds them decoded already.
/// </para>
/// <para>
/// A block hash fixes everything the response carries, total difficulty included, so a cached entry cannot go stale on
/// a reorg: numbers are resolved to hashes on every request. Only canonical blocks are cached, and a cached block below
/// <see cref="IBlockFinder.LowestServedBlock"/> is read again, because history pruning publishes that boundary before
/// it deletes the bodies behind it.
/// </para>
/// </remarks>
public sealed class HashesOnlyBlockReader
{
    /// <summary>About 8 MB of mainnet blocks: 250 transactions at 56 bytes a hash, plus header and withdrawals.</summary>
    public const int CacheSize = 512;

    private static readonly WithdrawalDecoder WithdrawalDecoder = new();

    private readonly IDb _blockDb;
    private readonly IHeaderDecoder _headerDecoder;
    private readonly ulong _headWindow;
    private readonly AssociativeCache<ValueHash256, HashesOnlyBlock> _cache = new(CacheSize);

    public HashesOnlyBlockReader([KeyFilter(DbNames.Blocks)] IDb blockDb, IHeaderDecoder headerDecoder)
        : this(blockDb, headerDecoder, BlockStore.CacheSize)
    {
    }

    /// <param name="blockDb">The blocks database the block store writes.</param>
    /// <param name="headerDecoder">The chain's header decoder.</param>
    /// <param name="headWindow">How many blocks below the head are left to the block tree.</param>
    internal HashesOnlyBlockReader(IDb blockDb, IHeaderDecoder headerDecoder, ulong headWindow)
    {
        _blockDb = blockDb;
        _headerDecoder = headerDecoder;
        _headWindow = headWindow;
    }

    /// <returns>The block, or <see langword="null"/> when it has to be looked up through the block tree.</returns>
    public HashesOnlyBlock? Find(IBlockFinder blockFinder, BlockParameter blockParameter)
    {
        if (blockFinder.Head is not { } head || head.Number < _headWindow)
        {
            return null;
        }

        ulong windowStart = head.Number - _headWindow;
        ulong? blockNumber = null;
        Hash256? blockHash;
        switch (blockParameter.Type)
        {
            case BlockParameterType.BlockNumber:
                blockNumber = blockParameter.BlockNumber!.Value;
                if (blockNumber >= windowStart) return null;
                blockHash = blockFinder.FindBlockHash(blockNumber.Value);
                break;
            case BlockParameterType.BlockHash:
                blockHash = blockParameter.BlockHash;
                break;
            default:
                return null;
        }

        return blockHash is null || blockHash == blockFinder.GenesisHash
            ? null
            : Find(blockFinder, blockHash, blockNumber, blockParameter.RequireCanonical, windowStart);
    }

    private HashesOnlyBlock? Find(IBlockFinder blockFinder, Hash256 blockHash, ulong? blockNumber, bool requireCanonical, ulong windowStart)
    {
        ValueHash256 cacheKey = blockHash.ValueHash256;
        if (_cache.TryGet(in cacheKey, out HashesOnlyBlock? cached)
            && cached!.Block.Number >= blockFinder.LowestServedBlock
            && (!requireCanonical || blockFinder.IsMainChain(cached.Block.Header)))
        {
            return cached;
        }

        // Resolves total difficulty and the canonical check as the block lookup does.
        BlockHeader? header = blockFinder.FindHeader(blockHash,
            requireCanonical ? BlockTreeLookupOptions.RequireCanonical : BlockTreeLookupOptions.None, blockNumber);
        if (header is null || header.Number >= windowStart)
        {
            return null;
        }

        HashesOnlyBlock? block = Read(header.Number, blockHash);
        if (block is null)
        {
            return null;
        }

        block.Block.Header.TotalDifficulty = header.TotalDifficulty;
        if (blockFinder.IsMainChain(header))
        {
            _cache.Set(in cacheKey, block);
        }

        return block;
    }

    /// <summary>Reads a stored block the way the block store finds it, by number-prefixed key and then by hash alone.</summary>
    [SkipLocalsInit]
    public HashesOnlyBlock? Read(ulong blockNumber, Hash256 blockHash)
    {
        Span<byte> dbKey = stackalloc byte[40];
        KeyValueStoreExtensions.GetBlockNumPrefixedKey(blockNumber, blockHash, dbKey);
        MemoryManager<byte>? memoryOwner = _blockDb.GetOwnedMemory(dbKey) ?? _blockDb.GetOwnedMemory(blockHash.Bytes);
        if (memoryOwner is null)
        {
            return null;
        }

        try
        {
            return Decode(memoryOwner.Memory);
        }
        finally
        {
            ((IMemoryOwner<byte>)memoryOwner).Dispose();
        }
    }

    /// <remarks>Mirrors <see cref="BlockDecoder"/>. The reader is span-backed so that nothing decoded keeps a slice of
    /// the database buffer, which is released on return.</remarks>
    private HashesOnlyBlock? Decode(Memory<byte> memory)
    {
        RlpReader reader = new(memory.Span);
        if (reader.IsNextItemEmptyList())
        {
            return null;
        }

        int sequenceLength = reader.ReadSequenceLength();
        int blockEnd = reader.Position + sequenceLength;
        BlockHeader header = _headerDecoder.DecodeGuardNotNull(ref reader);
        Hash256[] transactionHashes = ReadTransactionHashes(ref reader, memory, header);
        BlockHeader[] uncles = reader.DecodeNonNullArray(_headerDecoder);
        Withdrawal[]? withdrawals = reader.PeekNumberOfItemsRemaining(blockEnd, 1) > 0
            ? reader.DecodeNonNullArray(WithdrawalDecoder)
            : null;
        reader.Check(blockEnd);

        Block block = new(header, new BlockBody([], uncles, withdrawals))
        {
            EncodedSize = Rlp.LengthOfSequence(sequenceLength)
        };
        return new HashesOnlyBlock(block, transactionHashes);
    }

    /// <remarks>Hashes through <see cref="ReceiptRecoveryBlock"/>, which hashes each envelope the way the transaction
    /// decoder sets <see cref="Transaction.Hash"/>.</remarks>
    private static Hash256[] ReadTransactionHashes(ref RlpReader reader, Memory<byte> memory, BlockHeader header)
    {
        int contentLength = reader.ReadSequenceLength();
        int count = reader.PeekNumberOfItemsRemaining(reader.Position + contentLength);
        ReceiptRecoveryBlock transactions = new(null, header, memory.Slice(reader.Position, contentLength), count);
        reader.SkipBytes(contentLength);
        if (count == 0)
        {
            return [];
        }

        Hash256[] hashes = new Hash256[count];
        for (int i = 0; i < hashes.Length; i++)
        {
            hashes[i] = transactions.GetNextTransactionHash();
        }

        return hashes;
    }
}
