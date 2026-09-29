// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;
using System.Net;

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

    [Test]
    public async Task AnnounceAsync_discovers_peers_using_infohash_without_torrent_metadata()
    {
        byte[] peers = [127, 0, 0, 1, 0x1a, 0xe1];
        byte[] response = Bencode.Encode(Bencode.Dictionary(
            new KeyValuePair<string, BValue>("interval", Bencode.Integer(60)),
            new KeyValuePair<string, BValue>("peers", Bencode.Bytes(peers))));
        using RecordingHandler handler = new(response);
        using HttpClient http = new(handler);
        TrackerClient tracker = new(http, _ => { }, TimeSpan.FromSeconds(1));

        TrackerAnnounceResult result = await tracker.AnnounceAsync(
            [new Uri("https://tracker.example/announce")], new byte[20], 1, new byte[20], "00000000", 6881, 0, 0, CancellationToken.None, stopOnPeers: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Peers, Is.EqualTo(new[] { new PeerEndpoint("127.0.0.1", 6881) }));
            Assert.That(handler.RequestUri?.Query, Does.Contain("info_hash="));
            Assert.That(handler.RequestUri?.Query, Does.Contain("left=1"));
        }
    }

    [Test]
    public async Task AnnounceAsync_collects_peers_from_multiple_magnet_trackers()
    {
        byte[] first = Bencode.Encode(Bencode.Dictionary(new KeyValuePair<string, BValue>("peers", Bencode.Bytes([127, 0, 0, 1, 0x1a, 0xe1]))));
        byte[] second = Bencode.Encode(Bencode.Dictionary(new KeyValuePair<string, BValue>("peers", Bencode.Bytes([127, 0, 0, 2, 0x1a, 0xe2]))));
        using RecordingHandler handler = new(first, second);
        using HttpClient http = new(handler);
        TrackerClient tracker = new(http, _ => { }, TimeSpan.FromSeconds(1));

        TrackerAnnounceResult result = await tracker.AnnounceAsync(
            [new Uri("https://one.example/announce"), new Uri("https://two.example/announce")],
            new byte[20], 1, new byte[20], "00000000", 6881, 0, 0, CancellationToken.None, stopOnPeers: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(handler.RequestCount, Is.EqualTo(2));
            Assert.That(result.Peers, Is.EquivalentTo(new[] { new PeerEndpoint("127.0.0.1", 6881), new PeerEndpoint("127.0.0.2", 6882) }));
        }
    }

    [Test]
    public async Task AnnounceAsync_keeps_payload_tracker_fallback_beyond_magnet_limit()
    {
        byte[] empty = Bencode.Encode(Bencode.Dictionary(new KeyValuePair<string, BValue>("peers", Bencode.Bytes([]))));
        byte[] found = Bencode.Encode(Bencode.Dictionary(new KeyValuePair<string, BValue>("peers", Bencode.Bytes([127, 0, 0, 1, 0x1a, 0xe1]))));
        byte[][] responses = Enumerable.Repeat(empty, 64).Append(found).ToArray();
        Uri[] trackers = Enumerable.Range(0, 65).Select(i => new Uri($"https://tracker-{i}.example/announce")).ToArray();
        using RecordingHandler handler = new(responses);
        using HttpClient http = new(handler);
        TrackerClient tracker = new(http, _ => { }, TimeSpan.FromSeconds(1));

        TrackerAnnounceResult result = await tracker.AnnounceAsync(trackers, new byte[20], 1, new byte[20],
            "00000000", 6881, 0, 0, CancellationToken.None, stopOnPeers: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(handler.RequestCount, Is.EqualTo(65));
            Assert.That(result.Peers, Is.EqualTo(new[] { new PeerEndpoint("127.0.0.1", 6881) }));
        }
    }

    [Test]
    public async Task AnnounceAsync_keeps_found_peers_when_later_tracker_exceeds_budget()
    {
        byte[] first = Bencode.Encode(Bencode.Dictionary(new KeyValuePair<string, BValue>("peers", Bencode.Bytes([127, 0, 0, 1, 0x1a, 0xe1]))));
        using StallingHandler handler = new(first);
        using HttpClient http = new(handler);
        using CancellationTokenSource budget = new(TimeSpan.FromMilliseconds(300));
        TrackerClient tracker = new(http, _ => { }, TimeSpan.FromSeconds(1));

        TrackerAnnounceResult result = await tracker.AnnounceAsync(
            [new Uri("https://one.example/announce"), new Uri("https://two.example/announce")],
            new byte[20], 1, new byte[20], "00000000", 6881, 0, 0, budget.Token, stopOnPeers: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(handler.RequestCount, Is.EqualTo(2));
            Assert.That(result.Peers, Is.EqualTo(new[] { new PeerEndpoint("127.0.0.1", 6881) }));
        }
    }

    [Test]
    public async Task AnnounceAsync_caps_oversized_tracker_peer_list()
    {
        byte[] compact = new byte[6 * 1000];
        for (int i = 0; i < 1000; i++)
        {
            compact[i * 6] = 10;
            compact[i * 6 + 2] = (byte)(i / 256);
            compact[i * 6 + 3] = (byte)i;
            compact[i * 6 + 4] = 0x1a;
            compact[i * 6 + 5] = 0xe1;
        }

        byte[] response = Bencode.Encode(Bencode.Dictionary(new KeyValuePair<string, BValue>("peers", Bencode.Bytes(compact))));
        using RecordingHandler handler = new(response);
        using HttpClient http = new(handler);
        TrackerClient tracker = new(http, _ => { }, TimeSpan.FromSeconds(1));

        TrackerAnnounceResult result = await tracker.AnnounceAsync(
            [new Uri("https://tracker.example/announce")], new byte[20], 1, new byte[20], "00000000",
            6881, 0, 0, CancellationToken.None, stopOnPeers: false);

        Assert.That(result.Peers, Has.Count.EqualTo(256));
    }

    [Test]
    public async Task AnnounceEventAsync_stops_trackers_started_for_metadata_discovery()
    {
        byte[] response = Bencode.Encode(Bencode.Dictionary(new KeyValuePair<string, BValue>("peers", Bencode.Bytes(Array.Empty<byte>()))));
        using RecordingHandler handler = new(response, response);
        using HttpClient http = new(handler);
        TrackerClient tracker = new(http, _ => { }, TimeSpan.FromSeconds(1));
        byte[] infoHash = new byte[20];
        byte[] peerId = new byte[20];

        await tracker.AnnounceAsync([new Uri("https://tracker.example/announce")], infoHash, 1, peerId,
            "00000000", 6881, 0, 0, CancellationToken.None, stopOnPeers: false);
        await tracker.AnnounceEventAsync(infoHash, 1, peerId, "00000000", 6881,
            0, 0, "stopped", CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(handler.RequestCount, Is.EqualTo(2));
            Assert.That(handler.RequestUri?.Query, Does.Contain("event=stopped"));
        }
    }

    [Test]
    public async Task AnnounceEventAsync_contacts_ninth_tracker_when_eight_stops_stall()
    {
        using StalledStopHandler handler = new();
        using HttpClient http = new(handler);
        TrackerClient tracker = new(http, _ => { }, TimeSpan.FromSeconds(1));
        byte[] infoHash = new byte[20];
        byte[] peerId = new byte[20];
        Uri[] trackers = [.. Enumerable.Range(0, 8).Select(i => new Uri($"https://stalled-{i}.example/announce")),
            new Uri("https://ninth.example/announce")];
        await tracker.AnnounceAsync(trackers, infoHash, 1, peerId, "00000000", 6881, 0, 0,
            CancellationToken.None, stopOnPeers: false);
        using CancellationTokenSource timeout = new(TimeSpan.FromMilliseconds(300));

        Assert.That(async () => await tracker.AnnounceEventAsync(infoHash, 1, peerId, "00000000", 6881,
            0, 0, "stopped", timeout.Token, concurrent: true), Throws.InstanceOf<OperationCanceledException>());
        Assert.That(handler.NinthStopCount, Is.EqualTo(1));
    }

    private sealed class StallingHandler(byte[] firstResponse) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (RequestCount++ == 0)
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(firstResponse) };
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The tracker should have been canceled.");
        }
    }

    private sealed class RecordingHandler(params byte[][] responses) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            byte[] response = responses[RequestCount++];
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(response) });
        }
    }

    private sealed class StalledStopHandler : HttpMessageHandler
    {
        private int _ninthStopCount;

        public int NinthStopCount => Volatile.Read(ref _ninthStopCount);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Query.Contains("event=stopped", StringComparison.Ordinal))
            {
                if (request.RequestUri.Host != "ninth.example")
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }

                Interlocked.Increment(ref _ninthStopCount);
            }

            byte[] response = Bencode.Encode(Bencode.Dictionary(new KeyValuePair<string, BValue>("peers", Bencode.Bytes([]))));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(response) };
        }
    }

    private static BDictionary CreatePeer(string host, long port)
        => Bencode.Dictionary(
            new KeyValuePair<string, BValue>("ip", Bencode.String(host)),
            new KeyValuePair<string, BValue>("port", Bencode.Integer(port)));
}
