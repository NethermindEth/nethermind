// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Multiformats.Address;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Storage;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Libp2p.Core;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.P2P;

public class DataColumnSidecarsByRangeLoopbackTests
{
    private static readonly BeaconChainSpec Spec = BeaconChainSpec.Mainnet;

    [Test]
    [CancelAfter(120_000)]
    public async Task A_host_serves_by_range_columns_of_the_canonical_block_it_stores(CancellationToken token)
    {
        const ulong slot = 13_410_304;
        const ulong column = 5;
        Hash256 root = Keccak.Compute("canonical");
        DataColumnSidecarPool serverPool = new();
        serverPool.Add(root, slot, DataColumnSidecarTestFixture.BuildValidSidecar(column, slot, blobCount: 1));
        BeaconChainStore serverStore = new(new MemColumnsDb<BeaconChainDbColumns>());
        serverStore.SetCanonicalRoot(slot, root);

        await using BeaconP2P server = CreateHost(serverStore, serverPool);
        await using BeaconP2P client = CreateHost(new BeaconChainStore(new MemColumnsDb<BeaconChainDbColumns>()), new DataColumnSidecarPool());
        await server.StartAsync(token);
        await client.StartAsync(token);

        ISession toServer = await client.DialPeerAsync(LoopbackAddress(server), token);
        IReadOnlyList<DataColumnSidecar> served = await client.RequestDataColumnSidecarsByRangeAsync(toServer, slot, 1, [column], token);

        Assert.That(served.Select(static s => (s.SignedBlockHeader!.Message!.Slot, s.Index)), Is.EqualTo(new[] { (slot, column) }));
    }

    private static BeaconP2P CreateHost(BeaconChainStore store, DataColumnSidecarPool pool) =>
        new(new BeaconChainConfig { P2PPort = 0 }, Spec, store, new BeaconChainStatusHolder(Spec, Timestamper.Default), new LocalMetadataSource(), pool, new ExecutionPayloadEnvelopePool(), LimboLogs.Instance);

    private static Multiaddress LoopbackAddress(BeaconP2P node)
    {
        string address = node.ListenAddresses.First().ToString().Replace("0.0.0.0", "127.0.0.1");
        if (!address.Contains("/p2p/"))
        {
            address += $"/p2p/{node.LocalPeerId}";
        }

        return Multiaddress.Decode(address);
    }
}
