// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Threading;
using Autofac.Features.AttributeFilters;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Repositories;

namespace Nethermind.JsonRpc.Modules.Eth;

/// <summary>A stored block as a response without full transactions needs it.</summary>
/// <param name="block">The header, uncles and withdrawals, no transactions, and the stored size as <see cref="Block.EncodedSize"/>.</param>
/// <param name="transactionHashes">The hashes of the block's transactions, in block order. Not to be changed.</param>
public sealed class HashesOnlyBlock(Block block, ValueHash256[] transactionHashes)
{
    private const int HeaderBytes = 1024;
    private const int WithdrawalBytes = 128;

    private int _read;

    public Block Block { get; } = block;

    public ValueHash256[] TransactionHashes { get; } = transactionHashes;

    /// <summary>Roughly what the block keeps alive on the heap: its hashes, header, uncles and withdrawals.</summary>
    public long EstimatedSize { get; } =
        (long)transactionHashes.Length * ValueHash256.MemorySize
        + HeaderBytes * (1L + block.Uncles.Length)
        + WithdrawalBytes * (long)(block.Withdrawals?.Length ?? 0);

    internal void MarkRead()
    {
        if (Volatile.Read(ref _read) == 0) Volatile.Write(ref _read, 1);
    }

    /// <returns>Whether the block was read since the last call.</returns>
    internal bool TakeRead() => Interlocked.Exchange(ref _read, 0) == 1;
}

/// <summary>Reads blocks for <c>eth_getBlockByNumber</c> and <c>eth_getBlockByHash</c> called without full transactions.</summary>
/// <remarks>
/// <para>
/// Each transaction is hashed straight from its envelope in the stored block, so no transaction is decoded, and the
/// results are kept in a cache keyed by block hash and bounded by size. Blocks in the block store's head window are
/// left to the block tree, which holds them decoded already.
/// </para>
/// <para>
/// A block hash fixes everything the response carries, total difficulty included, so a cached block stays right through
/// a reorg, and only canonical blocks are cached. A hit by number costs the one level read that resolves the number,
/// whose canonical entry also confirms the cached total difficulty; a hit by hash reads nothing unless the canonical
/// check is asked for.
/// </para>
/// <para>
/// What a hit cannot see is a block deleted from the store. Bodies pruned below
/// <see cref="IBlockFinder.LowestServedBlock"/> are not served, because history pruning publishes that boundary before
/// it deletes them. Every other deletion of a block below the head window moves the head down first: deleting a chain
/// slice and resetting the head both do, and invalid blocks are never canonical below the head. So the cache is cleared
/// whenever the head moves down, and a read that started before the clear is not cached.
/// </para>
/// </remarks>
public sealed class HashesOnlyBlockReader : IDisposable
{
    private static readonly WithdrawalDecoder WithdrawalDecoder = new();

    private readonly IDb _blockDb;
    private readonly IHeaderDecoder _headerDecoder;
    private readonly IChainLevelInfoRepository _chainLevels;
    private readonly IBlockTree _blockTree;
    private readonly ulong _headWindow;
    private readonly HashesOnlyBlockCache _cache;
    private ulong _headNumber;

    public HashesOnlyBlockReader(
        [KeyFilter(DbNames.Blocks)] IDb blockDb,
        IHeaderDecoder headerDecoder,
        IChainLevelInfoRepository chainLevels,
        IBlockTree blockTree)
        : this(blockDb, headerDecoder, chainLevels, blockTree, BlockStore.CacheSize, HashesOnlyBlockCache.DefaultByteBudget)
    {
    }

    /// <param name="blockDb">The blocks database the block store writes.</param>
    /// <param name="headerDecoder">The chain's header decoder.</param>
    /// <param name="chainLevels">The chain levels the block tree reads.</param>
    /// <param name="blockTree">The block tree whose head moving down clears the cache.</param>
    /// <param name="headWindow">How many blocks below the head are left to the block tree.</param>
    /// <param name="cacheByteBudget">The estimated size the cached blocks may take.</param>
    internal HashesOnlyBlockReader(
        IDb blockDb,
        IHeaderDecoder headerDecoder,
        IChainLevelInfoRepository chainLevels,
        IBlockTree blockTree,
        ulong headWindow,
        long cacheByteBudget)
    {
        _blockDb = blockDb;
        _headerDecoder = headerDecoder;
        _chainLevels = chainLevels;
        _blockTree = blockTree;
        _headNumber = blockTree.Head?.Number ?? 0;
        blockTree.NewHeadBlock += OnNewHeadBlock;
        _headWindow = headWindow;
        _cache = new HashesOnlyBlockCache(cacheByteBudget);
    }

    /// <returns>The block, or <see langword="null"/> when it has to be looked up through the block tree.</returns>
    public HashesOnlyBlock? Find(IBlockFinder blockFinder, BlockParameter blockParameter)
    {
        if (blockFinder.Head is not { } head || head.Number < _headWindow)
        {
            return null;
        }

        ulong windowStart = head.Number - _headWindow;
        return blockParameter.Type switch
        {
            BlockParameterType.BlockNumber => FindByNumber(blockFinder, blockParameter, windowStart),
            BlockParameterType.BlockHash => FindByHash(blockFinder, blockParameter.BlockHash!, blockParameter.RequireCanonical, windowStart),
            _ => null
        };
    }

