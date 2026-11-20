// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Nethermind.Api;
using Nethermind.Arkiv.Native;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Arkiv.Test;

/// <summary>
/// Real Rabdi data: the network's genesis file and blocks 1–700 as raw RLP (exported with <c>debug_getRawBlock</c> from
/// a Nethermind node that had validated them against their headers), with facts the public RPC reports about them.
/// </summary>
public static class RabdiFixture
{
    public const ulong ChainId = 7794095;
    public static readonly Hash256 GenesisHash = new("0xa65a2b9e25129f0e8ccdf494dfd66ec744bdf400c806b832b1da86b0b939cbce");
    public static readonly Hash256 GenesisRoot = new("0x18f6f20576ca53378a20d9bf891eae550cdfdbc1473b05fe104e48f61720a9ec");

    /// <summary>The purge sender: every Rabdi block carries one protocol <c>purgeExpired</c> from it.</summary>
    public static readonly Address PurgeCaller = new("0x5ff20d1c9fea02c505a2c18f6d42feaeaf44c0af");

    private static readonly string Directory = Path.Combine(AppContext.BaseDirectory, "TestData");

    public static string GenesisFile => Path.Combine(AppContext.BaseDirectory, "Chains", "rabdi.json");

    /// <summary>A block tree that knows <paramref name="blocks"/> (which a test may add to) and block 0.</summary>
    public static IBlockTree BlockTreeOver(Dictionary<Hash256, Block> blocks)
    {
        BlockHeader genesis = new(Keccak.Zero, Keccak.OfAnEmptySequenceRlp, Address.Zero, 0, 0, 0, 0, []) { Hash = GenesisHash, Number = 0 };
        IBlockTree tree = Substitute.For<IBlockTree>();
        tree.FindHeader(Arg.Any<Hash256>(), Arg.Any<BlockTreeLookupOptions>(), Arg.Any<ulong?>())
            .Returns(c => blocks.TryGetValue(c.ArgAt<Hash256>(0), out Block? b) ? b.Header : c.ArgAt<Hash256>(0) == GenesisHash ? genesis : null);
        tree.FindBlock(Arg.Any<Hash256>(), Arg.Any<BlockTreeLookupOptions>(), Arg.Any<ulong?>())
            .Returns(c => blocks.GetValueOrDefault(c.ArgAt<Hash256>(0)));
        return tree;
    }

    /// <summary>
    /// Opens an <see cref="ArkivChain"/> on Rabdi's genesis over a store in <paramref name="baseDbPath"/>; the test is
    /// skipped when the native library was not built (<c>-p:BuildArkivNative=true</c>), as Nethermind builds without Rust,
    /// and fails instead when <c>ARKIV_REQUIRE_NATIVE</c> is set (the Arkiv CI workflow). Blocks commit as they execute
    /// unless <paramref name="holdHeadBlock"/> is set, as it is by default on a node.
    /// </summary>
    public static ArkivChain OpenChain(string baseDbPath, IDb db, IBlockTree blockTree, ulong checkpointInterval = 0, bool holdHeadBlock = false)
    {
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "libarkiv_ffi.so")))
        {
            const string Missing = "libarkiv_ffi.so was not built: build with -p:BuildArkivNative=true to run the Arkiv tests";
            if (Environment.GetEnvironmentVariable("ARKIV_REQUIRE_NATIVE") is not null) Assert.Fail(Missing);
            Assert.Ignore(Missing);
        }
        ArkivConfig config = new()
        {
            Enabled = true,
            GenesisFile = GenesisFile,
            StoreDirectory = Path.Combine(baseDbPath, "store"),
            CheckpointInterval = checkpointInterval,
            HoldHeadBlock = holdHeadBlock,
        };
        return new ArkivChain(config, new InitConfig { BaseDbPath = baseDbPath }, db, new Lazy<IBlockTree>(blockTree), new EthereumEcdsa(ChainId), LimboLogs.Instance);
    }

    private static readonly Lazy<Block[]> _blocks = new(LoadBlocks);

    /// <summary>Blocks 1–700, index <c>n - 1</c> for block <c>n</c>, with hashes and senders set.</summary>
    public static IReadOnlyList<Block> Blocks => _blocks.Value;

    private static Block[] LoadBlocks()
    {
        using FileStream file = File.OpenRead(Path.Combine(Directory, "rabdi-blocks-1-700.rlp.hex.gz"));
        using GZipStream gzip = new(file, CompressionMode.Decompress);
        using StreamReader reader = new(gzip);
        EthereumEcdsa ecdsa = new(ChainId);
        List<Block> blocks = [];
        while (reader.ReadLine() is { Length: > 0 } line)
        {
            Block block = Rlp.Decode<Block>(Bytes.FromHexString(line)) ?? throw new InvalidDataException("not a block");
            block.Header.Hash = block.Header.CalculateHash();
            foreach (Transaction tx in block.Transactions)
            {
                tx.SenderAddress = ecdsa.RecoverAddress(tx);
                tx.Hash = tx.CalculateHash();
            }
            blocks.Add(block);
        }
        return [.. blocks];
    }

    /// <summary>A sibling of <paramref name="block"/>: the same contents under different extra data, so a different hash with the same state.</summary>
    public static Block Sibling(Block block)
    {
        BlockHeader header = block.Header.Clone();
        header.ExtraData = [.. header.ExtraData, 0x01];
        header.Hash = header.CalculateHash();
        return block.WithReplacedHeader(header);
    }

    /// <summary><paramref name="block"/> re-parented on <paramref name="parent"/> and re-hashed.</summary>
    public static Block OnParent(Block block, Block parent)
    {
        BlockHeader header = block.Header.Clone();
        header.ParentHash = parent.Hash;
        header.Hash = header.CalculateHash();
        return block.WithReplacedHeader(header);
    }

    public static readonly Address ArkivAddress = new("0x4400000000000000000000000000000000000044");

    /// <summary>Blocks with a user call to the entity engine (<c>execute(Operation[])</c>, selector 0x49650044).</summary>
    public static IEnumerable<int> EntityBlockIndexes => Enumerable.Range(0, 700).Where(i =>
        Blocks[i].Transactions.Any(tx => tx.To == ArkivAddress && tx.Data.Span.StartsWith((ReadOnlySpan<byte>)[0x49, 0x65, 0x00, 0x44])));
}
