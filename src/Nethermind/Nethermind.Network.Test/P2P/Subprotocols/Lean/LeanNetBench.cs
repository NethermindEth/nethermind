// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DotNetty.Common;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Network.Test.Builders;
using Nethermind.Network.P2P.Subprotocols.Eth.V66;
using Nethermind.Synchronization;
using Nethermind.Config;
using Nethermind.Consensus.ProofAggregation;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Metric;
using Nethermind.Core.Test.Modules;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Analyzers;
using Nethermind.Network.P2P.EventArg;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.P2P.Subprotocols.Lean;
using Nethermind.Network.P2P.Subprotocols.Lean.Ethp2p;
using Nethermind.Network.Rlpx;
using Nethermind.Network.Rlpx.Handshake;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.Peers;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;
using static Nethermind.Network.Test.P2P.Subprotocols.Lean.LeanTestObjects;

namespace Nethermind.Network.Test.P2P.Subprotocols.Lean;

/// <summary>One node of a multi-process EIP-8437 network benchmark, driven by <c>scripts/lean-net-bench</c>.</summary>
/// <remarks>
/// <para>
/// Each node runs in its own process (and, on Linux, its own network namespace) over a real binding: RLPx over TCP with the
/// production handshake, framing and Snappy, or ethp2p over QUIC. The configuration file named by <c>LEAN_NET_CONFIG</c>
/// sets the node's address, its peers and the workload. Every node derives the same objects; each object has one origin,
/// is released at a fixed offset from the shared start time and is relayed by the normal transport (announce, then
/// GetObjects/GetChunks hop by hop) and, with broadcast, by authenticated Reed-Solomon broadcast under a test profile.
/// </para>
/// <para>
/// Coordination is through files in the run directory: the node writes <c>ready.N</c> once all its peers passed Status,
/// waits for <c>start</c> (a Unix nanosecond timestamp), writes <c>done.N</c> when it holds every object, keeps
/// serving until <c>stop</c>, then writes <c>result.N.json</c> with per-object timestamps and resource counters.
/// </para>
/// </remarks>
[Explicit("Multi-process network benchmark node; run through scripts/lean-net-bench")]
[NonParallelizable]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
public class LeanNetBenchNode
{
    /// <summary>Uses hash-indexed lookup for the benchmark's controlled pool.</summary>
    internal static void ConfigureIndexedPool(LeanTestNode node)
    {
        ConcurrentDictionary<ValueHash256, Transaction> pending = new();
        node.Pool.GetPendingTransactions().Returns(_ =>
        {
            List<Transaction> snapshot = new(pending.Count);
            foreach (KeyValuePair<ValueHash256, Transaction> entry in pending) snapshot.Add(entry.Value);
            return snapshot.ToArray();
        });
        node.Pool.TryGetPendingTransaction(Arg.Any<ValueHash256>(), out Arg.Any<Transaction?>()).Returns(call =>
        {
            bool found = pending.TryGetValue(call.Arg<ValueHash256>(), out Transaction? transaction);
            call[1] = transaction;
            return found;
        });
        node.Pool.SubmitTx(Arg.Any<Transaction>(), Arg.Any<TxHandlingOptions>()).Returns(call =>
        {
            if (node.Reject) return AcceptTxResult.Invalid;
            Transaction transaction = call.Arg<Transaction>();
            pending.TryAdd(transaction.Hash!.ValueHash256, transaction);
            return AcceptTxResult.Accepted;
        });
    }

    public sealed record PeerConfig(int Id, string Ip, int Port);

