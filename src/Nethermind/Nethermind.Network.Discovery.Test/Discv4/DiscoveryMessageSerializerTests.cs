// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net;
using System.Linq;
using DotNetty.Buffers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery.Discv4.Messages;
using Nethermind.Network.Enr;
using Nethermind.Network.Test;
using Nethermind.Network.Test.Builders;
using Nethermind.Stats.Model;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test.Discv4;

[Parallelizable(ParallelScope.Self)]
public class DiscoveryMessageSerializerTests
{
    private readonly PrivateKey _privateKey =
        new("49a7b37aa6f6645917e7b807e9d1c00d4fa71f18343b0d4122a4d2df64dd6fee");

    //private readonly PrivateKey _farPrivateKey = new PrivateKey("3a1076bf45ab87712ad64ccb3b10217737f7faacbf2872e88fdd9a537d8fe266");
    private readonly IPEndPoint _farAddress;
    private readonly IPEndPoint _nearAddress;
    private readonly IMessageSerializationService _messageSerializationService;
    private readonly ITimestamper _timestamper;
    private readonly PooledByteBufferAllocator _leakDetectionAllocator = PooledBufferLeakDetector.CreateAllocator();

    public DiscoveryMessageSerializerTests()
    {
        INetworkConfig networkConfig = new NetworkConfig();
        networkConfig.ExternalIp = "99.10.10.66";
        networkConfig.LocalIp = "10.0.0.5";
        _farAddress = new IPEndPoint(IPAddress.Parse("192.168.1.2"), 1);
        _nearAddress = new IPEndPoint(IPAddress.Parse(networkConfig.LocalIp), networkConfig.DiscoveryPort);
        _messageSerializationService = Build.A.SerializationService().WithDiscovery(_privateKey).TestObject;
        _timestamper = Timestamper.Default;
    }

