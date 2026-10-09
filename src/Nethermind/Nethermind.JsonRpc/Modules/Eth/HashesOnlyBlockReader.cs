// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Autofac.Features.AttributeFilters;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Headers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Int256;
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
/// A miss resolves the block as the block tree's lookup does: one level read gives the block's entry, its total
/// difficulty and whether it is canonical, and the header is the one decoded from the stored block. A block hash fixes
/// everything the response carries, so a cached block stays right through a reorg, and only canonical blocks are cached.
/// A hit by number costs the one level read that resolves the number, whose canonical entry also confirms the cached
/// total difficulty; a hit by hash reads nothing unless the canonical check is asked for. An entry that no longer
/// matches is read again and replaced.
/// </para>
/// <para>
/// What a hit by hash cannot see is a block deleted from the store. Bodies pruned below
/// <see cref="IBlockFinder.LowestServedBlock"/> are not served, because history pruning publishes that boundary before
/// it deletes them. Deleting a chain slice, which is how resetting the head and chain recovery remove blocks below the
/// head window, moves the head down once it is done, and invalid blocks are never canonical below the head. So the cache
/// is cleared whenever the head moves down, and a read that started before the clear is not cached. While a slice is
/// being deleted, a hit by hash can still serve a block the slice is removing.
/// </para>
/// </remarks>
public sealed class HashesOnlyBlockReader : IDisposable
{
    private const int MaxConcurrentLoads = 64;
    private static readonly WithdrawalDecoder WithdrawalDecoder = new();

    private readonly IDb _blockDb;
    private readonly IHeaderDecoder _headerDecoder;
    private readonly IChainLevelInfoRepository _chainLevels;
    private readonly IHeaderStore _headerStore;
    private readonly IBlockTree _blockTree;
    private readonly ISpecProvider _specProvider;
    private readonly ulong _headWindow;
    private readonly HashesOnlyBlockCache _cache;
    private readonly ConcurrentDictionary<LoadKey, TaskCompletionSource<HashesOnlyBlock?>> _loads = new();
    private readonly SemaphoreSlim _loadSlots = new(MaxConcurrentLoads);
    private ulong _headNumber;

    private readonly record struct LoadKey(ValueHash256 Hash, ulong Number, long Generation, UInt256? TotalDifficulty, bool IsCanonical, ulong LowestServedBlock);

    public HashesOnlyBlockReader(
        [KeyFilter(DbNames.Blocks)] IDb blockDb,
        IHeaderDecoder headerDecoder,
        IChainLevelInfoRepository chainLevels,
        IHeaderStore headerStore,
        IBlockTree blockTree,
        ISpecProvider specProvider)
        : this(blockDb, headerDecoder, chainLevels, headerStore, blockTree, specProvider, BlockStore.CacheSize, HashesOnlyBlockCache.DefaultByteBudget)
    {
    }

    /// <param name="blockDb">The blocks database the block store writes.</param>
    /// <param name="headerDecoder">The chain's header decoder.</param>
    /// <param name="chainLevels">The chain levels the block tree reads.</param>
    /// <param name="headerStore">The header store, for the number of a block requested by hash.</param>
    /// <param name="blockTree">The block tree whose head moving down clears the cache.</param>
    /// <param name="specProvider">The chain's specs, for a total difficulty that is always zero.</param>
    /// <param name="headWindow">How many blocks below the head are left to the block tree.</param>
    /// <param name="cacheByteBudget">The estimated size the cached blocks may take.</param>
    internal HashesOnlyBlockReader(
        IDb blockDb,
        IHeaderDecoder headerDecoder,
        IChainLevelInfoRepository chainLevels,
        IHeaderStore headerStore,
        IBlockTree blockTree,
        ISpecProvider specProvider,
        ulong headWindow,
        long cacheByteBudget)
    {
        _blockDb = blockDb;
        _headerDecoder = headerDecoder;
        _chainLevels = chainLevels;
        _headerStore = headerStore;
        _blockTree = blockTree;
        _specProvider = specProvider;
        _headWindow = headWindow;
        _cache = new HashesOnlyBlockCache(cacheByteBudget);
        _headNumber = blockTree.Head?.Number ?? 0;
        blockTree.NewHeadBlock += OnNewHeadBlock;
    }

