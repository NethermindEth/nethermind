// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using NonBlocking;

namespace Nethermind.Network.Discovery.UTP;

public partial class UTPStream
{
    private AckInfo _receiverAck_nr = new AckInfo(0, null); // Mutated by receiver only // From spec: c.ack_nr
    // TODO: Use LRU instead. Need a special expiry handling so that the _incomingBufferSize is correct.

    private TaskCompletionSource _dataTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    private ReceiveBuffer _receiveBuffer = new ReceiveBuffer();

    // Used to send data directly to stream without buffering in case where the data came in order.
    private Stream? _directOutput = null;

    public async Task ReadStream(Stream output, CancellationToken token)
    {
        Interlocked.Exchange(ref _directOutput, output);

        bool finished = false;
        while (!finished && _state != ConnectionState.CsEnded)
        {
            if (_logger.IsTrace) _logger.Trace($"R loop.");
            token.ThrowIfCancellationRequested();

            // Could be direct to output path is being used.
            Stream? outputTaken = Interlocked.Exchange(ref _directOutput, null);
            if (outputTaken != null)
            {
                finished = await IngestLoop(outputTaken, token);
                if (finished)
                {
                    break;
                }

                Interlocked.Exchange(ref _directOutput, outputTaken);
            }
            else
            {
                if (_logger.IsTrace) _logger.Trace($"R output taken");
            }

            if (_logger.IsTrace) _logger.Trace($"R wait");
            await Task.WhenAny(_dataTcs.Task, Task.Delay(100, token));
            _dataTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private async Task<bool> IngestLoop(Stream output, CancellationToken token)
    {
        ushort curAck = _receiverAck_nr.seq_nr;
        long packetIngested = 0;
        bool finished = false;

        while (_receiveBuffer.TryRemove(UTPUtil.WrappedAddOne(curAck), out ArraySegment<byte>? packetData))
        {
            curAck++;
            if (_logger.IsTrace) _logger.Trace($"R ingest {curAck}");

            if (packetData == null)
            {
                if (_logger.IsTrace) _logger.Trace($"R final packet found {curAck}");

                // Its unclear if this is a bidirectional stream should the sending get aborted also or not.
                output.Close();

                finished = true;
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
            _arrayPool.Return(packetData.Value.Array!);
        }

        // Assembling ack.
        byte[]? selectiveAck = _receiveBuffer.CompileSelectiveAckBitset(curAck);

        _receiverAck_nr = new AckInfo(curAck, selectiveAck);
        if (_logger.IsTrace) _logger.Trace($"R ack set to {curAck}. Bufsixe is {_receiveBuffer.Size}");

        await SendStatePacket(token);

        return finished;
    }

    private bool ShouldHandlePacket(UTPPacketHeader meta, ReadOnlySpan<byte> data)
    {
        ushort receiverAck = _receiverAck_nr.seq_nr;

        if (data.Length > MAX_PAYLOAD_SIZE)
        {
            return false;
        }

        // Got previous resend data probably
        if (UTPUtil.IsLess(meta.SeqNumber, receiverAck))
        {
            if (_logger.IsTrace) _logger.Trace($"less than ack {meta.SeqNumber}");
            return false;
        }

        // What if the sequence number is way too high. The threshold is estimated based on the window size
        // and the payload size
        if (!UTPUtil.IsLess(meta.SeqNumber, (ushort)(receiverAck + RECEIVE_WINDOW_SIZE / PayloadSize)))
        {
            return false;
        }

        // No space in buffer UNLESS its the next needed sequence, in which case, we still process it, otherwise
        if (_receiveBuffer.Size + data.Length > RECEIVE_WINDOW_SIZE &&
            meta.SeqNumber != UTPUtil.WrappedAddOne(receiverAck))
        {
            // Attempt to clean incoming buffer.
            // Ideally, it just use an LRU instead.
            foreach (var keptBuffer in _receiveBuffer.GetKeys())
            {
                // Can happen sometime, the buffer is written at the same time as it is being processed so the ack
                // is not updated yet at that point.
                if (UTPUtil.IsLess(keptBuffer, receiverAck))
                {
                    if (_receiveBuffer.TryRemove(keptBuffer, out ArraySegment<byte>? mem))
                    {
                    }
                }
            }

            if (_receiveBuffer.Size + data.Length > RECEIVE_WINDOW_SIZE)
            {
                if (_logger.IsTrace)
                {
                    _logger.Trace($"Receive buffer full for seq {meta.SeqNumber}");
                    _logger.Trace("The nums " + string.Join(", ", _receiveBuffer.GetKeys()));
                }

                return false;
            }
        }

        return true;
    }

    private Task OnStData(UTPPacketHeader packetHeader, ReadOnlySpan<byte> data, CancellationToken token)
    {
        if (ShouldHandlePacket(packetHeader, data))
        {
            if (UTPUtil.WrappedAddOne(_receiverAck_nr.seq_nr) == packetHeader.SeqNumber)
            {
                // Directly send the data to stream without buffering
                bool wasWritten = false;
                wasWritten = SendDataToOutputDirectly(packetHeader, data, wasWritten);

                if (wasWritten)
                {
                    if (_receiveBuffer.ContainsKey((ushort)(packetHeader.SeqNumber + 1)))
                    {
                        _dataTcs.TrySetResult();

                        return Task.CompletedTask;
                    }
                    else
                    {
                        return SendStatePacket(token);
                    }
                }
            }


            ArraySegment<byte> buffer = _arrayPool.Rent(data.Length);
            ArraySegment<byte> segment = buffer[..data.Length];
            data.CopyTo(segment);

            if (_receiveBuffer.TryAdd(packetHeader.SeqNumber, segment))
            {
                if (_logger.IsTrace) _logger.Trace($"R Receive buffer set {packetHeader.SeqNumber}");

                _dataTcs.TrySetResult();
            }
            else
            {
                _arrayPool.Return(segment.Array!);
            }
        }

        return Task.CompletedTask;
    }

    private bool SendDataToOutputDirectly(UTPPacketHeader packetHeader, ReadOnlySpan<byte> data, bool wasWritten)
    {
        Stream? outputTaken = Interlocked.Exchange(ref _directOutput, null);
        if (outputTaken == null) return wasWritten;

        ushort curAck = _receiverAck_nr.seq_nr;
        if (UTPUtil.WrappedAddOne(curAck) == packetHeader.SeqNumber) // Check again
        {
            outputTaken.Write(data);
            wasWritten = true;

            if (_receiveBuffer.TryRemove(packetHeader.SeqNumber, out var bufferSegment))
            {
                _logger.Trace($"Seq number {packetHeader.SeqNumber} was in receive buffer");
            }

            curAck++;
            byte[]? selectiveAck = _receiveBuffer.CompileSelectiveAckBitset(curAck);
            _receiverAck_nr = new AckInfo(curAck, selectiveAck);
            if (_logger.IsTrace) _logger.Trace($"R ack set to {curAck} in direct write.");
        }

        Interlocked.Exchange(ref _directOutput, outputTaken);
        return wasWritten;
    }

    private void OnStFin(UTPPacketHeader packetHeader, CancellationToken token)
    {
        if (ShouldHandlePacket(packetHeader, ReadOnlySpan<byte>.Empty))
        {
            _receiveBuffer.TryAdd(packetHeader.SeqNumber, null);
            _dataTcs.TrySetResult();
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