    public sealed record NodeConfig(int Id, string Binding, bool Broadcast, string Ip, int Port, PeerConfig[] Peers, int Nodes, int Kind,
        int Objects, int Warmup, int ProofBytes, int TransactionsPerObject, int Burst, int IntervalMs, string Origins, int VerifyMs, long GenesisUnixMs, string RunDir, int TimeoutSec);

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = false };

    internal static PrivateKey NodeKey(int id) => new(Keccak.Compute($"lean-net-bench node {id}").Bytes.ToArray());

    private static long Now() => (long)(Stopwatch.GetTimestamp() * (1_000_000_000.0 / Stopwatch.Frequency));

    private static long UnixNow() => (DateTime.UtcNow - DateTime.UnixEpoch).Ticks * 100;

    [Test]
    public async Task Run()
    {
        string? path = Environment.GetEnvironmentVariable("LEAN_NET_CONFIG");
        if (path is null) Assert.Ignore("LEAN_NET_CONFIG is not set");
        NodeConfig config = JsonSerializer.Deserialize<NodeConfig>(File.ReadAllText(path), Json)!;
        Metrics.LeanObjectTransferMicros = TransferCounter.Instance;
        // As in release builds of the runner: leak sampling captures stack traces of pooled buffers, which would dominate RLPx CPU.
        ResourceLeakDetector.Level = ResourceLeakDetector.DetectionLevel.Disabled;
        ILogManager logs = Environment.GetEnvironmentVariable("LEAN_NET_LOG") switch
        {
            null => LimboLogs.Instance,
            { } filter when filter.StartsWith("filter:") => new FilteredLogManager(filter["filter:".Length..]),
            { } level => new TestLogManager(Enum.Parse<LogLevel>(level, true))
        };
        string Path_(string name) => Path.Combine(config.RunDir, name);
        void Log(string message) => Console.WriteLine($"[node {config.Id} {DateTime.UtcNow:HH:mm:ss.fff}] {message}");

        using LeanTestNode node = new(manualTime: false, logManager: logs);
        ConfigureIndexedPool(node);
        node.Verifier.Delay = TimeSpan.FromMilliseconds(config.VerifyMs);
        ConcurrentDictionary<Hash256, BlockHeader> imported = new();
        node.Tree.FindHeader(Arg.Any<Hash256>(), Arg.Any<BlockTreeLookupOptions>(), Arg.Any<ulong?>())
            .Returns(call => imported.TryGetValue(call.Arg<Hash256>(), out BlockHeader? header) ? header : null);
        node.Tree.IsMainChain(Arg.Any<BlockHeader>()).Returns(true);
        Lock importLock = new();
        void Import(BenchObject o)
        {
            imported[o.Header!.Hash!] = o.Header;
            // Block import with the proof attached: the transport stores and announces the sidecar.
            lock (importLock) node.Tree.NewHeadBlock += Raise.EventWith(new BlockEventArgs(new Block(o.Header, new BlockBody(o.Transactions, [], null, o.Proof))));
        }

        BenchObject[] objects = [.. Enumerable.Range(0, config.Objects).Select(i => BenchObject.Create(config.Kind, i, config.ProofBytes, config.TransactionsPerObject))];
        foreach (BenchObject obj in objects)
            if (obj.Header is { } header) imported[header.Hash!] = header;
        int Origin(int i) => config.Origins == "all" ? i % config.Nodes : int.Parse(config.Origins);
        BenchBroadcastProfile? profile = config.Broadcast ? new BenchBroadcastProfile(config.GenesisUnixMs) : null;
        PrivateKey key = NodeKey(config.Id);
        PeerConfig[] dialed = [.. config.Peers.Where(p => p.Id > config.Id)];

        await using IBenchBinding binding = config.Binding switch
        {
            "rlpx" => await RlpxBinding.StartAsync(key, config.Ip, config.Port, node, logs),
            "ethp2p" => await Ethp2pBinding.StartAsync(key, config.Ip, config.Port, node, profile, logs),
            _ => throw new ArgumentException($"unknown binding {config.Binding}")
        };
        Log($"{config.Binding} listening on {config.Ip}:{config.Port}, {config.Peers.Length} peers");

        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(config.TimeoutSec));
        long lastDial = 0;
        while (node.Transport.PeerCount < config.Peers.Length || config.Peers.Any(p => !node.EthTransactions.HasPeer(NodeKey(p.Id).PublicKey)))
        {
            if (Now() - lastDial > 3_000_000_000L)
            {
                foreach (PeerConfig peer in dialed) binding.Dial(NodeKey(peer.Id).PublicKey, peer.Ip, peer.Port);
                lastDial = Now();
            }
            await Task.Delay(50, deadline.Token);
        }
        Log($"connected to {node.Transport.PeerCount} peers");
        File.WriteAllText(Path_($"ready.{config.Id}"), "");

        // A peer dropped during the run (for example after a protocol violation) is redialed at the interval of the
        // ethp2p host's static dialing, as a node keeps its configured peers.
        int redials = 0;
        using CancellationTokenSource stopRedial = new();
        Task redial = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), stopRedial.Token);
                    // The bindings' own view of a connection can lag a replaced session, so the transport's count decides.
                    if (node.Transport.PeerCount >= config.Peers.Length && config.Peers.All(p => node.EthTransactions.HasPeer(NodeKey(p.Id).PublicKey))) continue;
                    foreach (PeerConfig peer in dialed)
                        if (binding.Dial(NodeKey(peer.Id).PublicKey, peer.Ip, peer.Port))
                        {
                            Interlocked.Increment(ref redials);
                            Log($"redialing node {peer.Id}");
                        }
                }
            }
            catch (OperationCanceledException) { }
        });

        while (!File.Exists(Path_("start"))) await Task.Delay(10, deadline.Token);
        await Task.Delay(20, deadline.Token);
        // The start is wall-clock time, the same in every process; release times are kept on the monotonic clock.
        long start = Now() + long.Parse(File.ReadAllText(Path_("start")).Trim()) - UnixNow();
        long measured = 0, warmupCompletedAt = 0;
        TaskCompletionSource startSnapshotTaken = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<long> measuredStart = ReadMeasuredStart();
        async Task<long> ReadMeasuredStart()
        {
            while (!File.Exists(Path_("measure"))) await Task.Delay(10, deadline.Token);
            return Now() + long.Parse(File.ReadAllText(Path_("measure")).Trim()) - UnixNow();
        }

        void SignalWarmup()
        {
            warmupCompletedAt = Now();
            string marker = Path_($"warmup.{config.Id}");
            File.WriteAllText(marker + ".tmp", UnixNow().ToString());
            File.Move(marker + ".tmp", marker);
        }

        ConcurrentDictionary<int, long> originated = new();
        ConcurrentDictionary<int, long> completed = new();
        TaskCompletionSource holdsAll = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int warmupCompleted = 0;
        if (config.Warmup == 0) SignalWarmup();
        void Complete(int index)
        {
            if (!completed.TryAdd(index, Now())) return;
            if (index < config.Warmup && Interlocked.Increment(ref warmupCompleted) == config.Warmup) SignalWarmup();
            if (completed.Count == objects.Length) holdsAll.TrySetResult();
        }
        // Wrappers complete when the node's own validation accepts them, which is also when the transport publishes them.
        Dictionary<ValueHash256, int> indices = objects.ToDictionary(static o => BenchObject.Key(o.Body), static o => o.Index);
        void OnValidated(byte[] body)
        {
            if (indices.TryGetValue(BenchObject.Key(body), out int index)) Complete(index);
        }
        node.Wrappers.WrapperValidated += (body, _) => OnValidated(body);
        List<Task> work = [];
        async Task WaitForRelease(int i)
        {
            if (i < config.Warmup)
            {
                await WaitUntil(start + (long)i * config.IntervalMs * 1_000_000L);
                return;
            }
            long target = await measuredStart;
            await WaitUntil(target + (long)((i - config.Warmup) / Math.Max(1, config.Burst)) * config.IntervalMs * 1_000_000L);
            await startSnapshotTaken.Task.WaitAsync(deadline.Token);
        }
        async Task WaitUntil(long when)
        {
            long remaining = when - Now();
            if (remaining > 2_000_000) await Task.Delay(TimeSpan.FromTicks((remaining - 1_000_000) / 100), deadline.Token);
            while (Now() < when) Thread.SpinWait(50);
        }

        async Task Originate(BenchObject o)
        {
            await WaitForRelease(o.Index);
            originated[o.Index] = Now();
            if (o.Kind == LeanProtocol.KindBlockProof)
            {
                Import(o);
            }
            else
            {
                ProofWrapperAcceptance acceptance;
                while ((acceptance = await node.Wrappers.AcceptDetailedAsync(o.Body, o.Transactions.ToDictionary(t => t.Hash!.ValueHash256))).Status == ProofWrapperAcceptanceStatus.Busy)
                    await Task.Delay(1, deadline.Token);
                if (!acceptance.HasValidProof) throw new InvalidOperationException($"object {o.Index} was refused at its origin: {acceptance.Status}");
            }
            if (profile is not null && o.Kind != LeanProtocol.KindWrapper)
            {
                // A node broadcasts only objects it has stored, which publication does in the background.
                while (!node.Transport.IsStored(o.Descriptor.ObjectId)) await Task.Delay(1, deadline.Token);
                LeanBroadcastManifest manifest = LeanBroadcastManifest.Create(BenchBroadcastProfile.ProfileId, [1, 2, 3, 4], profile.CurrentSlot,
                    0, (ulong)config.Id, default, o.Descriptor, o.Body, out _);
                if (!binding.Broadcast!.Originate(manifest, BenchBroadcastProfile.Sign(manifest), []))
                    Log($"object {o.Index}: broadcast was not originated");
            }
            Complete(o.Index);
        }

        async Task FetchSidecar(BenchObject o)
        {
            await WaitForRelease(o.Index);
            RecursiveStark? proof = await node.Transport.TryGetAsync(new Block(o.Header!, new BlockBody(o.Transactions, [], null)), deadline.Token);
            if (proof is null) return;
            // Import verifies the proof before the block, and so its sidecar, is served onward.
            if (config.VerifyMs > 0) await Task.Delay(config.VerifyMs, deadline.Token);
            Import(o);
            Complete(o.Index);
        }

        foreach (BenchObject o in objects)
        {
            if (Origin(o.Index) == config.Id) work.Add(Task.Run(() => Originate(o)));
            else if (o.Kind == LeanProtocol.KindBlockProof) work.Add(Task.Run(() => FetchSidecar(o)));
        }

        Snapshot idle = default, before = default;
        int warmupAtIdle = 0, warmupAtStart = 0;
        long shardsBefore = 0;
        Task snapshot = Task.Run(async () =>
        {
            measured = await measuredStart;
            await WaitUntil(measured - 2_000_000_000L);
            warmupAtIdle = completed.Count(e => e.Key < config.Warmup);
            idle = Snapshot.Take();
            await WaitUntil(measured);
            warmupAtStart = completed.Count(e => e.Key < config.Warmup);
            shardsBefore = binding.Broadcast?.ShardsAccepted ?? 0;
            before = Snapshot.Take();
            startSnapshotTaken.TrySetResult();
        });
        try
        {
            while (!holdsAll.Task.IsCompleted)
            {
                await Task.WhenAny(holdsAll.Task, Task.Delay(100, deadline.Token));
                deadline.Token.ThrowIfCancellationRequested();
                if (work.FirstOrDefault(static t => t.IsFaulted) is { } failed) throw failed.Exception!;
            }
        }
        catch (OperationCanceledException)
        {
            Log($"timed out holding {completed.Count} of {objects.Length} objects");
        }
        long finished = Now();
        File.WriteAllText(Path_($"done.{config.Id}"), "");
        Log($"holds {completed.Count} of {objects.Length} objects");

        using CancellationTokenSource stopWait = new(TimeSpan.FromSeconds(config.TimeoutSec + 30));
        while (!File.Exists(Path_("stop")))
        {
            try { await Task.Delay(10, stopWait.Token); }
            catch (OperationCanceledException) { break; }
        }
        await snapshot;
        Snapshot after = Snapshot.Take();
        await stopRedial.CancelAsync();
        await redial;

        Dictionary<string, object?> result = new()
        {
            ["id"] = config.Id,
            ["runtime"] = Environment.Version.ToString(),
            ["body_bytes"] = objects[0].Body.Length,
            ["binding"] = config.Binding,
            ["broadcast"] = config.Broadcast,
            ["start_ns"] = start,
            ["measured_ns"] = measured,
            ["finished_ns"] = finished,
            ["warmup_completed_ns"] = warmupCompletedAt,
            ["idle_snapshot_ns"] = idle.Time,
            ["snapshot_before_ns"] = before.Time,
            ["snapshot_after_ns"] = after.Time,
            ["warmup_completed_at_idle"] = warmupAtIdle,
            ["warmup_completed_at_start"] = warmupAtStart,
            ["originated"] = originated.ToDictionary(static e => e.Key.ToString(), static e => e.Value),
            ["completed"] = completed.ToDictionary(static e => e.Key.ToString(), static e => e.Value),
            ["cpu_user_ms"] = (after.User - before.User).TotalMilliseconds,
            ["cpu_sys_ms"] = (after.System - before.System).TotalMilliseconds,
            ["idle_cpu_ms_per_s"] = (before.User + before.System - idle.User - idle.System).TotalMilliseconds / ((before.Time - idle.Time) / 1e9),
            ["window_ms"] = (after.Time - before.Time) / 1e6,
            ["allocated_bytes"] = after.Allocated - before.Allocated,
            ["gc_gen2"] = after.Gen2 - before.Gen2,
            ["peak_rss_kb"] = after.PeakRssKb,
            ["rss_before_kb"] = before.RssKb,
            ["rss_after_kb"] = after.RssKb,
            ["rlpx_bytes_sent"] = after.RlpxSent - before.RlpxSent,
            ["rlpx_bytes_received"] = after.RlpxReceived - before.RlpxReceived,
            ["ethp2p_stream_bytes"] = after.Streams.ToDictionary(static e => e.Key, e => e.Value - before.Streams.GetValueOrDefault(e.Key)),
            ["events"] = after.Events.ToDictionary(static e => e.Key, e => e.Value - before.Events.GetValueOrDefault(e.Key)),
            ["transfers"] = after.Transfers.ToDictionary(static e => e.Key, e => e.Value - before.Transfers.GetValueOrDefault(e.Key)),
            ["coding_micros"] = after.Coding - before.Coding,
            ["shards_accepted"] = binding.Broadcast?.ShardsAccepted ?? 0,
            ["shards_accepted_measured"] = (binding.Broadcast?.ShardsAccepted ?? 0) - shardsBefore,
            ["peers_at_end"] = node.Transport.PeerCount,
            ["redials"] = redials
        };
        File.WriteAllText(Path_($"result.{config.Id}.json"), JsonSerializer.Serialize(result, Json));
        Log("result written");
    }

    private readonly record struct Snapshot(long Time, TimeSpan User, TimeSpan System, long Allocated, int Gen2, long PeakRssKb, long RssKb, long RlpxSent,
        long RlpxReceived, long Coding, Dictionary<string, long> Streams, Dictionary<string, long> Events, Dictionary<string, long> Transfers)
    {
        public static Snapshot Take()
        {
            using Process process = Process.GetCurrentProcess();
            (long peak, long rss) = ReadRss();
            return new Snapshot(Now(), process.UserProcessorTime, process.PrivilegedProcessorTime, GC.GetTotalAllocatedBytes(true), GC.CollectionCount(2),
                peak, rss, Interlocked.Read(ref Metrics.P2PBytesSent), Interlocked.Read(ref Metrics.P2PBytesReceived),
                Interlocked.Read(ref Metrics.LeanBroadcastCodingMicros),
                Metrics.LeanEthp2pStreamBytes.ToDictionary(static e => $"{e.Key.Direction}/{e.Key.Stream}", static e => e.Value),
                Metrics.LeanObjectEvents.ToDictionary(static e => $"{e.Key.Kind}/{e.Key.Event}", static e => e.Value),
                TransferCounter.Instance.Counts.ToDictionary(static e => e.Key, static e => e.Value));
        }

        private static (long Peak, long Rss) ReadRss()
        {
            if (!File.Exists("/proc/self/status")) return (0, 0);
            long peak = 0, rss = 0;
            foreach (string line in File.ReadLines("/proc/self/status"))
            {
                if (line.StartsWith("VmHWM:")) peak = long.Parse(line[6..].Trim().Split(' ')[0]);
                else if (line.StartsWith("VmRSS:")) rss = long.Parse(line[6..].Trim().Split(' ')[0]);
            }
            return (peak, rss);
        }
    }

    /// <summary>Writes Info and above, and only the Debug lines matching a pattern, such as protocol violations.</summary>
    private sealed class FilteredLogManager(string pattern) : ILogManager, InterfaceLogger
    {
        private readonly System.Text.RegularExpressions.Regex _debug = new(pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        public ILogger GetClassLogger<T>() => new(this);
        public ILogger GetLogger(string loggerName) => new(this);
        public void Info(string text) => Write(text);
        public void Warn(string text) => Write(text);
        public void Debug(string text)
        {
            if (_debug.IsMatch(text)) Write(text);
        }
        public void Trace(string text) { }
        public void Error(string text, Exception? ex = null) => Write(ex is null ? text : $"{text}: {ex}");
        public bool IsInfo => true;
        public bool IsWarn => true;
        public bool IsDebug => true;
        public bool IsTrace => false;
        public bool IsError => true;
        private static void Write(string text) => Console.WriteLine($"{DateTime.UtcNow:HH:mm:ss.fff} {text}");
    }

    /// <summary>Counts validated transfers by kind and path (retrieval or broadcast).</summary>
    private sealed class TransferCounter : IMetricObserver
    {
        public static readonly TransferCounter Instance = new();
        public ConcurrentDictionary<string, long> Counts { get; } = new();

        public void Observe(double value, IMetricLabels? labels = null) =>
            Counts.AddOrUpdate(labels is null ? "" : string.Join("/", labels.Labels), 1, static (_, count) => count + 1);
    }

    /// <summary>A deterministic benchmark object, identical in every node process.</summary>
    internal sealed class BenchObject
    {
        public required int Index { get; init; }
        public required byte Kind { get; init; }
        public required byte[] Body { get; init; }
        public required LeanDescriptor Descriptor { get; init; }
        public BlockHeader? Header { get; init; }
        public byte[]? Proof { get; init; }
        public Transaction[] Transactions { get; init; } = [];

        /// <summary>Identifies a body by its leading bytes, which hold its distinct transaction.</summary>
        public static ValueHash256 Key(byte[] body) => ValueKeccak.Compute(body.AsSpan(0, Math.Min(body.Length, 4096)));

        public static BenchObject Create(int kind, int index, int proofBytes, int transactionsPerObject)
        {
            switch (kind)
            {
                case LeanProtocol.KindWrapper:
                {
                    Transaction[] transactions = [.. Enumerable.Range(0, transactionsPerObject).Select(i =>
                    {
                        int seed = checked(index * transactionsPerObject + i + 1);
                        Transaction transaction = FrameTransaction(seed);
                        FrameDependency dep = new(Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute(BitConverter.GetBytes(seed)), default);
                        transaction.Frames = [new(FrameMode.DepVerify, FrameFlags.None, null, Eip8288Constants.LeanSphincsVerificationGas,
                            UInt256.Zero, Eip8288Dependencies.Serialize([dep]))];
                        transaction.Hash = new Hash256(ValueKeccak.Compute(Envelope(transaction)));
                        return transaction;
                    })];
                    byte[] body = Wrapper(proofBytes, true, transactions);
                    return new BenchObject { Index = index, Kind = (byte)kind, Body = body, Descriptor = Describe(body), Transactions = transactions };
                }
                case LeanProtocol.KindBlockProof:
                {
                    byte[] proof = new byte[proofBytes];
                    new Random(1000 + index).NextBytes(proof);
                    Transaction transaction = FrameTransaction(index + 1);
                    Hash256 depsHash = new(Eip8288Dependencies.ComputeDepsHash(Eip8288Dependencies.ForTransaction(transaction)));
                    BlockHeader header = LeanTransportTests.ProofHeader(proof, depsHash, (ulong)index + 1);
                    byte[] body = LeanRlp.EncodeList(LeanRlp.EncodeBytes(proof));
                    LeanDescriptor descriptor = LeanDescriptor.Create(LeanProtocol.KindBlockProof, LeanObjectTransport.LocalProfile,
                        LeanDescriptor.BlockProofContext(header.Hash!.ValueHash256), body);
                    return new BenchObject { Index = index, Kind = (byte)kind, Body = body, Descriptor = descriptor, Header = header, Proof = proof, Transactions = [transaction] };
                }
                default:
                    throw new ArgumentException($"kind {kind}");
            }
        }
    }

    internal interface IBenchBinding : IAsyncDisposable
    {
        /// <summary>Dials a peer that has no connection or pending dial; returns whether a dial started.</summary>
        bool Dial(PublicKey key, string ip, int port);
        LeanBroadcastEngine? Broadcast { get; }
    }

    /// <summary>The ethp2p QUIC binding, as <see cref="LeanEthp2pQuicTests"/> runs it.</summary>
    private sealed class Ethp2pBinding(LeanEthp2pHost host, RlpxBinding eth) : IBenchBinding
    {
        private readonly ConcurrentDictionary<PublicKey, Task<LeanEthp2pConnection?>> _dials = new();

        public static async Task<Ethp2pBinding> StartAsync(PrivateKey key, string ip, int port, LeanTestNode node, BenchBroadcastProfile? profile,
            ILogManager logs)
        {
            if (!LeanEthp2pHost.IsSupported) throw new InvalidOperationException("QUIC is unavailable (libmsquic is not installed)");
            NetworkConfig config = new() { LocalIp = ip, LeanEthp2pPort = port, LeanBindings = LeanBinding.Ethp2p, LeanCommonBinding = LeanBinding.Ethp2p };
            LeanEthp2pHost host = new(node.Transport, config, new InsecureProtectedPrivateKey(key), logs, node.EthTransactions, profile);
            await host.StartAsync(CancellationToken.None);
            RlpxBinding eth = await RlpxBinding.StartAsync(key, ip, port - 1, node, logs, lean: false);
            return new Ethp2pBinding(host, eth);
        }

        public LeanBroadcastEngine? Broadcast => host.Broadcast;

        /// <summary>Dials a peer unless a dial is pending or its connection is open, as the host's own static dialing does.</summary>
        /// <remarks>A second connection from the same side replaces the first, dropping its requests and broadcast sessions.</remarks>
        public bool Dial(PublicKey key, string ip, int port)
        {
            eth.Dial(key, ip, port - 1);
            if (_dials.TryGetValue(key, out Task<LeanEthp2pConnection?>? dial)
                && (!dial.IsCompleted || dial.Result is { Closed.IsCompleted: false })) return false;
            _dials[key] = host.DialAsync(new LeanEthp2pRecord(key, new IPEndPoint(IPAddress.Parse(ip), port)), CancellationToken.None);
            return true;
        }

        public async ValueTask DisposeAsync()
        {
            await host.DisposeAsync();
            await eth.DisposeAsync();
        }
    }

    /// <summary>The RLPx binding over TCP: production handshake, framing, Snappy, P2P Hello and the lean/1 handler.</summary>
    private sealed class RlpxBinding(RlpxHost host, ProtocolsManager manager, ConcurrentDictionary<PublicKey, ISession> sessions, BackgroundTaskScheduler scheduler,
        SessionMonitor monitor) : IBenchBinding
    {
        private readonly ProtocolsManager _manager = manager;

        public static async Task<RlpxBinding> StartAsync(PrivateKey key, string ip, int port, LeanTestNode node, ILogManager logs, bool lean = true)
        {
            CryptoRandom random = new();
            IMessageSerializationService serializer = Build.A.SerializationService().WithEncryptionHandshake().WithP2P().WithEth66()
                .With(new AddCapabilityMessageSerializer())
                .With(new LeanStatusMessageSerializer()).With(new AnnounceObjectsMessageSerializer())
                .With(new GetObjectsMessageSerializer()).With(new ObjectsMessageSerializer())
                .With(new GetChunksMessageSerializer()).With(new ChunkMessageSerializer())
                .With(new CompleteMessageSerializer()).With(new CancelMessageSerializer()).TestObject;
            HandshakeService handshake = new(serializer, new EciesCipher(random), random, new Ecdsa(), key, logs);
            IPAddress address = IPAddress.Parse(ip);
            NetworkConfig config = new() { P2PPort = port, LocalIp = ip, ExternalIp = ip, MaxActivePeers = 50, RlpxHostShutdownCloseTimeoutMs = 100 };
            IIPResolver resolver = Substitute.For<IIPResolver>();
            resolver.Resolve(Arg.Any<CancellationToken>()).Returns(new ValueTask<IIPResolver.NethermindIp>(new IIPResolver.NethermindIp(address, address)));
            IPrivilegedIpProvider privileged = Substitute.For<IPrivilegedIpProvider>();
            privileged.IsPrivileged(Arg.Any<IPAddress>()).Returns(true);
            SessionMonitor monitor = new(config, logs);
            RlpxHost host = new(serializer, handshake, monitor, NullDisconnectsAnalyzer.Instance, config, resolver, privileged, logs,
                new NetworkListenerState(config, resolver, logs));

            INodeStatsManager stats = Substitute.For<INodeStatsManager>();
            BackgroundTaskScheduler scheduler = new(Substitute.For<IBranchProcessor>(), Substitute.For<IChainHeadInfoProvider>(), 2, 64, logs);
            ISyncServer sync = Substitute.For<ISyncServer>();
            sync.Head.Returns(_ => node.Tree.Head!.Header);
            sync.Genesis.Returns(_ => node.Tree.Genesis!);
            sync.NetworkId.Returns(1UL);
            Enode enode = new(key.PublicKey, address, port);
            IProtocolValidator validator = Substitute.For<IProtocolValidator>();
            validator.ValidateOrDisconnect(Arg.Any<string>(), Arg.Any<ISession>(), Arg.Any<ProtocolInitializedEventArgs>()).Returns(true);
            IProtocolHandlerFactory[] factories =
            [
                new Factory(Protocol.P2P, session => new P2PProtocolHandler(session, enode, stats, serializer, scheduler, logs)),
                new Factory(Protocol.Eth, session => new Eth66ProtocolHandler(session, serializer, stats, sync, scheduler, node.Pool,
                    Substitute.For<IGossipPolicy>(), new ForkInfo(node.Specs, sync), logs, leanTransactions: node.EthTransactions)),
                new Factory(LeanProtocol.Code, session => new Lean1ProtocolHandler(session, stats, serializer, scheduler, logs, node.Transport, node.EthTransactions))
            ];
            ProtocolsManager manager = new(Substitute.For<ISyncPeerPool>(), Substitute.For<ITxPool>(), Substitute.For<IDiscoveryApp>(), host, stats,
                validator, Substitute.For<IPeerManager>(), Substitute.For<INetworkStorage>(), factories,
                [new BenchCapabilities(lean)], logs);
            ConcurrentDictionary<PublicKey, ISession> sessions = new();
            host.SessionCreated += (_, e) =>
            {
                if (e.Session.RemoteNodeId is { } id) sessions[id] = e.Session;
                e.Session.Disconnected += (_, _) =>
                {
                    if (e.Session.RemoteNodeId is { } gone) sessions.TryRemove(new KeyValuePair<PublicKey, ISession>(gone, e.Session));
                };
            };
            await host.Init();
            monitor.Start();
            return new RlpxBinding(host, manager, sessions, scheduler, monitor);
        }

        public LeanBroadcastEngine? Broadcast => null;

        public bool Dial(PublicKey key, string ip, int port)
        {
            if (sessions.ContainsKey(key)) return false;
            _ = host.ConnectAsync(new Node(key, ip, port));
            return true;
        }

        public async ValueTask DisposeAsync()
        {
            GC.KeepAlive(_manager);
            monitor.Stop();
            await host.Shutdown();
            await scheduler.DisposeAsync();
        }

        private sealed class BenchCapabilities(bool lean) : IP2PCapabilityResolver
        {
            public event Action? Changed { add { } remove { } }
            public void Resolve(ISet<Capability> capabilities)
            {
                capabilities.Add(new Capability(Protocol.Eth, 66));
                if (lean) capabilities.Add(new Capability(LeanProtocol.Code, 1));
            }
        }

        private sealed class Factory(string code, Func<ISession, IProtocolHandler> create) : IProtocolHandlerFactory
        {
            public string ProtocolCode => code;

            public bool TryCreate(ISession session, int version, out IProtocolHandler? handler)
            {
                handler = create(session);
                return true;
            }
        }
    }
}

