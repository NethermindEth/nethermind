// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Consensus.Qbft.Bft;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using NSubstitute;

namespace Nethermind.Consensus.Qbft.Test;

/// <summary>A linear chain of QBFT headers behind an <see cref="IBlockTree"/> substitute: head, lookup by hash, number and block parameter.</summary>
public sealed class TestChain
{
    private readonly List<Block> _blocks = [];

    public TestChain()
    {
        BlockTree = Substitute.For<IBlockTree>();
        BlockTree.Head.Returns(_ => _blocks.Count == 0 ? null : _blocks[^1]);
        BlockTree.FindHeader(Arg.Any<Hash256>(), Arg.Any<BlockTreeLookupOptions>(), Arg.Any<ulong?>()).Returns(ci => FindByHash(ci.Arg<Hash256>()));
        BlockTree.FindHeader(Arg.Any<ulong>(), Arg.Any<BlockTreeLookupOptions>()).Returns(ci => FindByNumber(ci.Arg<ulong>()));
        BlockTree.FindHeader(Arg.Any<BlockParameter>(), Arg.Any<bool>()).Returns(ci => FindByParameter(ci.Arg<BlockParameter>()));
    }

    public IBlockTree BlockTree { get; }
    public IReadOnlyList<Block> Blocks => _blocks;
    public BlockHeader Head => _blocks[^1].Header;

    /// <summary>Appends a header proposed by <paramref name="proposer"/> carrying the given extra data; the hash is the BFT on-chain digest.</summary>
    public BftBlockHeader Add(Address proposer, BftExtraData extraData, ulong timestamp = 0, IBftExtraDataCodec? codec = null)
    {
        codec ??= QbftExtraDataCodec.Instance;
        ulong number = (ulong)_blocks.Count;
        BlockHeader header = QbftTestData.BftHeader(number, proposer, codec.Encode(extraData))
            .WithTimestamp(timestamp)
            .WithParentHash(_blocks.Count == 0 ? Keccak.Zero : _blocks[^1].Hash!)
            .TestObject;
        BftBlockHeader qbft = QbftTestData.ToQbftHeader(header, codec);
        _blocks.Add(Build.A.Block.WithHeader(qbft).TestObject);
        return qbft;
    }

    public BftBlockHeader Add(Address proposer, IReadOnlyList<Address> validators, Vote? vote = null, int round = 0, IReadOnlyList<Signature>? seals = null) =>
        Add(proposer, new BftExtraData(QbftTestData.ZeroVanity(), seals ?? [], vote, round, validators));

    private BlockHeader? FindByHash(Hash256 hash)
    {
        foreach (Block block in _blocks)
        {
            if (block.Hash == hash) return block.Header;
        }

        return null;
    }

    private BlockHeader? FindByNumber(ulong number) => number < (ulong)_blocks.Count ? _blocks[(int)number].Header : null;

    private BlockHeader? FindByParameter(BlockParameter parameter) => parameter.Type switch
    {
        BlockParameterType.BlockNumber => FindByNumber((ulong)parameter.BlockNumber!.Value),
        BlockParameterType.BlockHash => FindByHash(parameter.BlockHash!),
        BlockParameterType.Earliest => FindByNumber(0),
        _ => _blocks.Count == 0 ? null : Head,
    };
}