    [Test]
    public void PingMessageTest()
    {
        using PooledBufferLeakDetector detector = new(_leakDetectionAllocator);
        PingMsg message =
            new(_privateKey.PublicKey, 60 + _timestamper.UnixTime.MillisecondsLong, _farAddress, _nearAddress,
                new byte[32])
            { FarAddress = _farAddress };

        using DisposableByteBuffer data = _messageSerializationService.ZeroSerialize(message, detector.Allocator).AsDisposable();
        PingMsg deserializedMessage = _messageSerializationService.Deserialize<PingMsg>(data);

        byte[] expectedPingMdc =
            Bytes.FromHexString("0xf8c61953f3b94a91aefe611e61dd74fe26aa5c969d9f29b7e063e6169171a772");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(deserializedMessage.MsgType, Is.EqualTo(message.MsgType));
            Assert.That(deserializedMessage.FarPublicKey, Is.EqualTo(message.FarPublicKey));
            Assert.That(deserializedMessage.ExpirationTime, Is.EqualTo(message.ExpirationTime));

            Assert.That(deserializedMessage.SourceAddress, Is.EqualTo(message.FarAddress));
            Assert.That(deserializedMessage.DestinationAddress, Is.EqualTo(message.DestinationAddress));
            Assert.That(deserializedMessage.SourceAddress, Is.EqualTo(message.SourceAddress));
            Assert.That(deserializedMessage.Version, Is.EqualTo(message.Version));

            Assert.That(expectedPingMdc, Is.Not.Null);
        }
    }

    [Test]
    public void PingMessage_Allows_Endpoint_Port_Zero()
    {
        PingMsg message =
            new(_privateKey.PublicKey, 60 + _timestamper.UnixTime.MillisecondsLong, new IPEndPoint(_farAddress.Address, 0), _nearAddress,
                new byte[32])
            { FarAddress = _farAddress };

        using DisposableByteBuffer data = _messageSerializationService.ZeroSerialize(message).AsDisposable();
        PingMsg deserializedMessage = _messageSerializationService.Deserialize<PingMsg>(data);

        Assert.That(deserializedMessage.SourceAddress.Port, Is.Zero);
    }

    [Test]
    public void PingMessage_UsesUdpPortWhenTcpPortIsZero()
    {
        string devp2pDiscoveryOnlyPing =
            "24fca4f142312eb2c8b850295cf1c7b3dbbcac49c79a4e1dbc84bffde5e3605bd3c81c2d313f3ac8f293ead68f0efc76d78033279923216da1ad5bc0356f0d0e4dab24602f48452ac037d8a7291260c6999fc9f65b85095fb5f6abc4ed1f49020101e104c9847f00000182f2bb80c9847f000001827d6580846a17400086019e6ad1b545";

        PingMsg ping = _messageSerializationService.Deserialize<PingMsg>(Bytes.FromHexString(devp2pDiscoveryOnlyPing));

        Assert.That(ping.SourceAddress, Is.EqualTo(new IPEndPoint(IPAddress.Loopback, 62139)));
        Assert.That(ping.DestinationAddress, Is.EqualTo(new IPEndPoint(IPAddress.Loopback, 32101)));
    }

    [Test]
    public void PongMessageTest()
    {
        using PooledBufferLeakDetector detector = new(_leakDetectionAllocator);
        PongMsg message =
            new(_privateKey.PublicKey, 60 + _timestamper.UnixTime.MillisecondsLong, TestItem.KeccakA.ValueHash256)
            {
                FarAddress = _farAddress
            };

        using DisposableByteBuffer data = _messageSerializationService.ZeroSerialize(message, detector.Allocator).AsDisposable();
        PongMsg deserializedMessage = _messageSerializationService.Deserialize<PongMsg>(data);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(deserializedMessage.MsgType, Is.EqualTo(message.MsgType));
            Assert.That(deserializedMessage.FarPublicKey, Is.EqualTo(message.FarPublicKey));
            Assert.That(deserializedMessage.ExpirationTime, Is.EqualTo(message.ExpirationTime));
            Assert.That(deserializedMessage.PingMdc, Is.EqualTo(message.PingMdc));
            Assert.That(deserializedMessage.EnrSequence, Is.Null);
        }
    }

    [Test]
    public void Pong_with_enr_sequence_there_and_back()
    {
        PongMsg pongMsg = new(TestItem.PublicKeyA, long.MaxValue, TestItem.KeccakA.ValueHash256, 3)
        {
            FarAddress = _farAddress
        };

        using DisposableByteBuffer serialized = _messageSerializationService.ZeroSerialize(pongMsg).AsDisposable();
        pongMsg = _messageSerializationService.Deserialize<PongMsg>(serialized);

        Assert.That(pongMsg.EnrSequence, Is.EqualTo(3));
    }

    [Test]
    public void Pong_with_enr_sequence_and_extra_tail_reads_enr_sequence()
    {
        PongMsg pongMsg = new(TestItem.PublicKeyA, long.MaxValue, TestItem.KeccakA.ValueHash256, 3)
        {
            FarAddress = _farAddress
        };
        byte[] serialized = CreatePongWithExtraTail(pongMsg, [4, 5, 6]);

        pongMsg = _messageSerializationService.Deserialize<PongMsg>(serialized);

        Assert.That(pongMsg.EnrSequence, Is.EqualTo(3));
    }

    [Test]
    public void Ping_with_enr_there_and_back()
    {
        PingMsg pingMsg = new(TestItem.PublicKeyA, long.MaxValue, new IPEndPoint(TestItem.IPEndPointA.Address, 30303), new IPEndPoint(TestItem.IPEndPointB.Address, 30303), new byte[32]);
        pingMsg.EnrSequence = 3;
        using DisposableByteBuffer serialized = _messageSerializationService.ZeroSerialize(pingMsg).AsDisposable();
        pingMsg = _messageSerializationService.Deserialize<PingMsg>(serialized);
        Assert.That(pingMsg.EnrSequence, Is.EqualTo(3));
    }

    [Test]
    public void Enr_request_there_and_back()
    {
        EnrRequestMsg msg = new(TestItem.PublicKeyA, long.MaxValue);
        using DisposableByteBuffer serialized = _messageSerializationService.ZeroSerialize(msg).AsDisposable();
        EnrRequestMsg deserialized = _messageSerializationService.Deserialize<EnrRequestMsg>(serialized);
        Assert.That(deserialized.ExpirationTime, Is.EqualTo(msg.ExpirationTime));
        Assert.That(_privateKey.PublicKey, Is.EqualTo(deserialized.FarPublicKey));
    }

    [Test]
    public void Enr_request_hash_does_not_alias_input_buffer()
    {
        EnrRequestMsg msg = new(TestItem.PublicKeyA, long.MaxValue);
        using DisposableByteBuffer serialized = _messageSerializationService.ZeroSerialize(msg).AsDisposable();
        byte[] packet = serialized.ReadAllBytesAsArray();
        Hash256 expectedHash = new(packet.AsSpan(0, 32));
        Assert.That(expectedHash, Is.EqualTo(new Hash256("0x64c2e38e89cdfca030166b7a271c301dd77cf043172966ab112d97fc3430fa16")));

        using DisposableByteBuffer input = Unpooled.WrappedBuffer(packet).AsDisposable();
        EnrRequestMsg deserialized = _messageSerializationService.Deserialize<EnrRequestMsg>(input);
        Array.Clear(packet);

        Assert.That(deserialized.Hash, Is.Not.Null);
        Assert.That(new Hash256(deserialized.Hash!.Value), Is.EqualTo(expectedHash));
    }

    [Test]
    public void Enr_response_there_and_back()
    {
        using PooledBufferLeakDetector detector = new(_leakDetectionAllocator);
        EnrResponseMsg msg = BuildEnrResponse(_privateKey.CompressedPublicKey);

        using DisposableByteBuffer serialized = _messageSerializationService.ZeroSerialize(msg, detector.Allocator).AsDisposable();
        EnrResponseMsg deserialized = _messageSerializationService.Deserialize<EnrResponseMsg>(serialized);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(deserialized.NodeRecord.EnrSequence, Is.EqualTo(msg.NodeRecord.EnrSequence));
            Assert.That(deserialized.RequestKeccak, Is.EqualTo(msg.RequestKeccak));
            Assert.That(deserialized.NodeRecord.Signature, Is.EqualTo(msg.NodeRecord.Signature));
        }
    }

    [Test]
    public void Enr_response_deserialize_does_not_leak_buffer_on_invalid_signature()
    {
        // ENR with mismatched signature: Secp256K1 entry uses differentKey, but ENR is
        // signed with _privateKey. The outer Discovery envelope is valid, but the inner
        // ENR signature verification fails because the recovered signer doesn't match.
        using PooledBufferLeakDetector detector = new(_leakDetectionAllocator);
        PrivateKey differentKey = new("3a1076bf45ab87712ad64ccb3b10217737f7faacbf2872e88fdd9a537d8fe266");
        EnrResponseMsg msg = BuildEnrResponse(differentKey.CompressedPublicKey);
        using DisposableByteBuffer serialized = _messageSerializationService.ZeroSerialize(msg, detector.Allocator).AsDisposable();

        Assert.That(() => _messageSerializationService.Deserialize<EnrResponseMsg>(serialized), Throws.TypeOf<NetworkingException>().And.Matches<NetworkingException>(ex => ex.Message.Contains("Invalid ENR signature")));
    }

    [Test]
    public void Ping_with_node_id_address_is_rejected()
    {
        string message =
            "24917ba09abd910901145714c396ade5679735cf9f7796f7576439a13e6e5fc4466988ce6936ac208c4e513d1d0caa0e93160bd5ebdb10ec09df80c95e8d6c0c32d0f154d5bed121c028596f02cf974d50454e3b0ff2d0973deeb742e14e087a0004f9058df90585f8578e3138352e3136392e3233312e343982c35082c350b84036ae45a29ae5d99c0cdb78794fa439f180b13f595a2acd82bf7c0541c0238ea33f5fec5c16bfd7b851449ae0c1e8cbf1502342425fdb65face5eac705d6416a2f8568d3138352e37382e36362e31323282765f82765fb84034dedd0befcd6beb1acc3b7138246b08bd6056ec608b84983d5ce202e1af83c8cf8121063df26d7135536c1636aaa782b63e9f889f4c97172c3a4e5b09a4d721f8558c3138352e31372e34332e343382c35082c350b84035fb64bf23d73efa210bd9299e39d1b33bc189389a98c2d9998394df8d3b6f2e94cad1c36e8a00e3050d60394a8bd0febdfcd22b8127edc71ee7fd28bd2a8f8df8578e3136372e39392e3133382e32313482520b82520bb8403d9ca5956b38557aba991e31cf510d4df641dce9cc26bfeb7de082f0c07abb6ede3a58410c8f249dabeecee4ad3979929ac4c7c496ad20b8cfdd061b7401b4f5f8578e31332e3132352e3131302e323336820bd9820bd9b8402b40b3299cc4680a191c67564ab877face479b8d0c06e17946c68832dd2f17d814fda0258b941f0bd54358d2fc7b1bb5018197114ee0054e3dce576ce6567174f8568d36392e3136342e3231352e313482765f82765fb8402d11cfe93f8caf5aa9c4a90128ddc61350f585d5b0a14c137c18b12f21c4c5d0d28e440601ace627498e8d19903f0676b18ea210c80b528b14afb57edcbcee12f8578e3138352e3136392e3233302e363082c35082c350b840209dc79ec6937114afcefe9ca604a2b62a5313181cfa517298c386030cc421b23feb84b82ab024e983b902c410f936bacc55d88aee3d819b0e7bfcf7d285d28cf8548b31332e3232392e312e3339827597827597b84023c049cfc57345656e1fc9924a121859723a6cc3adea62e6ddd5c15f4b04b8ed044a29cd188d7c26d798da93aa828b911d65e37914935c34f92c9d6f671b3e7bf8588f3232302e3131372e3135342e313431820400820400b8401eecac5177f517a00373f5918f373fb3aa347c87dba678b58a09c0fe73bf578c2447e8f1d6e8f92c3248514d55157398e4909d36d42840f2c70f98120fd2da92f8558c3132322e31312e34372e393582c4a782c4a7b84011e4bc809f78687ac4cceff4ac574cda15010ef20d657d296fc0daf696dd8e80178c3aa64a02db51eecd7c6e05513d49dbbc0824df0fbb53fbbef07e81335926f8588f3138352e3135332e3139382e32303382c35082c350b84014ce698fb9ebd75a7ee6ab123b87f10e041e8bad7b290e5caddd7b75e3f477661923d7ad303a9a97042eb9b1657dc0848411d7b58287d8655881971ab25fd965f8588f3230372e3135342e3231382e313139825209825209b8400ba6b9f606a43a95edc6247cdb1c1e105145817be7bcafd6b2c0ba15d58145f0dc1a194f70ba73cd6f4cdd6864edc7687f311254c7555cc32e4d45aeb1b80416f8558c3133372e37342e3134342e3482765f82765fb8401083237e8c12e17153970639079096ad87bf0f534c84c131e7da339d70282e81919e1dbe02415453464849c72e9deb6c784997de2c4aa175282f84ffcd4b79f3f8568d35312e3134302e3132372e393582765f82765fb8400efa939a67ba0d177143c26cad8bc86a29cf7456af8132ddcfb956ab470173981fcf1d08fdbaa14ec4aa9e240880115406f533911f833545809704f5fff6b89ef8568d3230372e3134382e32372e3834827661827661b84003944d60046265f36aa333373e36604570029dc0dc9518d4226ba2037ae33cc2c5dd6940ee22c3ce85ad8a3c5791f81b73530dbe77aacd22d9e25593c4a354c8f8568d36342e33342e3233312e31343082765f82765fb8401feb66dd6b901ba73614a5bb7946426e1d9f0bf3df8368c3d80b47c6983b0f82d0fc360d422e79d67e81faaa0b37ec39c84f962179805dc85357fdb27e282c47845b867da0";
        Assert.Throws<NetworkingException>(() => _messageSerializationService.Deserialize<NeighborsMsg>(Bytes.FromHexString(message)));
    }

    [Test]
    [Ignore("Is it some v5 message?")]
    public void Can_deserialize_the_strange_message()
    {
        string message =
            "46261b14e3783640a24a652205a6fb7afdb94855c07bb9559777d98e54e51562442219fd8673b1a6aef0f4eaa3b1ed39695839775ed634e9b58d56bde116cd1c63e88d9e953bf05b24e9871de8ea630d98f812bdf176b712b7f9ba2c4db242170102f6c3808080cb845adc681b827668827668a070dfc96ee3da9864524f1f0214a35d46b56093f020ee588a05fafe1323335ce7845cc60fd7";
        PongMsg deserializedMessage =
            _messageSerializationService.Deserialize<PongMsg>(Bytes.FromHexString(message));
        Assert.That(deserializedMessage, Is.Not.Null);
    }

    [Test]
    public void FindNodeMessageTest()
    {
        using PooledBufferLeakDetector detector = new(_leakDetectionAllocator);
        FindNodeMsg message =
            new(_privateKey.PublicKey, 60 + _timestamper.UnixTime.MillisecondsLong, new byte[] { 1, 2, 3 })
            {
                FarAddress = _farAddress
            };

        using DisposableByteBuffer data = _messageSerializationService.ZeroSerialize(message, detector.Allocator).AsDisposable();
        FindNodeMsg deserializedMessage = _messageSerializationService.Deserialize<FindNodeMsg>(data);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(deserializedMessage.MsgType, Is.EqualTo(message.MsgType));
            Assert.That(deserializedMessage.FarPublicKey, Is.EqualTo(message.FarPublicKey));
            Assert.That(deserializedMessage.ExpirationTime, Is.EqualTo(message.ExpirationTime));

            Assert.That(deserializedMessage.SearchedNodeId, Is.EqualTo(message.SearchedNodeId));
        }
    }

    [Test]
    public void FindNodeMessage_Rejects_Oversized_Node_Id()
    {
        FindNodeMsg message = new(_privateKey.PublicKey, 60 + _timestamper.UnixTime.MillisecondsLong, new byte[65])
        {
            FarAddress = _farAddress
        };

        using DisposableByteBuffer data = _messageSerializationService.ZeroSerialize(message).AsDisposable();
        Assert.Throws<RlpLimitException>(() => _messageSerializationService.Deserialize<FindNodeMsg>(data));
    }

    [Test]
    public void NeighborsMessageTest()
    {
        using PooledBufferLeakDetector detector = new(_leakDetectionAllocator);
        NeighborsMsg message =
            new(_privateKey.PublicKey, 60 + _timestamper.UnixTime.MillisecondsLong,
                new[]
                {
                    new Node(TestItem.PublicKeyA, "192.168.1.2", 1),
                    new Node(TestItem.PublicKeyB, "192.168.1.3", 2),
                    new Node(TestItem.PublicKeyC, "192.168.1.4", 3)
                })
            {
                FarAddress = _farAddress
            };

        using DisposableByteBuffer data = _messageSerializationService.ZeroSerialize(message, detector.Allocator).AsDisposable();
        NeighborsMsg deserializedMessage = _messageSerializationService.Deserialize<NeighborsMsg>(data);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(deserializedMessage.MsgType, Is.EqualTo(message.MsgType));
            Assert.That(deserializedMessage.FarPublicKey, Is.EqualTo(message.FarPublicKey));
            Assert.That(deserializedMessage.ExpirationTime, Is.EqualTo(message.ExpirationTime));

            for (int i = 0; i < message.Nodes.Count; i++)
            {
                Assert.That(deserializedMessage.Nodes[i].Host, Is.EqualTo(message.Nodes[i].Host));
                Assert.That(deserializedMessage.Nodes[i].Port, Is.EqualTo(message.Nodes[i].Port));
                Assert.That(deserializedMessage.Nodes[i].IdHash, Is.EqualTo(message.Nodes[i].IdHash));
                Assert.That(deserializedMessage.Nodes[i], Is.EqualTo(message.Nodes[i]));
            }
        }
    }

    [Test]
    public void NeighborsMessage_Drops_Empty_List_Node_Entries()
    {
        // A misbehaving peer can encode a node entry as an RLP empty list (0xc0);
        // such entries must be skipped instead of reaching consumers.
        byte[] ip = [192, 168, 1, 2];
        byte[] id = TestItem.PublicKeyA.Bytes;
        const int port = 30303;
        long expirationTime = 60 + _timestamper.UnixTime.MillisecondsLong;

        int nodeContentLength = Rlp.LengthOf(ip) + 2 * Rlp.LengthOf(port) + Rlp.LengthOf(id);
        int nodesContentLength = Rlp.LengthOfSequence(nodeContentLength) + Rlp.OfEmptyList.Bytes.Length;
        int contentLength = Rlp.LengthOfSequence(nodesContentLength) + Rlp.LengthOf(expirationTime);

        RlpStream stream = new(Rlp.LengthOfSequence(contentLength));
        stream.StartSequence(contentLength);
        stream.StartSequence(nodesContentLength);
        stream.StartSequence(nodeContentLength);
        stream.Encode(ip);
        stream.Encode(port);
        stream.Encode(port);
        stream.Encode(id);
        stream.Encode(Rlp.OfEmptyList);
        stream.Encode(expirationTime);

        NeighborsMsg deserialized = _messageSerializationService.Deserialize<NeighborsMsg>(
            SignAndWrapDiscoveryPacket((byte)MsgType.Neighbors, stream.Data.ToArray()!));

        Assert.That(deserialized.Nodes, Has.Count.EqualTo(1));
        Assert.That(deserialized.Nodes[0].Id, Is.EqualTo(TestItem.PublicKeyA));
    }

    private byte[] SignAndWrapDiscoveryPacket(byte msgType, byte[] data)
    {
        // [<mdc 32 bytes><sig 64 bytes><sig recovery id><msg type><data>]
        byte[] packet = new byte[32 + 64 + 1 + 1 + data.Length];
        packet[97] = msgType;
        data.CopyTo(packet, 98);

        ValueHash256 toSign = ValueKeccak.Compute(packet.AsSpan(97));
        Signature signature = new Ecdsa().Sign(_privateKey, in toSign);
        signature.Bytes.CopyTo(packet.AsSpan(32));
        packet[96] = signature.RecoveryId;

        ValueHash256 mdc = ValueKeccak.Compute(packet.AsSpan(32));
        mdc.BytesAsSpan.CopyTo(packet);
        return packet;
    }

    private EnrResponseMsg BuildEnrResponse(CompressedPublicKey enrPublicKey)
    {
        NodeRecord nodeRecord = new();
        nodeRecord.SetEntry(new SecP256k1Entry(enrPublicKey));
        nodeRecord.EnrSequence = 5;
        NodeRecordSigner signer = new(new Ecdsa(), _privateKey);
        signer.Sign(nodeRecord);
        return new EnrResponseMsg(TestItem.PublicKeyA, nodeRecord, TestItem.KeccakA);
    }

    private byte[] CreatePongWithExtraTail(PongMsg message, byte[] extraTail)
    {
        byte[] addressBytes = message.FarAddress!.Address.GetAddressBytes();
        int farAddressLength =
            Rlp.LengthOf(addressBytes) +
            Rlp.LengthOf(message.FarAddress.Port) +
            Rlp.LengthOf(message.FarAddress.Port);
        int contentLength =
            Rlp.LengthOfSequence(farAddressLength) +
            Rlp.LengthOf(message.PingMdc) +
            Rlp.LengthOf(message.ExpirationTime) +
            Rlp.LengthOf(message.EnrSequence.GetValueOrDefault()) +
            Rlp.LengthOf(extraTail);
        byte[] messageRlp = new byte[Rlp.LengthOfSequence(contentLength)];
        RlpStream rlpStream = new(messageRlp);
        rlpStream.StartSequence(contentLength);
        rlpStream.StartSequence(farAddressLength);
        rlpStream.Encode(addressBytes);
        rlpStream.Encode(message.FarAddress.Port);
        rlpStream.Encode(message.FarAddress.Port);
        rlpStream.Encode(message.PingMdc);
        rlpStream.Encode(message.ExpirationTime);
        rlpStream.Encode(message.EnrSequence.GetValueOrDefault());
        rlpStream.Encode(extraTail);

        byte[] signedPayload = new byte[1 + messageRlp.Length];
        signedPayload[0] = (byte)MsgType.Pong;
        messageRlp.CopyTo(signedPayload.AsSpan(1));

        Ecdsa ecdsa = new();
        ValueHash256 toSign = ValueKeccak.Compute(signedPayload);
        Signature signature = ecdsa.Sign(_privateKey, in toSign);

        byte[] signatureAndPayload = new byte[65 + signedPayload.Length];
        signature.Bytes.CopyTo(signatureAndPayload.AsSpan(0, 64));
        signatureAndPayload[64] = signature.RecoveryId;
        signedPayload.CopyTo(signatureAndPayload.AsSpan(65));

        ValueHash256 mdc = ValueKeccak.Compute(signatureAndPayload);
        byte[] packet = new byte[32 + signatureAndPayload.Length];
        mdc.Bytes.CopyTo(packet.AsSpan(0, 32));
        signatureAndPayload.CopyTo(packet.AsSpan(32));
        return packet;
    }

    [Test]
    public void NeighborsMessage_Rejects_Port_Zero()
    {
        using PooledBufferLeakDetector detector = new(_leakDetectionAllocator);
        NeighborsMsg message =
            new(_privateKey.PublicKey, 60 + _timestamper.UnixTime.MillisecondsLong,
                new Node[] { new(TestItem.PublicKeyA, "192.168.1.2", 0) })
            {
                FarAddress = _farAddress
            };

        using DisposableByteBuffer data = _messageSerializationService.ZeroSerialize(message, detector.Allocator).AsDisposable();
        Assert.Throws<NetworkingException>(() => _messageSerializationService.Deserialize<NeighborsMsg>(data));
    }

    [Test]
    public void NeighborsMessage_Rejects_Too_Many_Nodes()
    {
        NeighborsMsg message = new(_privateKey.PublicKey, 60 + _timestamper.UnixTime.MillisecondsLong,
            Enumerable.Range(0, 17)
                .Select(i => new Node(TestItem.PublicKeys[i], $"192.168.1.{i + 2}", i + 1))
                .ToArray())
        {
            FarAddress = _farAddress
        };

        using DisposableByteBuffer data = _messageSerializationService.ZeroSerialize(message).AsDisposable();
        Assert.Throws<RlpLimitException>(() => _messageSerializationService.Deserialize<NeighborsMsg>(data));
    }

    [Test]
    public void PongMessage_Rejects_Oversized_Ping_Mdc()
    {
        RlpStream stream = new(128);
        long expirationTime = 60 + _timestamper.UnixTime.MillisecondsLong;
        int addressContentLength = Rlp.LengthOf(new byte[] { 127, 0, 0, 1 }) + Rlp.LengthOf(30303) + Rlp.LengthOf(30303);
        int contentLength = Rlp.LengthOfSequence(addressContentLength) + Rlp.LengthOf(new byte[33]) + Rlp.LengthOf(expirationTime);
        stream.StartSequence(contentLength);
        stream.StartSequence(addressContentLength);
        stream.Encode(new byte[] { 127, 0, 0, 1 });
        stream.Encode(30303);
        stream.Encode(30303);
        stream.Encode(new byte[33]);
        stream.Encode(expirationTime);

        Assert.Throws<RlpLimitException>(() => _messageSerializationService.Deserialize<PongMsg>(
            SignAndWrapDiscoveryPacket((byte)MsgType.Pong, stream.Data.ToArray()!)));
    }
}