/// <summary>A test-only broadcast profile: one fixed secp256k1 producer key, a 12-second slot clock from a shared genesis,
/// and up to <see cref="Duties"/> duties per kind and slot.</summary>
/// <remarks>Authorization recovers the signer of the manifest ID, which costs what a real producer-signature check costs;
/// there is no consensus context, so every reconstructed body matches.</remarks>
internal sealed class BenchBroadcastProfile(long genesisUnixMs) : ILeanBroadcastProfile
{
    public static readonly ValueHash256 ProfileId = ValueKeccak.Compute("lean-net-bench broadcast profile"u8);
    private static readonly PrivateKey Signer = new(Keccak.Compute("lean-net-bench producer").Bytes.ToArray());
    private static readonly Ecdsa Ecdsa = new();
    private static readonly TimeSpan SlotTime = TimeSpan.FromSeconds(12);
    public const int Duties = 1;

    private readonly DateTimeOffset _genesis = DateTimeOffset.FromUnixTimeMilliseconds(genesisUnixMs);

    public ValueHash256 Id => ProfileId;

    public ulong CurrentSlot
    {
        get
        {
            TimeSpan elapsed = DateTimeOffset.UtcNow - _genesis;
            return elapsed <= TimeSpan.Zero ? 0 : (ulong)(elapsed / SlotTime);
        }
    }