    /// <returns>The block, or <see langword="null"/> when it has to be looked up through the block tree.</returns>
    public HashesOnlyBlock? Find(IBlockFinder blockFinder, BlockParameter blockParameter)
    {
        long generation = _cache.Generation;
        if (blockFinder.Head is not { } head || head.Number < _headWindow)
        {
            return null;
        }

        ulong windowStart = head.Number - _headWindow;
        return blockParameter.Type switch
        {
            BlockParameterType.BlockNumber => FindByNumber(blockFinder, blockParameter.BlockNumber!.Value, windowStart, generation),
            BlockParameterType.BlockHash => FindByHash(blockFinder, blockParameter.BlockHash!, blockParameter.RequireCanonical, windowStart, generation),
            _ => null
        };
    }

    /// <remarks>Resolves the number through its level, as the block tree does for a canonical block. A level with no
    /// canonical block is left to the block tree.</remarks>
    private HashesOnlyBlock? FindByNumber(IBlockFinder blockFinder, ulong number, ulong windowStart, long generation)
    {
        if (number >= windowStart || _chainLevels.LoadLevel(number) is not { MainChainBlock: { } blockInfo } level)
        {
            return null;
        }

        Hash256 blockHash = blockInfo.BlockHash;
        if (blockHash == blockFinder.GenesisHash)
        {
            return null;
        }

        return _cache.TryGet(blockHash.ValueHash256, out HashesOnlyBlock? cached) && IsCurrent(blockFinder, cached!, blockInfo)
            ? cached
            : Load(blockHash, number, level, generation, requireCanonical: true, blockFinder.LowestServedBlock);
    }

    private HashesOnlyBlock? FindByHash(IBlockFinder blockFinder, Hash256 blockHash, bool requireCanonical, ulong windowStart, long generation)
    {
        if (_cache.TryGet(blockHash.ValueHash256, out HashesOnlyBlock? cached)
            && cached!.Block.Number >= blockFinder.LowestServedBlock
            && (!requireCanonical || IsCanonical(cached, blockHash)))
        {
            return cached;
        }

        // Only the number is needed here, and the header is decoded from the stored block, as the block tree's lookup does.
        if (blockHash == blockFinder.GenesisHash
            || _headerStore.GetBlockNumber(blockHash) is not { } number
            || number >= windowStart
            || _chainLevels.LoadLevel(number) is not { } level)
        {
            return null;
        }

        return Load(blockHash, number, level, generation, requireCanonical, blockFinder.LowestServedBlock);
    }

    /// <summary>Whether a cached block can be served: <paramref name="blockInfo"/>, its canonical entry, still gives the
    /// cached total difficulty, and its body has not been pruned.</summary>
    private bool IsCurrent(IBlockFinder blockFinder, HashesOnlyBlock cached, BlockInfo blockInfo) =>
        cached.Block.Header.TotalDifficulty == ResolveTotalDifficulty(blockInfo)
        && cached.Block.Number >= blockFinder.LowestServedBlock;

    private bool IsCanonical(HashesOnlyBlock cached, Hash256 blockHash) =>
        _chainLevels.LoadLevel(cached.Block.Number)?.MainChainBlock is { } blockInfo
        && blockInfo.BlockHash == blockHash
        && cached.Block.Header.TotalDifficulty == ResolveTotalDifficulty(blockInfo);

    /// <remarks>A level without the block's entry is left to the block tree, which may create it.</remarks>
    private HashesOnlyBlock? Load(Hash256 blockHash, ulong number, ChainLevelInfo level, long generation, bool requireCanonical, ulong lowestServedBlock)
    {
        bool isCanonical = level.MainChainBlock?.BlockHash == blockHash;
        if ((requireCanonical && !isCanonical) || level.FindBlockInfo(blockHash) is not { } blockInfo)
        {
            return null;
        }

        LoadKey key = new(blockHash.ValueHash256, number, generation, ResolveTotalDifficulty(blockInfo), isCanonical, lowestServedBlock);
        if (_loads.TryGetValue(key, out TaskCompletionSource<HashesOnlyBlock?>? pending))
        {
            return ReadShared(pending, key, blockHash);
        }

        if (!_loadSlots.Wait(0))
        {
            return ReadAndCache(key, blockHash);
        }

        TaskCompletionSource<HashesOnlyBlock?> load = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<HashesOnlyBlock?> selected = _loads.GetOrAdd(key, load);
        if (!ReferenceEquals(selected, load))
        {
            _loadSlots.Release();
            return ReadShared(selected, key, blockHash);
        }

        try
        {
            HashesOnlyBlock? block = ReadAndCache(key, blockHash);
            load.SetResult(block);
            return block;
        }
        finally
        {
            load.TrySetResult(null);
            _loads.TryRemove(new KeyValuePair<LoadKey, TaskCompletionSource<HashesOnlyBlock?>>(key, load));
            _loadSlots.Release();
        }
    }

