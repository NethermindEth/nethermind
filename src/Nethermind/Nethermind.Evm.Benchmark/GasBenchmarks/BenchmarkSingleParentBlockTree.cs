// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Evm.Benchmark.GasBenchmarks;

/// <summary>
/// Block tree stub for gas benchmarks that knows about a single parent header.
/// Used by both NewPayload and BlockBuilding benchmarks. Replaces the previously
/// duplicated BenchmarkNewPayloadBlockTree and BenchmarkBlockProducerBlockTree.
/// </summary>
internal sealed class BenchmarkSingleParentBlockTree : BenchmarkBlockTreeBase
{
    private readonly BlockHeader _parentHeader;
    private readonly Block _head;
    private readonly BlockInfo _parentInfo;
    private readonly bool _isBetterThanHead;

    /// <param name="parentHeader">The parent block header that this tree knows about.</param>
    /// <param name="isBetterThanHead">
    /// False for NewPayload benchmarks (handler drives processing, not the tree).
    /// True (default) for BlockBuilding benchmarks.
    /// </param>
    public BenchmarkSingleParentBlockTree(BlockHeader parentHeader, bool isBetterThanHead = true)
    {
        _parentHeader = parentHeader;
        _isBetterThanHead = isBetterThanHead;
        _head = new Block(parentHeader, new BlockBody(Array.Empty<Transaction>(), Array.Empty<BlockHeader>(), null));
        _parentInfo = new BlockInfo(parentHeader.Hash, parentHeader.TotalDifficulty ?? UInt256.Zero)
        {
            WasProcessed = true,
            BlockNumber = parentHeader.Number,
        };
        BestSuggestedHeader = parentHeader;
    }

    public override Hash256 HeadHash => _head.Hash;
    public override Hash256 GenesisHash => _parentHeader.Hash;
    public override Block Head => _head;
    public override BlockHeader Genesis => _parentHeader;
    public override Block BestSuggestedBody => _head;
    public override BlockHeader BestSuggestedBeaconHeader => _parentHeader;
    public override long BestKnownNumber => _parentHeader.Number;
    public override long BestKnownBeaconNumber => _parentHeader.Number;
    public override long GetLowestBlock() => _parentHeader.Number;

    public override Block FindBlock(Hash256 blockHash, BlockTreeLookupOptions options, long? blockNumber = null) =>
        blockHash == _parentHeader.Hash ? _head : null;
    public override Block FindBlock(long blockNumber, BlockTreeLookupOptions options) =>
        blockNumber == _parentHeader.Number ? _head : null;
    public override bool HasBlock(long blockNumber, Hash256 blockHash) =>
        blockNumber == _parentHeader.Number && blockHash == _parentHeader.Hash;
    public override BlockHeader FindHeader(Hash256 blockHash, BlockTreeLookupOptions options, long? blockNumber = null) =>
        blockHash == _parentHeader.Hash ? _parentHeader : null;
    public override BlockHeader FindHeader(long blockNumber, BlockTreeLookupOptions options) =>
        blockNumber == _parentHeader.Number ? _parentHeader : null;
    public override Hash256 FindBlockHash(long blockNumber) =>
        blockNumber == _parentHeader.Number ? _parentHeader.Hash : null;
    public override Hash256 FindHash(long blockNumber) =>
        blockNumber == _parentHeader.Number ? _parentHeader.Hash : null;
    public override bool IsMainChain(BlockHeader blockHeader) => blockHeader?.Hash == _parentHeader.Hash;
    public override bool IsMainChain(Hash256 blockHash, bool throwOnMissingHash = true) => blockHash == _parentHeader.Hash;
    public override bool IsKnownBlock(long number, Hash256 blockHash) =>
        number == _parentHeader.Number && blockHash == _parentHeader.Hash;
    public override bool IsKnownBeaconBlock(long number, Hash256 blockHash) =>
        number == _parentHeader.Number && blockHash == _parentHeader.Hash;
    public override bool WasProcessed(long number, Hash256 blockHash) =>
        number == _parentHeader.Number && blockHash == _parentHeader.Hash;

    public override (BlockInfo Info, ChainLevelInfo Level) GetInfo(long number, Hash256 blockHash) =>
        number == _parentHeader.Number && blockHash == _parentHeader.Hash ? (_parentInfo, null) : (null, null);
    public override BlockInfo FindCanonicalBlockInfo(long blockNumber) => _parentInfo;
    public override bool IsBetterThanHead(BlockHeader header) => _isBetterThanHead;
}