    public DateTimeOffset SlotStart(ulong slot) => _genesis + SlotTime * slot;

    public LeanBroadcastAuthorization Authorize(LeanBroadcastManifest manifest, in ValueHash256 manifestId, ReadOnlySpan<byte> signature,
        ReadOnlySpan<byte> authorization)
    {
        if (manifest.DutyIndex >= Duties || signature.Length != Signature.Size) return LeanBroadcastAuthorization.Rejected;
        PublicKey? signer = Ecdsa.RecoverPublicKey(new Signature(signature[..64], signature[64]), manifestId);
        return signer is not null && signer.Equals(Signer.PublicKey) ? LeanBroadcastAuthorization.Authorized : LeanBroadcastAuthorization.Rejected;
    }

    public bool MatchesContext(LeanBroadcastManifest manifest, ReadOnlySpan<byte> body) => true;

    public static byte[] Sign(LeanBroadcastManifest manifest) =>
        Ecdsa.Sign(Signer, manifest.Id(1, LeanTestNode.Genesis.ValueHash256)).BytesWithRecovery;
}

[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
public class LeanNetBenchFixtureTests
{
    [Test]
    public void Controlled_pool_uses_hash_index_and_owned_snapshots()
    {
        using LeanTestNode node = new();
        LeanNetBenchNode.ConfigureIndexedPool(node);
        Transaction[] transactions = [FrameTransaction(1), FrameTransaction(2)];
        Transaction decoy = FrameTransaction(3);
        node.Pending.Add(decoy);
        foreach (Transaction transaction in transactions)
            Assert.That(node.Pool.SubmitTx(transaction, TxHandlingOptions.PersistentBroadcast), Is.EqualTo(AcceptTxResult.Accepted));
        node.Pool.SubmitTx(transactions[0], TxHandlingOptions.PersistentBroadcast);
        Transaction[] snapshot = node.Pool.GetPendingTransactions();
        Assert.That(snapshot, Is.EquivalentTo(transactions));
        snapshot[0] = decoy;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(node.Pool.GetPendingTransactions(), Is.EquivalentTo(transactions));
            Assert.That(node.Pool.TryGetPendingTransaction(transactions[0].Hash!.ValueHash256, out Transaction? found), Is.True);
            Assert.That(found, Is.SameAs(transactions[0]));
            Assert.That(node.Pool.TryGetPendingTransaction(decoy.Hash!.ValueHash256, out Transaction? missing), Is.False);
            Assert.That(missing, Is.Null);
        }
        node.Reject = true;
        Assert.That(node.Pool.SubmitTx(decoy, TxHandlingOptions.PersistentBroadcast), Is.EqualTo(AcceptTxResult.Invalid));
        Assert.That(node.Pool.GetPendingTransactions(), Is.EquivalentTo(transactions));
    }
}
