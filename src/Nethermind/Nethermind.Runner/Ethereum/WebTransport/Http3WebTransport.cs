// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Runner.Ethereum.WebTransport;

internal static class Http3WebTransport
{
    private const int MaxFieldSectionSize = 4096;
    private const int MaxFramePayloadSize = 4096;
    private const int MaxClientUniStreams = 8;
    private const long HeadersFrame = 0x01;
    private const long SettingsFrame = 0x04;
    private const long WebTransportStream = 0x41;
    private const long EnableWebTransportDraft02 = 0x2b603742;

    // WebTransport draft-02 is compatible with the reliable-reset dialect in stock MsQuic.
    private static readonly byte[] Settings = BuildSettings();
    private static readonly byte[] Accepted = BuildAccepted();
    private static readonly byte[] BadRequest = [0x01, 0x04, 0x00, 0x00, 0xff, 0x04];

    internal static async Task ServeAsync(
        MsQuicConnection connection,
        Func<string, (BrowserCapability Capability, byte[] Nonce)?> parseOffer,
        Func<Stream, BrowserCapability, byte[], CancellationToken, Task> exchange,
        CancellationToken cancellationToken)
    {
        // The connection keeps HTTP/3 critical streams open until QUIC shutdown.
        MsQuicStream control = await connection.OpenStreamAsync(unidirectional: true, cancellationToken);
        await control.WriteAsync(Settings, cancellationToken);

        using CancellationTokenSource session = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationToken token = session.Token;
        TaskCompletionSource<MsQuicStream> connect = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> peerSettings = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task accepting = ReadClientStreamsAsync(connection, connect, peerSettings, session);
        try
        {
            if (!await peerSettings.Task.WaitAsync(token))
                throw new InvalidDataException("Invalid HTTP/3 connection setup.");
            MsQuicStream selected = await connect.Task.WaitAsync(token);
            byte[]? fields = await ReadRequestHeadersAsync(selected, token);
            if (fields is null || !Http3Qpack.TryReadOffer(fields, out string? offer) ||
                parseOffer(offer) is not { } selectedOffer)
            {
                await selected.WriteAsync(BadRequest, token);
                selected.CompleteWrites();
                return;
            }

            await selected.WriteAsync(Accepted, token);
            MsQuicStream stream = await connection.OpenStreamAsync(unidirectional: false, token);
            byte[] prefix = new byte[16];
            int written = EncodeInteger(prefix, WebTransportStream);
            written += EncodeInteger(prefix.AsSpan(written), selected.Id);
            await stream.WriteAsync(prefix.AsMemory(0, written), token);

            await exchange(stream, selectedOffer.Capability, selectedOffer.Nonce, token);
            stream.CompleteWrites();

            using CancellationTokenSource closing = CancellationTokenSource.CreateLinkedTokenSource(token);
            closing.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                await DrainUntilPeerClosesAsync(selected, closing.Token);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                // The client can still receive a CLOSE_WEBTRANSPORT_SESSION capsule before we end CONNECT.
                await selected.WriteAsync(new byte[] { 0x00, 0x07, 0x68, 0x43, 0x04, 0x00, 0x00, 0x00, 0x00 }, token);
            }
            selected.CompleteWrites();
        }
        finally
        {
            await session.CancelAsync();
            await accepting;
        }
    }

    private static async Task ReadClientStreamsAsync(
        MsQuicConnection connection,
        TaskCompletionSource<MsQuicStream> connect,
        TaskCompletionSource<bool> peerSettings,
        CancellationTokenSource session)
    {
        List<Task> readers = [];
        CancellationToken cancellationToken = session.Token;
        CriticalStreams criticalStreams = new();
        try
        {
            while (true)
            {
                MsQuicStream stream = await connection.AcceptStreamAsync(cancellationToken);
                if (!stream.IsUnidirectional && connect.TrySetResult(stream))
                    continue;

                if (stream.IsUnidirectional && readers.Count < MaxClientUniStreams)
                {
                    readers.Add(ReadClientUniStreamAsync(stream, peerSettings, criticalStreams, session));
                    continue;
                }

                throw new InvalidDataException("The browser exceeded the HTTP/3 stream limit.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancellation ends acceptance before the owning connection is disposed.
        }
        catch
        {
            peerSettings.TrySetResult(false);
            await session.CancelAsync();
            throw;
        }
        finally
        {
            await Task.WhenAll(readers);
        }
    }

    private static async Task ReadClientUniStreamAsync(
        MsQuicStream stream,
        TaskCompletionSource<bool> peerSettings,
        CriticalStreams criticalStreams,
        CancellationTokenSource session)
    {
        CancellationToken cancellationToken = session.Token;
        try
        {
            byte[] scratch = new byte[MaxFramePayloadSize];
            long? streamType = await ReadIntegerAsync(stream, scratch, cancellationToken);
            switch (streamType)
            {
                case 0x00: // HTTP/3 control stream
                    criticalStreams.Add(1);
                    await ReadPeerSettingsAsync(stream, scratch, cancellationToken);
                    peerSettings.TrySetResult(true);
                    while (await ReadIntegerAsync(stream, scratch, cancellationToken) is { } frameType)
                    {
                        if (frameType is SettingsFrame or 0x00 or HeadersFrame or 0x05)
                            throw new InvalidDataException("Unexpected frame on the HTTP/3 control stream.");
                        await SkipPayloadAsync(stream, scratch, cancellationToken);
                    }
                    throw new InvalidDataException("The browser closed its HTTP/3 control stream.");
                case 0x02: // QPACK encoder stream, zero dynamic capacity
                    criticalStreams.Add(2);
                    await DrainQpackStreamAsync(stream, scratch, true, cancellationToken);
                    break;
                case 0x03: // QPACK decoder stream, no dynamic references to acknowledge
                    criticalStreams.Add(4);
                    await DrainQpackStreamAsync(stream, scratch, false, cancellationToken);
                    break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            peerSettings.TrySetResult(false);
            await session.CancelAsync();
            throw;
        }
    }

    private static async Task DrainQpackStreamAsync(Stream stream, byte[] scratch, bool isEncoder, CancellationToken cancellationToken)
    {
        int read;
        while ((read = await stream.ReadAsync(scratch, cancellationToken)) != 0)
        {
            // An encoder may explicitly set its table capacity to 0.
            if (!isEncoder || scratch.AsSpan(0, read).ContainsAnyExcept((byte)0x20))
                throw new InvalidDataException("Unexpected QPACK instructions with static-only headers.");
        }
        throw new InvalidDataException("The browser closed its QPACK stream.");
    }

    private static async Task ReadPeerSettingsAsync(Stream stream, byte[] scratch, CancellationToken cancellationToken)
    {
        if (await ReadIntegerAsync(stream, scratch, cancellationToken) != SettingsFrame)
            throw new InvalidDataException("SETTINGS must be the first HTTP/3 control frame.");

        int length = await ReadLengthAsync(stream, scratch, cancellationToken);
        await stream.ReadExactlyAsync(scratch.AsMemory(0, length), cancellationToken);
        if (!SupportsWebTransport(scratch.AsSpan(0, length)))
            throw new InvalidDataException("Missing or invalid HTTP/3 WebTransport settings.");
    }

    private static bool SupportsWebTransport(ReadOnlySpan<byte> settings)
    {
        HashSet<long> received = [];
        bool datagrams = false, draft02 = false;
        while (!settings.IsEmpty)
        {
            if (!TryReadInteger(ref settings, out long name) || !TryReadInteger(ref settings, out long value) ||
                !received.Add(name) || name is >= 0x02 and <= 0x05)
                return false;

            if (name == 0x33)
                datagrams = value == 1;
            else if (name == EnableWebTransportDraft02)
                draft02 = value == 1;
        }

        return datagrams && draft02;
    }

    private static async Task<byte[]?> ReadRequestHeadersAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] scratch = new byte[MaxFieldSectionSize];
        int skipped = 0;
        while (await ReadIntegerAsync(stream, scratch, cancellationToken) is { } frameType)
        {
            int length = await ReadLengthAsync(stream, scratch, cancellationToken);
            if (frameType == HeadersFrame)
            {
                byte[] fields = new byte[length];
                await stream.ReadExactlyAsync(fields, cancellationToken);
                return fields;
            }

            // Reserved QUIC frame types are invalid as HTTP/3 frame types. Unknown HTTP/3 extensions may precede HEADERS.
            if (frameType is 0x00 or 0x02 or 0x03 or 0x04 or 0x05 or 0x06 or 0x07 or 0x08 or 0x09 or 0x0d or WebTransportStream ||
                (skipped += length + 2) > MaxFieldSectionSize)
                return null;

            await stream.ReadExactlyAsync(scratch.AsMemory(0, length), cancellationToken);
        }

        return null;
    }

    private static async Task DrainUntilPeerClosesAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] scratch = new byte[MaxFramePayloadSize];
        while (await ReadIntegerAsync(stream, scratch, cancellationToken) is { } frameType)
        {
            if (frameType is HeadersFrame or SettingsFrame or WebTransportStream)
                throw new InvalidDataException("Unexpected frame on the WebTransport CONNECT stream.");

            await SkipPayloadAsync(stream, scratch, cancellationToken);
        }
    }

    private static async ValueTask SkipPayloadAsync(Stream stream, byte[] scratch, CancellationToken cancellationToken)
    {
        int length = await ReadLengthAsync(stream, scratch, cancellationToken);
        await stream.ReadExactlyAsync(scratch.AsMemory(0, length), cancellationToken);
    }

    private static async ValueTask<int> ReadLengthAsync(Stream stream, byte[] scratch, CancellationToken cancellationToken)
    {
        long? length = await ReadIntegerAsync(stream, scratch, cancellationToken);
        return length is >= 0 and <= MaxFramePayloadSize
            ? (int)length.Value : throw new InvalidDataException("Invalid HTTP/3 frame length.");
    }

    private static byte[] BuildAccepted()
    {
        ReadOnlySpan<byte> headerName = "sec-webtransport-http3-draft"u8;
        ReadOnlySpan<byte> headerValue = "draft02"u8;
        byte[] frame = new byte[8 + headerName.Length + headerValue.Length];
        frame[0] = (byte)HeadersFrame;
        frame[1] = (byte)(frame.Length - 2);
        // Zero Required Insert Count and Delta Base, static :status 200, and one literal header field.
        frame[4] = 0xd9;
        frame[5] = 0x27;
        frame[6] = (byte)(headerName.Length - 7);
        headerName.CopyTo(frame.AsSpan(7));
        frame[7 + headerName.Length] = (byte)headerValue.Length;
        headerValue.CopyTo(frame.AsSpan(8 + headerName.Length));
        return frame;
    }

    private static byte[] BuildSettings()
    {
        byte[] frame = new byte[64];
        frame[0] = 0x00; // HTTP/3 control stream
        frame[1] = (byte)SettingsFrame;
        int written = 3;
        foreach ((long name, long value) in new (long, long)[]
        {
            (0x01, 0), (0x06, MaxFieldSectionSize), (0x07, 0), // QPACK and field section limits
            (0x08, 1), (0x33, 1), // extended CONNECT and HTTP datagrams
            (EnableWebTransportDraft02, 1),
        })
        {
            written += EncodeInteger(frame.AsSpan(written), name);
            written += EncodeInteger(frame.AsSpan(written), value);
        }

        frame[2] = (byte)(written - 3);
        return frame[..written];
    }

    private static async ValueTask<long?> ReadIntegerAsync(Stream stream, byte[] scratch, CancellationToken cancellationToken)
    {
        int read = await stream.ReadAsync(scratch.AsMemory(0, 1), cancellationToken);
        if (read == 0)
            return null;

        int length = 1 << (scratch[0] >> 6);
        await stream.ReadExactlyAsync(scratch.AsMemory(1, length - 1), cancellationToken);
        long value = scratch[0] & 0x3f;
        for (int i = 1; i < length; i++)
            value = (value << 8) | scratch[i];
        return value;
    }

    private static bool TryReadInteger(ref ReadOnlySpan<byte> encoded, out long value)
    {
        value = 0;
        if (encoded.IsEmpty)
            return false;

        int length = 1 << (encoded[0] >> 6);
        if (encoded.Length < length)
            return false;

        value = encoded[0] & 0x3f;
        for (int i = 1; i < length; i++)
            value = (value << 8) | encoded[i];
        encoded = encoded[length..];
        return true;
    }

    private static int EncodeInteger(Span<byte> output, long value)
    {
        if (value < 1 << 6)
        {
            output[0] = (byte)value;
            return 1;
        }

        if (value < 1 << 14)
        {
            BinaryPrimitives.WriteUInt16BigEndian(output, (ushort)(value | 0x4000));
            return 2;
        }

        if (value < 1 << 30)
        {
            BinaryPrimitives.WriteUInt32BigEndian(output, (uint)value | 0x80000000);
            return 4;
        }

        BinaryPrimitives.WriteUInt64BigEndian(output, (ulong)value | 0xc000000000000000);
        return 8;
    }

    private sealed class CriticalStreams
    {
        private int _seen;

        internal void Add(int stream)
        {
            if ((Interlocked.Or(ref _seen, stream) & stream) != 0)
                throw new InvalidDataException("Duplicate HTTP/3 critical stream.");
        }
    }
}
