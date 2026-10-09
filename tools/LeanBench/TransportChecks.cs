// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using DotNetty.Buffers;
using DotNetty.Transport.Channels.Embedded;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Subprotocols.Lean;

namespace Nethermind.Tools.LeanBench;

internal static class TransportChecks
{
    public static async Task RunAsync()
    {
        await DeclinedProbesDoNotShiftAcceptedSamples();
        int[] chunkSizes = [0, LeanProofChunkMessage.DefaultChunkSize];
        foreach (int chunkSize in chunkSizes)
        {
            await using MixedTrafficTransport transport = new(chunkSize, 100);
            byte[] payload = RandomNumberGenerator.GetBytes(96 * 1024);
            byte[] received = await transport.TransferAsync(payload, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            Require(received.AsSpan().SequenceEqual(payload), "Mixed traffic changed the payload");
            Require(transport.WriteDrops == 0, "Mixed traffic unexpectedly dropped a write");
        }
        foreach (int chunkSize in chunkSizes) await FinalControlDrainPreservesReceiveFailure(chunkSize);
        await MixedReceiveFailureStillCleansUp();
        await LoopbackReceiveFailureIsImmediate();
        string[] malformedChunks = ["packet-type", "short-header", "geometry"];
        foreach (string malformed in malformedChunks)
            await MalformedChunkFailsBeforeCallback(malformed);
        Console.WriteLine("Transport checks passed: declined probes, whole/chunk delivery, receive/drain failures, cleanup and malformed chunks");
    }

    private static async Task DeclinedProbesDoNotShiftAcceptedSamples()
    {
        await using MixedTrafficTransport transport = new(0, 100);
        PacketSender packets = Field<PacketSender>(transport, "_packets");
        FieldInfo removed = FindField(packets, "_removed");
        removed.SetValue(packets, true);
        long declined = Stopwatch.GetTimestamp() - Stopwatch.Frequency;
        int[] probeKinds = [0, 1, 2];
        foreach (int kind in probeKinds)
            Require(!transport.Probe((ulong)kind, declined, kind), "Removed sender accepted a probe");
        removed.SetValue(packets, false);
        long accepted = Stopwatch.GetTimestamp();
        Require(transport.Probe(10, accepted, 0), "Writable sender declined a ping");
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        while (transport.Controls.Length == 0) await Task.Delay(10, timeout.Token);
        ControlSample[] samples = transport.Controls;
        Require(samples.Length == 1 && samples[0].ScheduledTimestamp == accepted,
            "A declined ping shifted the next accepted sample");
        Require(Field<System.Collections.Concurrent.ConcurrentDictionary<ulong, long>>(transport, "_headerStarts").IsEmpty,
            "Declined header probes retained timestamps");
    }

    private static async Task FinalControlDrainPreservesReceiveFailure(int chunkSize)
    {
        MixedTrafficTransport transport = new(chunkSize, 100);
        Socket sender = Field<Socket>(transport, "_sender");
        Socket receiver = Field<Socket>(transport, "_receiver");
        try
        {
            byte[] payload = RandomNumberGenerator.GetBytes(96 * 1024);
            await transport.TransferAsync(payload, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            transport.ThrowIfReceiveFailed();
            sender.Shutdown(SocketShutdown.Send);
            Task reader = Field<Task>(transport, "_reading");
            await ExpectFailure<EndOfStreamException>(reader);
            bool observed = false;
            try { transport.ThrowIfReceiveFailed(); }
            catch (EndOfStreamException failure)
            {
                observed = ReferenceEquals(failure, reader.Exception!.GetBaseException());
            }
            Require(observed, "Final control drain did not preserve the receive EOF");
        }
        finally
        {
            await ExpectFailure<EndOfStreamException>(transport.DisposeAsync().AsTask());
            Require(sender.SafeHandle.IsClosed && receiver.SafeHandle.IsClosed, "Final control drain leaked sockets");
        }
    }

    private static async Task MixedReceiveFailureStillCleansUp()
    {
        MixedTrafficTransport transport = new(0, 100);
        Socket sender = Field<Socket>(transport, "_sender");
        Socket receiver = Field<Socket>(transport, "_receiver");
        sender.Shutdown(SocketShutdown.Send);
        try
        {
            Task reader = Field<Task>(transport, "_reading");
            await ExpectFailure<EndOfStreamException>(reader);
            await ExpectFailure<EndOfStreamException>(transport.TransferAsync([1], CancellationToken.None));
        }
        finally
        {
            await ExpectFailure<EndOfStreamException>(transport.DisposeAsync().AsTask());
            Require(sender.SafeHandle.IsClosed && receiver.SafeHandle.IsClosed, "Faulted mixed transport leaked sockets");
        }
    }

    private static async Task LoopbackReceiveFailureIsImmediate()
    {
        TcpRlpxLoopback transport = new();
        Socket sender = Field<Socket>(transport, "_sender");
        Socket receiver = Field<Socket>(transport, "_receiver");
        receiver.Dispose();
        try { await ExpectFailure<ObjectDisposedException>(transport.TransferAsync([1], CancellationToken.None)); }
        finally
        {
            transport.Dispose();
            Require(sender.SafeHandle.IsClosed && receiver.SafeHandle.IsClosed, "Faulted loopback leaked sockets");
        }
    }

    private static async Task MalformedChunkFailsBeforeCallback(string malformed)
    {
        int malformedCallbacks = 0;
        int packetType = malformed == "packet-type" ? 2 : new LeanProofChunkMessage(default, 1, 0, 1,
            LeanProofChunkMessage.DefaultChunkSize, new byte[1]).PacketType;
        byte[] malformedContent = [];
        using TcpRlpxLoopback transport = new(packet =>
        {
            if (packet.PacketType == packetType && packet.Content.ReadableBytes == malformedContent.Length
                && packet.Content.Array.AsSpan(packet.Content.ArrayOffset + packet.Content.ReaderIndex,
                    packet.Content.ReadableBytes).SequenceEqual(malformedContent))
                malformedCallbacks++;
        });
        EmbeddedChannel outbound = Field<EmbeddedChannel>(transport, "_outbound");
        Socket sender = Field<Socket>(transport, "_sender");
        IByteBuffer input = Unpooled.Buffer(LeanProofChunkMessage.HeaderSize + 2);
        input.WriteByte(packetType);
        if (malformed == "short-header") input.WriteByte(0);
        else
        {
            input.WriteZero(32);
            input.WriteInt(1);
            input.WriteInt(malformed == "geometry" ? 1 : 0);
            input.WriteInt(1);
            input.WriteInt(LeanProofChunkMessage.DefaultChunkSize);
            input.WriteByte(0);
        }
        malformedContent = new byte[input.ReadableBytes - 1];
        input.GetBytes(input.ReaderIndex + 1, malformedContent);
        outbound.WriteOutbound(input);
        IByteBuffer wire = outbound.ReadOutbound<IByteBuffer>();
        try
        {
            int sent = 0;
            while (sent < wire.ReadableBytes)
            {
                int written = await sender.SendAsync(wire.Array.AsMemory(wire.ArrayOffset + wire.ReaderIndex + sent,
                    wire.ReadableBytes - sent), SocketFlags.None);
                Require(written != 0, "Malformed chunk write made no progress");
                sent += written;
            }
        }
        finally { wire.Release(); }
        await ExpectFailure<InvalidOperationException>(transport.TransferAsync([1], CancellationToken.None));
        Require(malformedCallbacks == 0, "Malformed chunk reached the protocol callback");
    }

    private static async Task ExpectFailure<T>(Task task) where T : Exception
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}");
    }

    private static T Field<T>(object target, string name) => (T)FindField(target, name).GetValue(target)!;

    private static FieldInfo FindField(object target, string name) => target.GetType().GetField(name,
        BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new MissingFieldException(target.GetType().Name, name);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
