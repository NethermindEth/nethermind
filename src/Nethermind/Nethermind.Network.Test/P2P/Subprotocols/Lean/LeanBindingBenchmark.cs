// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus.ProofAggregation;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Subprotocols.Lean;
using Nethermind.Network.P2P.Subprotocols.Lean.Ethp2p;
using NUnit.Framework;
using static Nethermind.Network.Test.P2P.Subprotocols.Lean.LeanTestObjects;

namespace Nethermind.Network.Test.P2P.Subprotocols.Lean;

/// <summary>Runs one object-transfer workload between two in-process nodes over a chosen EIP-8437 binding.</summary>
/// <remarks>
/// <para>
/// Settings come from environment variables: <c>LEAN_BENCH_KIND</c> (1 or 3, default 1), <c>LEAN_BENCH_OBJECTS</c> (default 20, at
/// most 250, 48 MiB of proofs and, when broadcasting, 64 sessions), <c>LEAN_BENCH_BYTES</c> (proof bytes per object, default 1 MiB less 4 KiB) and <c>LEAN_BENCH_CONCURRENCY</c> (default 4).
/// The <c>ethp2p-broadcast</c> case always uses kind 3 and also announces each object, so retrieval and broadcast race.
/// </para>
/// <para>
/// Latency runs from the sender's validation to the receiver storing the validated object. RLPx bytes are encoded <c>lean/1</c>
/// message data before Snappy and RLPx framing, over an in-process wire; ethp2p bytes are QUIC stream data over loopback, before
/// QUIC, TLS and UDP overhead. CPU is the test process's, covering both nodes. Wire-level comparisons need two real nodes.
/// </para>
/// </remarks>
[Explicit("Benchmark")]
[NonParallelizable]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
public class LeanBindingBenchmark
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);
    private const long MaxProofBytes = 48 * 1024 * 1024;

    private static int Setting(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out int value) && value > 0 ? value : fallback;

    private sealed class Pair(LeanTestNode a, LeanTestNode b, Func<long> bytes, Func<ValueTask> dispose, LeanEthp2pHost? broadcaster,
        TestBroadcastProfile? profile) : IAsyncDisposable
    {
        public LeanTestNode A { get; } = a;
        public LeanTestNode B { get; } = b;
        public long Bytes => bytes();
        public LeanEthp2pHost? Broadcaster { get; } = broadcaster;
        public TestBroadcastProfile? Profile { get; } = profile;
        public ValueTask DisposeAsync() => dispose();
    }

    private static async Task<Pair> Open(string binding)
    {
        if (binding == "rlpx")
        {
            Lean1ProtocolHandlerTests.Connection connection = new();
            await connection.Open();
            return new Pair(connection.A.Node, connection.B.Node, () => connection.Bytes, connection.DisposeAsync, null, null);
        }
        if (!LeanEthp2pHost.IsSupported) Assert.Ignore("QUIC is unavailable on this platform (libmsquic is not installed)");
        TestBroadcastProfile? profile = binding == "ethp2p-broadcast" ? new TestBroadcastProfile() : null;
        (LeanEthp2pQuicTests.QuicNode a, LeanEthp2pQuicTests.QuicNode b) = await LeanEthp2pQuicTests.Connected(profile);
        return new Pair(a.Node, b.Node, SentStreamBytes, async () =>
        {
            await a.DisposeAsync();
            await b.DisposeAsync();
        }, a.Host, profile);
    }

    /// <summary>Stream bytes sent by both in-process nodes, which is every stream byte between them.</summary>
    private static long SentStreamBytes() => Metrics.LeanEthp2pStreamBytes.Where(static e => e.Key.Direction == "out").Sum(static e => e.Value);

    [TestCase("rlpx")]
    [TestCase("ethp2p")]
    [TestCase("ethp2p-broadcast")]
    public async Task Transfer(string binding)
    {
        bool broadcast = binding == "ethp2p-broadcast";
        byte kind = broadcast ? LeanProtocol.KindInclusionList : (byte)Setting("LEAN_BENCH_KIND", LeanProtocol.KindWrapper);
        int proofBytes = Setting("LEAN_BENCH_BYTES", LeanReedSolomon.MaxBodyBytes - 4096);
        if (broadcast) proofBytes = Math.Min(proofBytes, LeanReedSolomon.MaxBodyBytes - 4096);
        // Unmined proofs stay in each node's 64 MiB proof store, and distinct test transactions are limited.
        int count = Math.Min(Setting("LEAN_BENCH_OBJECTS", 20), (int)Math.Min(250, MaxProofBytes / proofBytes));
        // Each broadcast takes its own duty's allowance and session until the duty expires.
        if (broadcast) count = Math.Min(count, LeanBroadcastEngine.MaxSessions);
        int concurrency = Setting("LEAN_BENCH_CONCURRENCY", 4);
        Assert.That(kind, Is.EqualTo(LeanProtocol.KindWrapper).Or.EqualTo(LeanProtocol.KindInclusionList), "LEAN_BENCH_KIND");

        await using Pair pair = await Open(binding);
        long bytesBefore = pair.Bytes;
        TimeSpan cpuBefore = Process.GetCurrentProcess().TotalProcessorTime;
        long started = Stopwatch.GetTimestamp();
        using SemaphoreSlim slots = new(concurrency);
        using CancellationTokenSource deadline = new(Timeout);
        long bodyBytes = 0;
        double[] latencies = await Task.WhenAll(Enumerable.Range(0, count).Select(async i =>
        {
            await slots.WaitAsync(deadline.Token);
            try
            {
                Transaction transaction = FrameTransaction(i);
                byte[] body = kind == LeanProtocol.KindWrapper ? Wrapper(proofBytes, false, transaction) : InclusionList(proofBytes, transaction);
                Interlocked.Add(ref bodyBytes, body.Length);
                LeanDescriptor descriptor = kind == LeanProtocol.KindWrapper ? Describe(body, out _)
                    : LeanDescriptor.Create(kind, LeanObjectTransport.LocalProfile, LeanDescriptor.InclusionListContext(ValueKeccak.Compute(body)), body, out _);
                ProofWrapperAcceptance acceptance;
                // Proof admission takes one object at a time and reports Busy to the others.
                while ((acceptance = kind == LeanProtocol.KindWrapper
                           ? await pair.A.Wrappers.AcceptDetailedAsync(body)
                           : await pair.A.Wrappers.AcceptInclusionListDetailedAsync(body)).Status == ProofWrapperAcceptanceStatus.Busy)
                    await Task.Delay(1, deadline.Token);
                Assert.That(acceptance.HasValidProof, Is.True, "the sender validates its object");
                long sent = Stopwatch.GetTimestamp();
                if (broadcast) await Originate(pair, descriptor, body, (ulong)i, deadline.Token);
                while (!pair.B.Transport.IsStored(descriptor.ObjectId))
                {
                    if (deadline.IsCancellationRequested)
                        Assert.Fail($"object {i} never reached the receiver: {pair.B.Transport.AssemblyCount} assemblies, " +
                            $"{pair.B.Transport.IncompleteBytes} incomplete bytes, {pair.B.Transport.StoredObjects} stored; " +
                            $"{pair.A.Transport.PeerCount} and {pair.B.Transport.PeerCount} peers");
                    await Task.Delay(1, CancellationToken.None);
                }
                return Stopwatch.GetElapsedTime(sent).TotalMilliseconds;
            }
            finally
            {
                slots.Release();
            }
        }));
        TimeSpan elapsed = Stopwatch.GetElapsedTime(started);
        double cpu = (Process.GetCurrentProcess().TotalProcessorTime - cpuBefore).TotalMilliseconds;
        long transferred = pair.Bytes - bytesBefore;

        Array.Sort(latencies);
        string line = $"lean bench binding={binding} kind={kind} objects={count} body={bodyBytes / count} B concurrency={concurrency}: " +
            $"latency p50={Percentile(latencies, 0.5):F1} ms p95={Percentile(latencies, 0.95):F1} ms max={latencies[^1]:F1} ms, " +
            $"wall {elapsed.TotalSeconds:F2} s, {transferred / count} B/object ({(double)transferred / bodyBytes - 1:P2} over body), " +
            $"cpu {cpu:F0} ms ({cpu / count:F1} ms/object), coding {Interlocked.Read(ref Metrics.LeanBroadcastCodingMicros) / 1000.0:F0} ms cumulative";
        TestContext.Out.WriteLine(line);
        Console.WriteLine(line);
    }

    private static async Task Originate(Pair pair, LeanDescriptor descriptor, byte[] body, ulong duty, CancellationToken token)
    {
        // A node broadcasts only objects it has stored, which publication does in the background after validation.
        while (!pair.A.Transport.IsStored(descriptor.ObjectId)) await Task.Delay(1, token);
        LeanBroadcastManifest manifest = LeanBroadcastManifest.Create(TestBroadcastProfile.ProfileId, [1, 2, 3, 4], pair.Profile!.CurrentSlot, duty, 7,
            default, descriptor, null, body, out _);
        Assert.That(pair.Broadcaster!.Broadcast!.Originate(manifest, TestBroadcastProfile.Sign(manifest), []), Is.True, "the broadcast starts");
    }

    private static double Percentile(IReadOnlyList<double> sorted, double quantile) => sorted[(int)Math.Min(sorted.Count - 1, Math.Ceiling(quantile * sorted.Count) - 1)];
}
