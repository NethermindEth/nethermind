// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;

namespace Nethermind.Torrent.Tests;

[TestFixture]
public sealed class TrackerClientTests
{
    [Test]
    public void ParsePeers_ignores_dictionary_peers_with_invalid_ports()
    {
        BList peerList = new([
            CreatePeer("zero.example", 0),
            CreatePeer("large.example", 70000),
            CreatePeer("valid.example", 6881),
        ]);
        List<PeerEndpoint> peers = [];

        TrackerClient.ParsePeers(peerList, peers);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(peers, Has.Count.EqualTo(1));
            Assert.That(peers[0], Is.EqualTo(new PeerEndpoint("valid.example", 6881)));
        }
    }

    private static BDictionary CreatePeer(string host, long port)
        => Bencode.Dictionary(
            new KeyValuePair<string, BValue>("ip", Bencode.String(host)),
            new KeyValuePair<string, BValue>("port", Bencode.Integer(port)));
}
