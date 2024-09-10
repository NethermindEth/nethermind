// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.Discovery.UTP;

public partial class UTPStream
{
    private readonly TaskCompletionSource<UTPPacketHeader> _synTcs = new();
    private TaskCompletionSource<UTPPacketHeader>? _stateTcs = null;

    public async Task InitiateHandshake(CancellationToken token, ushort? synConnectionId = null)
    {
        // TODO: MTU probe
        // TODO: in libp2p this is added to m_outbuf, and therefore part of the
        // resend logic
        _seq_nr = 1;
        UTPPacketHeader synHeader = CreateBaseHeader(UTPPacketType.StSyn);
        synHeader.ConnectionId = synConnectionId ?? connectionId;
        _seq_nr++;
        _state = ConnectionState.CsSynSent;

        UTPPacketHeader firstStateHeader;
        _stateTcs = new TaskCompletionSource<UTPPacketHeader>();

        while (true)
        {
            token.ThrowIfCancellationRequested();
            // Send the syn
            await peer.ReceiveMessage(synHeader, ReadOnlySpan<byte>.Empty, token);

            if (await Task.WhenAny(Task.Delay(100, token), _stateTcs.Task) == _stateTcs.Task)
            {
                firstStateHeader = await _stateTcs.Task;
                _stateTcs = null;
                break;
            }
        }

        // The seqNumber need to Subtract 1.
        // This is because the first StData would have the same sequence number
        // And we would skip it if we don't subtract 1.
        // Returning this ack seems to be fine.
        _receiverAck_nr = new AckInfo((ushort)(firstStateHeader.SeqNumber - 1), null); // From spec: c.ack_nr
        _state = ConnectionState.CsConnected;
        await SendStatePacket(token);
    }

    public async Task HandleHandshake(CancellationToken token)
    {
        UTPPacketHeader packageHeader;
        await using (token.Register(() => { _synTcs.TrySetCanceled(); }))
        {
            packageHeader = await _synTcs.Task;
        }

        _seq_nr = (ushort)Random.Shared.Next(); // From spec: c.seq_nr
        _receiverAck_nr = new AckInfo(packageHeader.SeqNumber, null); // From spec: c.ack_nr
        _state = ConnectionState.CsSynRecv; // From spec: c.state
        await SendStatePacket(token);
    }
}
