// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using DotNetty.Buffers;
using Nethermind.Consensus.Qbft.Messages;
using Nethermind.Network;
using Nethermind.Network.P2P.Messages;

namespace Nethermind.Consensus.Qbft.P2P;

/// <summary>An <c>istanbul/100</c> frame: the consensus message RLP is carried opaquely and decoded by the state machine.</summary>
public abstract class QbftWireMessage(byte[] data) : P2PMessage
{
    public const string ProtocolName = "istanbul";

    public byte[] Data { get; } = data;
    public override string Protocol => ProtocolName;

    public override string ToString() => $"{QbftMessageCode.Name(PacketType)}({Data.Length} bytes)";

    public static QbftWireMessage Create(int code, byte[] data) => code switch
    {
        QbftMessageCode.Proposal => new ProposalWireMessage(data),
        QbftMessageCode.Prepare => new PrepareWireMessage(data),
        QbftMessageCode.Commit => new CommitWireMessage(data),
        QbftMessageCode.RoundChange => new RoundChangeWireMessage(data),
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Not a QBFT message code."),
    };
}

public sealed class ProposalWireMessage(byte[] data) : QbftWireMessage(data)
{
    public override int PacketType => QbftMessageCode.Proposal;
}

public sealed class PrepareWireMessage(byte[] data) : QbftWireMessage(data)
{
    public override int PacketType => QbftMessageCode.Prepare;
}

public sealed class CommitWireMessage(byte[] data) : QbftWireMessage(data)
{
    public override int PacketType => QbftMessageCode.Commit;
}

public sealed class RoundChangeWireMessage(byte[] data) : QbftWireMessage(data)
{
    public override int PacketType => QbftMessageCode.RoundChange;
}

/// <summary>Copies the opaque consensus RLP to and from the frame.</summary>
public abstract class QbftWireMessageSerializer<T>(Func<byte[], T> create) : IZeroMessageSerializer<T> where T : QbftWireMessage
{
    public void Serialize(IByteBuffer byteBuffer, T message)
    {
        byteBuffer.EnsureWritable(message.Data.Length);
        byteBuffer.WriteBytes(message.Data);
    }

    public T Deserialize(IByteBuffer byteBuffer)
    {
        byte[] data = new byte[byteBuffer.ReadableBytes];
        byteBuffer.ReadBytes(data);
        return create(data);
    }
}

public sealed class ProposalWireMessageSerializer() : QbftWireMessageSerializer<ProposalWireMessage>(static data => new(data));
public sealed class PrepareWireMessageSerializer() : QbftWireMessageSerializer<PrepareWireMessage>(static data => new(data));
public sealed class CommitWireMessageSerializer() : QbftWireMessageSerializer<CommitWireMessage>(static data => new(data));
public sealed class RoundChangeWireMessageSerializer() : QbftWireMessageSerializer<RoundChangeWireMessage>(static data => new(data));
