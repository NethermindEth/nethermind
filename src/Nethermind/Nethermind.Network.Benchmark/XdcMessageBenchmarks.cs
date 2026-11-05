// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Autofac;
using BenchmarkDotNet.Attributes;
using DotNetty.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Network.P2P.Messages;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.P2P;
using Nethermind.Xdc.Test.Helpers;
using Nethermind.Xdc.Types;

namespace Nethermind.Network.Benchmarks;

/// <summary>
/// Measures serializing XDC consensus messages into a packet the way the packet sender does, and
/// deserializing them from a DotNetty buffer the way the XDC protocol handlers do.
/// </summary>
[MemoryDiagnoser]
public class XdcMessageBenchmarks
{
    public enum Message
    {
        Vote,
        Timeout,
        SyncInfo
    }

    /// <summary>The signatures a certificate carries: two thirds of the 108 XDC mainnet masternodes.</summary>
    private const int CertificateSignatures = 73;

    private const ulong Round = 123_456;
    private const ulong GapNumber = 450;

    private XdcTestBlockchain _chain = null!;
    private Func<int> _serialize = null!;
    private Func<IByteBuffer, P2PMessage> _deserialize = null!;
    private IByteBuffer _body = null!;

    [ParamsAllValues]
    public Message Type { get; set; }

    [GlobalSetup]
    public async Task GlobalSetup()
    {
        _chain = await XdcTestBlockchain.Create(0);
        IMessageSerializationService service = _chain.Container.Resolve<IMessageSerializationService>();
        BlockRoundInfo block = new(TestItem.KeccakA, Round, 98_765_432);

        switch (Type)
        {
            case Message.Vote:
                SetUp(service, new VoteMsg { Vote = new Vote(block, GapNumber, Sign(0)) });
                break;
            case Message.Timeout:
                SetUp(service, new TimeoutMsg { Timeout = new Timeout(Round, Sign(0), GapNumber) });
                break;
            case Message.SyncInfo:
                SetUp(service, new SyncInfoMsg
                {
                    SyncInfo = new SyncInfo(
                        new QuorumCertificate(block, Signatures(), GapNumber),
                        new TimeoutCertificate(Round, Signatures(), GapNumber))
                });
                break;
        }
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _body.Release();
        _chain.Dispose();
    }

    [Benchmark]
    public int Serialize() => _serialize();

    [Benchmark]
    public int Deserialize()
    {
        _body.SetReaderIndex(0);
        using P2PMessage message = _deserialize(_body);
        return message.PacketType;
    }

    private void SetUp<T>(IMessageSerializationService service, T message) where T : P2PMessage
    {
        _serialize = () =>
        {
            IByteBuffer packet = service.ZeroSerialize(message);
            int length = packet.ReadableBytes;
            packet.Release();
            return length;
        };
        _deserialize = body => service.Deserialize<T>(body);

        IByteBuffer packet = service.ZeroSerialize(message);
        packet.SkipBytes(Rlp.LengthOf(message.AdaptivePacketType));
        _body = Unpooled.WrappedBuffer(packet.ReadAllBytesAsArray());
        packet.Release();
    }

    private static Signature[] Signatures()
    {
        Signature[] signatures = new Signature[CertificateSignatures];
        for (int i = 0; i < signatures.Length; i++)
            signatures[i] = Sign(i);
        return signatures;
    }

    private static Signature Sign(int seed) => new EthereumEcdsa(0).Sign(TestItem.PrivateKeyA, Keccak.Compute(BitConverter.GetBytes(seed)));
}
