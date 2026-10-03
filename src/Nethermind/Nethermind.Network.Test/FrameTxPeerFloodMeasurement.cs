// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using DotNetty.Buffers;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Timers;
using Nethermind.Logging;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V62;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V68;
using Nethermind.Network.Rlpx;
using Nethermind.Network.Test.Builders;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test;

/// <summary>
/// Measures how many attacker frame transactions one peer gets this node to process before
/// <see cref="TxFloodController"/> downgrades and then disconnects it: the campaign's r_attack per peer.
/// </summary>
/// <remarks>
/// Drives a real <see cref="Eth68ProtocolHandler"/> with full <c>Transactions</c> messages, so each one goes
/// through the real per-message gate, real deserialization and the real per-transaction report. Only three
/// things are substituted:
/// <list type="bullet">
/// <item>The pool, which returns the verdict a shape gets from the real pool: <c>FrameSimulationFailed</c>
/// for an EVM shape such as keccak-wide, optionally alternating with <c>FrameSimulationDeferred</c> to model a
/// spent per-head simulation budget, and <c>Invalid</c> from <c>FrameTxSignatureFilter</c> for signature
/// stuffing. The controller reads only the verdict, so the wire transactions are plain signed legacy ones.</item>
/// <item>The flood controller's clock, swapped for a <see cref="ManualTimestamper"/> so the 60 s window runs
/// in simulated time.</item>
/// <item>The session, which records the first disconnect. The harness then stops delivering messages, as
/// <c>Session.ReceiveMessage</c> drops them once the session is closing; the rest of the message in flight
/// is still processed, as on this branch.</item>
/// </list>
/// "Processed" counts the verdicts that cost this node verification work (failed simulations and invalid
/// signatures); a deferral is assumed to cost none. Gas per second is processed tx/s times the ceiling C.
/// Results are appended as <c>RESULT case=peer_flood ...</c> lines to <c>FRAME_PEER_FLOOD_OUT</c>, then
/// <c>FRAME_RETRY_OUT</c>, or <c>frame-peer-flood.txt</c> in the temp directory.
/// </remarks>
[TestFixture]
[Explicit("measurement harness")]
[NonParallelizable]
[Category("frame-tx-cost")]
public class FrameTxPeerFloodMeasurement
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(60);
    private static readonly DateTime Start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly int[] OfferedRates = [10, 50, 100, 200, 500, 800, 1_000, 2_000];
    private static readonly ulong[] Ceilings = [100_000, 235_800, 300_000, 500_000];

    /// <summary>Seeds for the controller's drop sampler, which decides which messages a downgraded peer gets through.</summary>
    private const int Seeds = 5;

    public enum Shape
    {
        KeccakWide,
        KeccakWideHalfDeferred,
        SignatureStuffed,
    }

    private readonly record struct Outcome(
        int Submitted,
        int Processed,
        TimeSpan? DowngradedAt,
        TimeSpan? DisconnectedAt,
        DisconnectReason? Reason);

    [Test]
    public void Peer_flood([Values] Shape shape, [Values(1, 64)] int txsPerMessage)
    {
        byte[] packet = SerializeTransactions(txsPerMessage);

        foreach (int rate in OfferedRates)
        {
            Outcome[] outcomes = new Outcome[Seeds];
            for (int seed = 0; seed < Seeds; seed++)
            {
                outcomes[seed] = Run(shape, rate, txsPerMessage, seed, packet);
                AssertExpectedReason(shape, outcomes[seed]);
            }

            Emit(shape, rate, txsPerMessage, outcomes);
        }
    }

    private static void AssertExpectedReason(Shape shape, in Outcome outcome)
    {
        if (shape == Shape.SignatureStuffed)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(outcome.Reason, Is.EqualTo(DisconnectReason.InvalidTxReceived));
                Assert.That(outcome.DisconnectedAt, Is.EqualTo(TimeSpan.Zero));
            }
        }
        else if (outcome.Reason is not null)
        {
            Assert.That(outcome.Reason, Is.EqualTo(DisconnectReason.TxFlooding));
        }
    }

    private static Outcome Run(Shape shape, int rate, int txsPerMessage, int seed, byte[] packet)
    {
        ManualTimestamper clock = new(Start);
        Block genesis = Build.A.Block.Genesis.TestObject;
        ISyncServer syncServer = Substitute.For<ISyncServer>();
        syncServer.Head.Returns(genesis.Header);
        syncServer.Genesis.Returns(genesis.Header);

        TimeSpan? disconnectedAt = null;
        DisconnectReason? reason = null;
        ISession session = Substitute.For<ISession>();
        session.Node.Returns(new Node(TestItem.PublicKeyA, new IPEndPoint(IPAddress.Loopback, 30303)));
        session.When(s => s.DeliverMessage(Arg.Any<P2PMessage>())).Do(c => c.Arg<P2PMessage>().Dispose());
        // A real session reports closing as soon as the disconnect starts, which is what stops the handler from
        // submitting the rest of an in-flight message.
        session.IsClosing.Returns(_ => reason is not null);
        session.When(s => s.InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>())).Do(c =>
        {
            if (reason is null)
            {
                reason = c.Arg<DisconnectReason>();
                disconnectedAt = clock.UtcNow - Start;
            }
        });

        int submitted = 0;
        int processed = 0;
        ITxPool txPool = Substitute.For<ITxPool>();
        txPool.SubmitTx(Arg.Any<Transaction>(), Arg.Any<TxHandlingOptions>()).Returns(_ =>
        {
            AcceptTxResult verdict = Verdict(shape, submitted++);
            if (verdict != AcceptTxResult.FrameSimulationDeferred) processed++;
            return verdict;
        });

        ITxGossipPolicy gossipPolicy = Substitute.For<ITxGossipPolicy>();
        gossipPolicy.ShouldListenToGossipedTransactions.Returns(true);

        using Eth68ProtocolHandler handler = new(
            session,
            Build.A.SerializationService().WithEth68().TestObject,
            new NodeStatsManager(Substitute.For<ITimerFactory>(), LimboLogs.Instance),
            syncServer,
            RunImmediatelyScheduler.Instance,
            txPool,
            Substitute.For<IGossipPolicy>(),
            new ForkInfo(Substitute.For<ISpecProvider>(), syncServer),
            LimboLogs.Instance,
            Substitute.For<ITxPoolConfig>(),
            Substitute.For<IChainHeadSpecProvider>(),
            gossipPolicy);
        UseClock(handler, clock, seed);
        handler.Init();
        ReceiveStatus(handler, genesis);
        Assert.That(reason, Is.Null, "the handshake disconnected the peer before the flood started");

        TimeSpan? downgradedAt = null;
        double interval = (double)txsPerMessage / rate;
        for (long message = 0; reason is null; message++)
        {
            TimeSpan at = TimeSpan.FromSeconds(message * interval);
            if (at >= Window) break;

            clock.Set(Start + at);
            handler.HandleMessage(new Packet(Protocol.Eth, Eth62MessageCode.Transactions, packet));
            if (downgradedAt is null && handler.IsFloodDowngraded) downgradedAt = at;
        }

        return new Outcome(submitted, processed, downgradedAt, disconnectedAt, reason);
    }

    private static AcceptTxResult Verdict(Shape shape, int index) => shape switch
    {
        Shape.KeccakWide => AcceptTxResult.FrameSimulationFailed,
        Shape.KeccakWideHalfDeferred => index % 2 == 0
            ? AcceptTxResult.FrameSimulationFailed
            : AcceptTxResult.FrameSimulationDeferred,
        Shape.SignatureStuffed => AcceptTxResult.Invalid,
        _ => throw new ArgumentOutOfRangeException(nameof(shape)),
    };

    /// <summary>The handler builds its controller on the wall clock; swap in one on simulated time.</summary>
    private static void UseClock(Eth62ProtocolHandler handler, ITimestamper clock, int seed)
    {
        FieldInfo? field = typeof(Eth62ProtocolHandler).GetField("_floodController", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null, "Eth62ProtocolHandler no longer keeps its flood controller in _floodController");
        field!.SetValue(handler, new TxFloodController(handler, clock, LimboLogs.Instance.GetClassLogger<FrameTxPeerFloodMeasurement>(), new Random(seed)));
    }

    private static void ReceiveStatus(Eth68ProtocolHandler handler, Block genesis)
    {
        IMessageSerializationService serializer = Build.A.SerializationService().WithEth68().TestObject;
        StatusMessage status = new() { GenesisHash = genesis.Hash!, BestHash = genesis.Hash! };
        IByteBuffer buffer = serializer.ZeroSerialize(status);
        try
        {
            buffer.ReadByte();
            handler.HandleMessage(new ZeroPacket(buffer) { PacketType = Eth62MessageCode.Status });
        }
        finally
        {
            buffer.Release();
        }
    }

    /// <summary>Wire bytes of one <c>Transactions</c> message, without the message-code prefix.</summary>
    /// <remarks>Its content does not matter: the controller sees only the verdict the substitute pool returns.</remarks>
    private static byte[] SerializeTransactions(int count)
    {
        ArrayPoolList<Transaction> transactions = new(count);
        for (int i = 0; i < count; i++)
        {
            transactions.Add(Build.A.Transaction.WithNonce((ulong)i).SignedAndResolved(TestItem.PrivateKeyA).TestObject);
        }

        using TransactionsMessage message = new(transactions);
        IMessageSerializationService serializer = Build.A.SerializationService().WithEth68().TestObject;
        IByteBuffer buffer = serializer.ZeroSerialize(message);
        try
        {
            buffer.ReadByte();
            byte[] bytes = new byte[buffer.ReadableBytes];
            buffer.ReadBytes(bytes);
            return bytes;
        }
        finally
        {
            buffer.Release();
        }
    }

    private static void Emit(Shape shape, int rate, int txsPerMessage, Outcome[] outcomes)
    {
        double windowSeconds = Window.TotalSeconds;
        double processedMean = outcomes.Average(o => (double)o.Processed);
        double processedPerSecond = processedMean / windowSeconds;
        TimeSpan[] disconnects = outcomes.Where(o => o.DisconnectedAt is not null).Select(o => o.DisconnectedAt!.Value).ToArray();
        TimeSpan[] downgrades = outcomes.Where(o => o.DowngradedAt is not null).Select(o => o.DowngradedAt!.Value).ToArray();
        DisconnectReason? reason = outcomes.Select(o => o.Reason).FirstOrDefault(r => r is not null);

        List<string> fields =
        [
            "case=peer_flood",
            $"shape={ShapeName(shape)}",
            $"txs_per_msg={txsPerMessage}",
            $"offered_tx_per_s={rate}",
            $"window_s={F(windowSeconds)}",
            $"seeds={outcomes.Length}",
            $"submitted_mean={F(outcomes.Average(o => (double)o.Submitted))}",
            $"processed_mean={F(processedMean)}",
            $"processed_min={outcomes.Min(o => o.Processed)}",
            $"processed_max={outcomes.Max(o => o.Processed)}",
            $"downgraded={downgrades.Length}/{outcomes.Length}",
            $"downgrade_s={(downgrades.Length == 0 ? "none" : F(downgrades.Average(t => t.TotalSeconds)))}",
            $"disconnected={disconnects.Length}/{outcomes.Length}",
            $"disconnect_s_mean={(disconnects.Length == 0 ? "none" : F(disconnects.Average(t => t.TotalSeconds)))}",
            $"disconnect_s_min={(disconnects.Length == 0 ? "none" : F(disconnects.Min().TotalSeconds))}",
            $"disconnect_s_max={(disconnects.Length == 0 ? "none" : F(disconnects.Max().TotalSeconds))}",
            $"disconnect_reason={reason?.ToString() ?? "none"}",
            $"processed_tx_per_s={F(processedPerSecond)}",
        ];
        foreach (ulong ceiling in Ceilings)
        {
            fields.Add($"gas_per_s_c{ceiling}={F(processedPerSecond * ceiling)}");
        }

        fields.Add("handler=eth68");
        string record = $"RESULT {string.Join(' ', fields)}";
        string path = Environment.GetEnvironmentVariable("FRAME_PEER_FLOOD_OUT")
                      ?? Environment.GetEnvironmentVariable("FRAME_RETRY_OUT")
                      ?? Path.Combine(Path.GetTempPath(), "frame-peer-flood.txt");
        TestContext.Out.WriteLine(record);
        File.AppendAllText(path, record + Environment.NewLine);
    }

    private static string ShapeName(Shape shape) => shape switch
    {
        Shape.KeccakWide => "keccak-wide",
        Shape.KeccakWideHalfDeferred => "keccak-wide-half-deferred",
        Shape.SignatureStuffed => "signature-stuffed",
        _ => throw new ArgumentOutOfRangeException(nameof(shape)),
    };

    private static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
