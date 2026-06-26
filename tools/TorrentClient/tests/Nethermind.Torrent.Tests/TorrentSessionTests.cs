// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;

namespace Nethermind.Torrent.Tests;

[TestFixture]
public sealed class TorrentSessionTests
{
    [TestCase(0)]
    [TestCase(-1)]
    [TestCase(513)]
    public void Constructor_rejects_invalid_max_peers(int maxPeers)
    {
        TorrentClientOptions options = new()
        {
            TorrentPath = "payload.torrent",
            OutputDirectory = "downloads",
            MaxPeers = maxPeers,
        };

        Assert.That(() => new TorrentSession(options, _ => { }), Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [TestCase(0)]
    [TestCase(-1)]
    [TestCase(65536)]
    public void Constructor_rejects_invalid_listen_port(int listenPort)
    {
        TorrentClientOptions options = new()
        {
            TorrentPath = "payload.torrent",
            OutputDirectory = "downloads",
            ListenPort = listenPort,
        };

        Assert.That(() => new TorrentSession(options, _ => { }), Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public void Constructor_rejects_session_without_peer_discovery()
    {
        TorrentClientOptions options = new()
        {
            TorrentPath = "payload.torrent",
            OutputDirectory = "downloads",
            EnableDht = false,
            EnableTrackers = false,
        };

        Assert.That(() => new TorrentSession(options, _ => { }), Throws.TypeOf<ArgumentException>());
    }

    [TestCase("tracker", 0)]
    [TestCase("tracker", 3601)]
    [TestCase("dht-timeout", 0)]
    [TestCase("dht-timeout", 3601)]
    [TestCase("dht-interval", 0)]
    [TestCase("dht-interval", 3601)]
    [TestCase("peer", 0)]
    [TestCase("peer", 3601)]
    public void Constructor_rejects_invalid_timeout_options(string option, int seconds)
    {
        TorrentClientOptions options = new()
        {
            TorrentPath = "payload.torrent",
            OutputDirectory = "downloads",
        };
        TimeSpan timeout = TimeSpan.FromSeconds(seconds);
        options = option switch
        {
            "tracker" => new TorrentClientOptions
            {
                TorrentPath = options.TorrentPath,
                OutputDirectory = options.OutputDirectory,
                TrackerTimeout = timeout,
            },
            "dht-timeout" => new TorrentClientOptions
            {
                TorrentPath = options.TorrentPath,
                OutputDirectory = options.OutputDirectory,
                DhtLookupTimeout = timeout,
            },
            "dht-interval" => new TorrentClientOptions
            {
                TorrentPath = options.TorrentPath,
                OutputDirectory = options.OutputDirectory,
                DhtLookupInterval = timeout,
            },
            "peer" => new TorrentClientOptions
            {
                TorrentPath = options.TorrentPath,
                OutputDirectory = options.OutputDirectory,
                PeerTimeout = timeout,
            },
            _ => throw new ArgumentOutOfRangeException(nameof(option), option, null),
        };

        Assert.That(() => new TorrentSession(options, _ => { }), Throws.TypeOf<ArgumentOutOfRangeException>());
    }
}