    /// <remarks>Resolves the number through its level, as the block tree does for a canonical block. A level with no
    /// canonical block is left to the block tree.</remarks>
    private HashesOnlyBlock? FindByNumber(IBlockFinder blockFinder, BlockParameter blockParameter, ulong windowStart)
    {
        ulong number = blockParameter.BlockNumber!.Value;
        if (number >= windowStart || _chainLevels.LoadLevel(number)?.MainChainBlock is not { } blockInfo)
        {
            return null;
        }

        Hash256 blockHash = blockInfo.BlockHash;
        return _cache.TryGet(blockHash.ValueHash256, out HashesOnlyBlock? cached) && IsCurrent(blockFinder, cached!, blockInfo)
            ? cached
            : Load(blockFinder, blockHash, number, blockParameter.RequireCanonical, windowStart);
    }

    private HashesOnlyBlock? FindByHash(IBlockFinder blockFinder, Hash256 blockHash, bool requireCanonical, ulong windowStart)
    {
        if (_cache.TryGet(blockHash.ValueHash256, out HashesOnlyBlock? cached)
            && cached!.Block.Number >= blockFinder.LowestServedBlock
            && (!requireCanonical || IsCanonical(cached, blockHash)))
        {
            return cached;
        }

        return Load(blockFinder, blockHash, blockNumber: null, requireCanonical, windowStart);
    }

    /// <summary>Whether a cached block can be served by number: <paramref name="blockInfo"/>, the level's canonical entry
    /// the number resolved through, still reports the cached total difficulty.</summary>
    private static bool IsCurrent(IBlockFinder blockFinder, HashesOnlyBlock cached, BlockInfo blockInfo) =>
        cached.Block.Header.TotalDifficulty == blockInfo.TotalDifficulty
        && cached.Block.Number >= blockFinder.LowestServedBlock;

    private bool IsCanonical(HashesOnlyBlock cached, Hash256 blockHash) =>
        _chainLevels.LoadLevel(cached.Block.Number)?.MainChainBlock is { } blockInfo
        && blockInfo.BlockHash == blockHash
        && cached.Block.Header.TotalDifficulty == blockInfo.TotalDifficulty;

    private HashesOnlyBlock? Load(IBlockFinder blockFinder, Hash256 blockHash, ulong? blockNumber, bool requireCanonical, ulong windowStart)
    {
        if (blockHash == blockFinder.GenesisHash)
        {
            return null;
        }

        long generation = _cache.Generation;

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
            _cache.Add(blockHash.ValueHash256, block, generation);
        }

        return block;
    }

    public void Dispose() => _blockTree.NewHeadBlock -= OnNewHeadBlock;

    private void OnNewHeadBlock(object? sender, BlockEventArgs e)
    {
        ulong number = e.Block.Number;
        if (number < Interlocked.Exchange(ref _headNumber, number))
        {
            _cache.Clear();
        }
    }

    /// <summary>Reads a stored block the way the block store finds it, by number-prefixed key and then by hash alone.</summary>
    [SkipLocalsInit]
    internal HashesOnlyBlock? Read(ulong blockNumber, Hash256 blockHash)
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

    /// <returns>The block, or <see langword="null"/> when the stored layout is not the one this reader knows, so that a
    /// body field a later fork adds leaves the block to the block tree's decoder instead of failing the request.</returns>
    private HashesOnlyBlock? Decode(Memory<byte> memory)
    {
        try
        {
            return DecodeKnownLayout(memory);
        }
        catch (Exception e) when (e is RlpException or IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <remarks>Mirrors <see cref="BlockDecoder"/> and <see cref="BlockBodyDecoder.DecodeUnwrapped"/>: header, then
    /// transactions, uncles and optional withdrawals. The reader is span-backed so that nothing decoded keeps a slice of
    /// the database buffer, which is released on return.</remarks>
    private HashesOnlyBlock? DecodeKnownLayout(Memory<byte> memory)
    {
        RlpReader reader = new(memory.Span);
        if (reader.IsNextItemEmptyList())
        {
            return null;
        }

        int sequenceLength = reader.ReadSequenceLength();
        int blockEnd = reader.Position + sequenceLength;
        BlockHeader header = _headerDecoder.DecodeGuardNotNull(ref reader);
        ValueHash256[] transactionHashes = ReadTransactionHashes(ref reader, memory, header);
        BlockHeader[] uncles = reader.DecodeNonNullArray(_headerDecoder);
        Withdrawal[]? withdrawals = reader.PeekNumberOfItemsRemaining(blockEnd, 1) > 0
            ? reader.DecodeNonNullArray(WithdrawalDecoder)
            : null;
        if (reader.Position != blockEnd)
        {
            return null;
        }

        Block block = new(header, new BlockBody([], uncles, withdrawals))
        {
            EncodedSize = Rlp.LengthOfSequence(sequenceLength)
        };
        return new HashesOnlyBlock(block, transactionHashes);
    }

    /// <remarks>Hashes through <see cref="ReceiptRecoveryBlock"/>, which hashes each envelope the way the transaction
    /// decoder sets <see cref="Transaction.Hash"/>.</remarks>
    private static ValueHash256[] ReadTransactionHashes(ref RlpReader reader, Memory<byte> memory, BlockHeader header)
    {
        int contentLength = reader.ReadSequenceLength();
        int count = reader.PeekNumberOfItemsRemaining(reader.Position + contentLength);
        ReceiptRecoveryBlock transactions = new(null, header, memory.Slice(reader.Position, contentLength), count);
        reader.SkipBytes(contentLength);
        if (count == 0)
        {
            return [];
        }

        ValueHash256[] hashes = new ValueHash256[count];
        for (int i = 0; i < hashes.Length; i++)
        {
            hashes[i] = transactions.GetNextTransactionHash().ValueHash256;
        }

        return hashes;
    }
}