    private HashesOnlyBlock? ReadShared(TaskCompletionSource<HashesOnlyBlock?> pending, LoadKey key, Hash256 blockHash)
    {
        HashesOnlyBlock? block = pending.Task.GetAwaiter().GetResult();
        return block ?? ReadAndCache(key, blockHash);
    }

    private HashesOnlyBlock? ReadAndCache(LoadKey key, Hash256 blockHash)
    {
        if (_cache.Generation == key.Generation
            && _cache.TryGet(key.Hash, out HashesOnlyBlock? cached)
            && cached!.Block.Number >= key.LowestServedBlock
            && cached.Block.Header.TotalDifficulty == key.TotalDifficulty)
        {
            return cached;
        }

        HashesOnlyBlock? block = Read(key.Number, blockHash);
        if (block is null)
        {
            return null;
        }

        block.Block.Header.TotalDifficulty = key.TotalDifficulty;
        if (key.IsCanonical)
        {
            _cache.Add(key.Hash, block, key.Generation);
        }

        return block;
    }

    /// <summary>The total difficulty the block tree sets from a level entry on a block it has just decoded.</summary>
    /// <remarks>A level written without a known total difficulty stores zero, which leaves the header's unset unless
    /// the chain's total difficulty is always zero.</remarks>
    private UInt256? ResolveTotalDifficulty(BlockInfo blockInfo) =>
        !blockInfo.TotalDifficulty.IsZero
            ? blockInfo.TotalDifficulty
            : _blockTree.Genesis?.Difficulty == 0 && _specProvider.TerminalTotalDifficulty == 0
                ? UInt256.Zero
                : null;

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

    /// <returns>The block, or <see langword="null"/> when the stored block does not decode as the layout this reader
    /// knows, so that a body field a later fork adds, or anything else unexpected, leaves the block to the block tree's
    /// decoder instead of failing the request.</returns>
    private HashesOnlyBlock? Decode(Memory<byte> memory)
    {
        try
        {
            return DecodeKnownLayout(memory);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <remarks>Mirrors <see cref="BlockDecoder"/> and <see cref="BlockBodyDecoder.DecodeUnwrapped"/>, with its count
    /// limits: header, then transactions, uncles and optional withdrawals. The reader is span-backed so that nothing decoded keeps a slice of
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
        BlockHeader[] uncles = reader.DecodeNonNullArray(_headerDecoder, limit: BlockBodyDecoder.UnclesCountLimit);
        Withdrawal[]? withdrawals = reader.PeekNumberOfItemsRemaining(blockEnd, 1) > 0
            ? reader.DecodeNonNullArray(WithdrawalDecoder, limit: BlockBodyDecoder.WithdrawalsCountLimit)
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

    /// <remarks>Hashes through <see cref="ReceiptRecoveryBlock.GetNextTransactionValueHash"/>, which hashes each envelope
    /// the way the transaction decoder sets <see cref="Transaction.Hash"/>.</remarks>
    private static ValueHash256[] ReadTransactionHashes(ref RlpReader reader, Memory<byte> memory, BlockHeader header)
    {
        int contentLength = reader.ReadSequenceLength();
        RlpLimit limit = BlockBodyDecoder.TransactionsCountLimit;
        int count = reader.PeekNumberOfItemsRemaining(reader.Position + contentLength, limit.Limit + 1);
        reader.GuardLimit(count, limit);
        ReceiptRecoveryBlock transactions = new(null, header, memory.Slice(reader.Position, contentLength), count);
        reader.SkipBytes(contentLength);
        if (count == 0)
        {
            return [];
        }

        ValueHash256[] hashes = new ValueHash256[count];
        for (int i = 0; i < hashes.Length; i++)
        {
            hashes[i] = transactions.GetNextTransactionValueHash();
        }

        return hashes;
    }
}
