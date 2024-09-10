// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NonBlocking;

namespace Nethermind.Network.Discovery.UTP;

public partial class UTPStream
{
    private AckInfo _receiverAck_nr = new AckInfo(0, null); // Mutated by receiver only // From spec: c.ack_nr

    // TODO: Use LRU instead. Need a special expiry handling so that the _incomingBufferSize is correct.
    private readonly ConcurrentDictionary<ushort, Memory<byte>?> _receiveBuffer = new();

    private int _incomingBufferSize = 0;

    public async Task ReadStream(Stream output, CancellationToken token)
    {
        bool finished = false;
        while (!finished && _state != ConnectionState.CsEnded)
        {
            if (_logger.IsTrace) _logger.Trace($"R loop.");
            token.ThrowIfCancellationRequested();

            ushort curAck = _receiverAck_nr.seq_nr;
            long packetIngested = 0;

            while (_receiveBuffer.TryRemove(UTPUtil.WrappedAddOne(curAck), out Memory<byte>? packetData))
            {
                curAck++;
                if (_logger.IsTrace) _logger.Trace($"R ingest {curAck}");

                if (packetData == null)
                {
                    // Its unclear if this is a bidirectional stream should the sending get aborted also or not.
                    finished = true;
                    output.Close();
                    break;
                }

                // Periodically send out ack.
                packetIngested++;
                if (packetIngested > 4)
                {
                    _receiverAck_nr = new AckInfo(curAck, null);
                    await SendStatePacket(token);
                    packetIngested = 0;
                }

                await output.WriteAsync(packetData.Value, token);
                Interlocked.Add(ref _incomingBufferSize, -packetData.Value.Length);
            }

            // Assembling ack.
            byte[]? selectiveAck = UTPUtil.CompileSelectiveAckBitset(curAck, _receiveBuffer);

            _receiverAck_nr = new AckInfo(curAck, selectiveAck);
            if (_logger.IsTrace) _logger.Trace($"R ack set to {curAck}. Bufsixe is {_receiveBuffer.Count()}");

            await SendStatePacket(token);

            if (_logger.IsTrace) _logger.Trace($"R wait");
            await WaitForNewMessage(100);
        }
    }

    private bool ShouldNotHandleSequence(UTPPacketHeader meta, ReadOnlySpan<byte> data)
    {
        ushort receiverAck = _receiverAck_nr.seq_nr;

        if (data.Length > MAX_PAYLOAD_SIZE)
        {
            return true;
        }

        // Got previous resend data probably
        if (UTPUtil.IsLess(meta.SeqNumber, receiverAck))
        {
            if (_logger.IsTrace) _logger.Trace($"less than ack {meta.SeqNumber}");
            return true;
        }

        // What if the sequence number is way too high. The threshold is estimated based on the window size
        // and the payload size
        if (!UTPUtil.IsLess(meta.SeqNumber, (ushort)(receiverAck + RECEIVE_WINDOW_SIZE / PayloadSize)))
        {
            return true;
        }

        // No space in buffer UNLESS its the next needed sequence, in which case, we still process it, otherwise
        if (_incomingBufferSize + data.Length > RECEIVE_WINDOW_SIZE &&
            meta.SeqNumber != UTPUtil.WrappedAddOne(receiverAck))
        {
            // Attempt to clean incoming buffer.
            // Ideally, it just use an LRU instead.
            List<ushort> keptBuffers = _receiveBuffer.Select(kv => kv.Key).ToList();
            foreach (var keptBuffer in keptBuffers)
            {
                // Can happen sometime, the buffer is written at the same time as it is being processed so the ack
                // is not updated yet at that point.
                if (UTPUtil.IsLess(keptBuffer, receiverAck))
                {
                    if (_receiveBuffer.TryRemove(keptBuffer, out Memory<byte>? mem))
                    {
                        if (mem != null) _incomingBufferSize -= mem.Value.Length;
                    }
                }
            }

            if (_incomingBufferSize + data.Length > RECEIVE_WINDOW_SIZE)
            {
                if (_logger.IsTrace)
                {
                    _logger.Trace($"Receive buffer full for seq {meta.SeqNumber}");
                    var it = _receiveBuffer.Select(kv => kv.Key).ToList();
                    _logger.Trace("The nums " + string.Join(", ", it));
                }

                return true;
            }
        }

        return false;
    }

    private void OnStData(UTPPacketHeader packageHeader, ReadOnlySpan<byte> data)
    {
        if (!ShouldNotHandleSequence(packageHeader, data))
        {
            // TODO: Fast path without going to receive buffer?
            Interlocked.Add(ref _incomingBufferSize, data.Length);
            _receiveBuffer[packageHeader.SeqNumber] = data.ToArray();
        }
    }

    private void OnStFin(UTPPacketHeader packageHeader, CancellationToken token)
    {
        if (!ShouldNotHandleSequence(packageHeader, ReadOnlySpan<byte>.Empty))
        {
            _receiveBuffer[packageHeader.SeqNumber] = null;
        }

        if (_state == ConnectionState.CsEnded)
        {
            // Special case, once FIN is first received, the reader stream thread would have exited after its ACK.
            // In the case where the last ACK is lost, the sender send FIN again, so this need to happen.
            _ = SendStatePacket(token);
        }
    }

    private async Task SendStatePacket(CancellationToken token)
    {
        UTPPacketHeader stateHeader = CreateBaseHeader(UTPPacketType.StState);
        await peer.ReceiveMessage(stateHeader, ReadOnlySpan<byte>.Empty, token);
    }

}
