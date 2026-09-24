// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Multiformats.Address;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.ReqResp;
using Nethermind.BeaconChain.P2P.ReqResp.Protocols;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Libp2p.Core;
using Nethermind.Logging;
using NUnit.Framework;
using static Nethermind.BeaconChain.Test.Types.SignedBeaconBlockBuilders;

namespace Nethermind.BeaconChain.Test.P2P;

/// <summary>
/// <c>beacon_blocks_by_range</c> and <c>beacon_blocks_by_root</c> v2 carry the fork digest of each block's
/// slot epoch as context bytes, and from Gloas that digest selects <c>gloas.SignedBeaconBlock</c>
/// (gloas/p2p-interface.md). Decoding every chunk as Fulu would fail each Gloas block and penalize every
/// peer that serves one, so range sync could never cross the fork.
/// </summary>
public class BlocksProtocolForkDecodeTests
{
    private static readonly ForkedSignedBeaconBlock LastFuluBlock = new ForkedSignedBeaconBlock.OfFulu(CreateMinimalBlock(FirstGloasSlot - 1));
    private static readonly ForkedSignedBeaconBlock FirstGloasBlock = new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(FirstGloasSlot));

    [Test]
    public async Task Response_spanning_the_fork_decodes_each_chunk_as_the_shape_its_context_names()
    {
        TestBlocksProtocol protocol = new(Sepolia);
        using MemoryStream stream = new();
        using CancellationTokenSource cts = new();
        await protocol.WriteBlockAsync(stream, LastFuluBlock, cts);
        await protocol.WriteBlockAsync(stream, FirstGloasBlock, cts);
        stream.Position = 0;

        IReadOnlyList<ForkedSignedBeaconBlock> blocks = await protocol.ReadBlocksAsync(stream, maxBlocks: 2);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(blocks.Select(static b => b.GetType()), Is.EqualTo(new[] { typeof(ForkedSignedBeaconBlock.OfFulu), typeof(ForkedSignedBeaconBlock.OfGloas) }), "one shape per fork");
            Assert.That(blocks.Select(static b => b.ComputeMessageRoot()), Is.EqualTo(new[] { LastFuluBlock.ComputeMessageRoot(), FirstGloasBlock.ComputeMessageRoot() }), "block roots round-trip");
        }
    }

    [Test]
    public async Task Chunk_whose_context_names_the_other_fork_is_rejected([Values] bool gloasBlock)
    {
        ForkedSignedBeaconBlock block = gloasBlock ? FirstGloasBlock : LastFuluBlock;
        ulong otherForkEpoch = gloasBlock ? Sepolia.GloasForkEpoch - 1 : Sepolia.GloasForkEpoch;
        using MemoryStream stream = new();
        await ReqRespFraming.WriteResponseChunkAsync(
            stream, ReqRespFraming.ResponseCode.Success, ForkDigest.Compute(Sepolia, otherForkEpoch), SignedBeaconBlockCodec.Encode(block, Sepolia), default);
        stream.Position = 0;

        Eth2ReqRespException thrown = Assert.ThrowsAsync<Eth2ReqRespException>(() => new TestBlocksProtocol(Sepolia).ReadBlocksAsync(stream, maxBlocks: 1))!;

        Assert.That(thrown.Message, Does.Contain("context bytes"));
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task Store_serves_blocks_of_both_forks_by_range_and_by_root(CancellationToken token)
    {
        Hash256 fuluRoot = LastFuluBlock.ComputeMessageRoot();
        Hash256 gloasRoot = FirstGloasBlock.ComputeMessageRoot();
        BeaconChainStore serverStore = new(new MemColumnsDb<BeaconChainDbColumns>(), Sepolia);
        serverStore.PutForkedBlock(fuluRoot, LastFuluBlock);
        serverStore.PutForkedBlock(gloasRoot, FirstGloasBlock);
        serverStore.SetCanonicalRoot(LastFuluBlock.Slot, fuluRoot);
        serverStore.SetCanonicalRoot(FirstGloasBlock.Slot, gloasRoot);
        serverStore.SetAnchor(fuluRoot, LastFuluBlock.Slot);

        await using BeaconP2P server = CreateNode(serverStore);
        await using BeaconP2P client = CreateNode(new BeaconChainStore(new MemColumnsDb<BeaconChainDbColumns>(), Sepolia));
        await server.StartAsync(token);
        await client.StartAsync(token);
        ISession toServer = await client.DialPeerAsync(LoopbackAddress(server), token);

        IReadOnlyList<ForkedSignedBeaconBlock> byRange = await client.RequestBlocksByRangeAsync(toServer, LastFuluBlock.Slot, 2, token);
        IReadOnlyList<ForkedSignedBeaconBlock> byRoot = await client.RequestBlocksByRootAsync(toServer, [gloasRoot], token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(byRange.Select(static b => b.ComputeMessageRoot()), Is.EqualTo(new[] { fuluRoot, gloasRoot }), "by range crosses the fork");
            Assert.That(byRange[^1], Is.InstanceOf<ForkedSignedBeaconBlock.OfGloas>(), "by range keeps the Gloas shape");
            Assert.That(byRoot.Select(static b => b.ComputeMessageRoot()), Is.EqualTo(new[] { gloasRoot }), "by root serves the Gloas block");
            Assert.That(byRoot[0], Is.InstanceOf<ForkedSignedBeaconBlock.OfGloas>(), "by root keeps the Gloas shape");
        }
    }

    private static BeaconP2P CreateNode(BeaconChainStore store) =>
        new(new BeaconChainConfig { P2PPort = 0 }, Sepolia, store, new BeaconChainStatusHolder(Sepolia, Timestamper.Default), new LocalMetadataSource(),
            new DataColumnSidecarPool(), new ExecutionPayloadEnvelopePool(), LimboLogs.Instance);

    private static Multiaddress LoopbackAddress(BeaconP2P node)
    {
        string address = node.ListenAddresses.First().ToString().Replace("0.0.0.0", "127.0.0.1");
        if (!address.Contains("/p2p/"))
        {
            address += $"/p2p/{node.LocalPeerId}";
        }

        return Multiaddress.Decode(address);
    }

    private sealed class TestBlocksProtocol(BeaconChainSpec spec) : BlocksProtocolBase(spec)
    {
        private const string ProtocolId = "/test/blocks-fork-decode/1";

        public Task<IReadOnlyList<ForkedSignedBeaconBlock>> ReadBlocksAsync(Stream stream, int maxBlocks) =>
            ReadBlockChunksAsync(stream, maxBlocks, ProtocolId);

        public Task WriteBlockAsync(Stream stream, ForkedSignedBeaconBlock block, CancellationTokenSource cts) =>
            WriteBlockChunkAsync(stream, block, cts);
    }
}
