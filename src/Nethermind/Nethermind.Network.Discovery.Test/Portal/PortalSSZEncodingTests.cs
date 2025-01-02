// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using System.Collections.Generic;
using FluentAssertions;
using Lantern.Discv5.Enr;
using Lantern.Discv5.Enr.Identity.V4;
using Nethermind.Core.Extensions;
using NUnit.Framework;
using Nethermind.Int256;
using Nethermind.Serialization;
using Nethermind.Network.Portal.Messages;
using System;
using Nethermind.Network.Portal.History;

namespace Nethermind.Network.Discovery.Test.Portal;

public class PortalSSZEncodingTests
{

    public static IEnumerable<(string, MessageUnion)> TestVectors()
    {
        {
            var pingMessage = new MessageUnion()
            {
                Selector = MessageType.Ping,
                Ping = new Ping()
                {
                    EnrSeq = 1,
                    CustomPayload = (UInt256.MaxValue - 1).ToLittleEndian()
                }
            };

            yield return ("0001000000000000000c000000feffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", pingMessage);
        }

        {
            var pongMessage = new MessageUnion()
            {
                Selector = MessageType.Pong,
                Pong = new Pong()
                {
                    EnrSeq = 1,
                    CustomPayload = (UInt256.MaxValue / 2).ToLittleEndian()
                }
            };

            yield return ("0101000000000000000c000000ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff7f", pongMessage);
        }

        {
            var findNeighbourMessage = new MessageUnion()
            {
                Selector = MessageType.FindNodes,
                FindNodes = new FindNodes()
                {
                    Distances = [256, 255]
                }
            };

            yield return ("02040000000001ff00", findNeighbourMessage);
        }

        {
            var nodeResponseMessage = new MessageUnion()
            {
                Selector = MessageType.Nodes,
                Nodes = new Nodes()
                {
                    Total = 1,
                    Enrs = []
                }
            };

            yield return ("030105000000", nodeResponseMessage);
        }

        {

            var nodeResponseMessage = new MessageUnion()
            {
                Selector = MessageType.Nodes,
                Nodes = new Nodes()
                {
                    Total = 1,
                    Enrs = [
                        new Network.Portal.Messages.Enr { Data = RlpEncodeEnr("enr:-HW4QBzimRxkmT18hMKaAL3IcZF1UcfTMPyi3Q1pxwZZbcZVRI8DC5infUAB_UauARLOJtYTxaagKoGmIjzQxO2qUygBgmlkgnY0iXNlY3AyNTZrMaEDymNMrg1JrLQB2KTGtv6MVbcNEVv0AHacwUAPMljNMTg") },
                        new Network.Portal.Messages.Enr { Data = RlpEncodeEnr("enr:-HW4QNfxw543Ypf4HXKXdYxkyzfcxcO-6p9X986WldfVpnVTQX1xlTnWrktEWUbeTZnmgOuAY_KUhbVV1Ft98WoYUBMBgmlkgnY0iXNlY3AyNTZrMaEDDiy3QkHAxPyOgWbxp5oF1bDdlYE6dLCUUp8xfVw50jU") },
                    ]
                }
            };

            yield return ("030105000000080000007f000000f875b8401ce2991c64993d7c84c29a00bdc871917551c7d330fca2dd0d69c706596dc655448f030b98a77d4001fd46ae0112ce26d613c5a6a02a81a6223cd0c4edaa53280182696482763489736563703235366b31a103ca634cae0d49acb401d8a4c6b6fe8c55b70d115bf400769cc1400f3258cd3138f875b840d7f1c39e376297f81d7297758c64cb37dcc5c3beea9f57f7ce9695d7d5a67553417d719539d6ae4b445946de4d99e680eb8063f29485b555d45b7df16a1850130182696482763489736563703235366b31a1030e2cb74241c0c4fc8e8166f1a79a05d5b0dd95813a74b094529f317d5c39d235", nodeResponseMessage);
        }

        {
            // Note: not part of test vector. This is specific to history network.
            var findContentMessage = new MessageUnion()
            {
                Selector = MessageType.FindContent,
                FindContent = new FindContent()
                {
                    ContentKey = new ContentKey { Data = Bytes.FromHexString("706f7274616c") }
                }
            };

            yield return ("0404000000706f7274616c", findContentMessage);
        }

        {
            var contentResponse = new MessageUnion()
            {
                Selector = MessageType.Content,
                Content = new Content()
                {
                    Selector = ContentType.ConnectionId,
                    ConnectionId = 0x0201 // little endian here.. I guess.
                }
            };

            yield return ("05000102", contentResponse);
        }

        {
            var contentResponse = new MessageUnion()
            {
                Selector = MessageType.Content,
                Content = new Content()
                {
                    Selector = ContentType.Payload,
                    Payload = Bytes.FromHexString("7468652063616b652069732061206c6965"),
                }
            };

            yield return ("05017468652063616b652069732061206c6965", contentResponse);
        }

        {
            var contentResponse = new MessageUnion()
            {
                Selector = MessageType.Content,
                Content = new Content()
                {
                    Selector = ContentType.Enrs,
                    Enrs = [
                        new Network.Portal.Messages.Enr { Data = RlpEncodeEnr("enr:-HW4QBzimRxkmT18hMKaAL3IcZF1UcfTMPyi3Q1pxwZZbcZVRI8DC5infUAB_UauARLOJtYTxaagKoGmIjzQxO2qUygBgmlkgnY0iXNlY3AyNTZrMaEDymNMrg1JrLQB2KTGtv6MVbcNEVv0AHacwUAPMljNMTg") },
                        new Network.Portal.Messages.Enr { Data = RlpEncodeEnr("enr:-HW4QNfxw543Ypf4HXKXdYxkyzfcxcO-6p9X986WldfVpnVTQX1xlTnWrktEWUbeTZnmgOuAY_KUhbVV1Ft98WoYUBMBgmlkgnY0iXNlY3AyNTZrMaEDDiy3QkHAxPyOgWbxp5oF1bDdlYE6dLCUUp8xfVw50jU") },
                    ],
                }
            };

            yield return ("0502080000007f000000f875b8401ce2991c64993d7c84c29a00bdc871917551c7d330fca2dd0d69c706596dc655448f030b98a77d4001fd46ae0112ce26d613c5a6a02a81a6223cd0c4edaa53280182696482763489736563703235366b31a103ca634cae0d49acb401d8a4c6b6fe8c55b70d115bf400769cc1400f3258cd3138f875b840d7f1c39e376297f81d7297758c64cb37dcc5c3beea9f57f7ce9695d7d5a67553417d719539d6ae4b445946de4d99e680eb8063f29485b555d45b7df16a1850130182696482763489736563703235366b31a1030e2cb74241c0c4fc8e8166f1a79a05d5b0dd95813a74b094529f317d5c39d235", contentResponse);
        }

        {
            var offerRequest = new MessageUnion()
            {
                Selector = MessageType.Offer,
                Offer = new Offer()
                {
                    ContentKeys = [new ContentKey { Data = Bytes.FromHexString("010203") }]
                }
            };

            yield return ("060400000004000000010203", offerRequest);
        }

        {
            var acceptResponse = new MessageUnion()
            {
                Selector = MessageType.Accept,
                Accept = new Accept()
                {
                    ConnectionId = 0x0201,
                    AcceptedBits = new BitArray(new[] { true, false, false, false, false, false, false, false })
                }
            };

            yield return ("070102060000000101", acceptResponse);
        }
    }

    [TestCaseSource(nameof(TestVectors))]
    public void TestSSZEncoding((string, MessageUnion) test)
    {
        (string Encoding, MessageUnion Object) = test;

        var serializedValue = Serialization.SszEncoding.Encode(Object);

        serializedValue.ToHexString().Should().BeEquivalentTo(Encoding);
    }

    [TestCaseSource(nameof(TestVectors))]
    public void TestSSZEncoding_Roundtrip((string, MessageUnion) test)
    {
        (string Encoding, MessageUnion Object) = test;

        var encoded = Serialization.SszEncoding.Encode(Object);
        Serialization.SszEncoding.Decode(encoded, out MessageUnion decoded);
        var encodedAgain = Serialization.SszEncoding.Encode(decoded);

        encodedAgain.ToHexString().Should().BeEquivalentTo(encoded.ToHexString());
    }

    [TestCaseSource(nameof(TestVectors))]
    public void TestSSZDecoding((string, MessageUnion) test)
    {
        (string Encoding, MessageUnion Object) = test;

        SszEncoding.Decode(Bytes.FromHexString(Encoding), out MessageUnion decodedValue);

        decodedValue.Should().BeEquivalentTo(Object);
    }

    private static readonly EnrEntryRegistry Registry = new EnrEntryRegistry();
    private static readonly EnrFactory EnrFactory = new(Registry);
    private static readonly IdentityVerifierV4 IdentityVerifier = new();

    static byte[] RlpEncodeEnr(string enrString)
    {
        return EnrFactory.CreateFromString(enrString, IdentityVerifier).EncodeRecord();
    }

    [Test]
    public void TestSSZEncodin2g()
    {
        byte[] response = Convert.FromHexString("0404000000019068acefea6dbabf091c10a10429ee6f7423beda11b7fe92f0487e2277f6aeed");
        MessageUnion union;
        //Content message;
        SszEncoding.Decode(response, out union);
    }

    [Test]
    public void TestSSZEncodin2g3()
    {
        byte[] response = Convert.FromHexString("080000002d020000f90222a02c58e3212c085178dbb1277e2f3c24b3f451267a75a234945c1581af639f4a7aa058a694212e0416353a4d3865ccf475496b55af3a3d3b002057000741af9731919400192fb10df37c9fb26829eb2cc623cd1bf599e8a067a9fb631f4579f9015ef3c6f1f3830dfa2dc08afe156f750e90022134b9ebf6a018a2978fc62cd1a23e90de920af68c0c3af3330327927cda4c005faccefb5ce7a0168a3827607627e781941dc777737fc4b6beb69a8b139240b881992b35b854eab9010000200000400000001000400080080000000000010004010001000008000000002000110000000000000090020001110402008000080208040010000000a8000000000000000000210822000900205020000000000160020020000400800040000000000042080000000400004008084020001000001004004000001000000000000001000000110000040000010200844040048101000008002000404810082002800000108020000200408008000100000000000000002020000b00010080600902000200000050000400000000000000400000002002101000000a00002000003420000800400000020100002000000000000000c00040000001000000100187327bd7ad3116ce83e147ed8401c9c36483140db184627d9afa9a457468657265756d50504c4e532f326d696e6572735f55534133a0f1a32e24eb62f01ec3f2b3b5893f7be9062fbf5482bc0d490a54352240350e26882087fbb243327696851aae1651b6010cc53ffa2df1bae1550a0000000000000000000000000000000000000000000063d45d0a2242d35484f289108b3c80cccf943005db0db6c67ffea4c4a47fd529f64d74fa6068a3fd89a2c0d9938c3a751c4706d0b0e8f99dec6b517cf12809cb413795c8c678b3171303ddce2fa1a91af6a0961b9db72750d4d5ea7d5103d8d25f23f522d9af4c13fe8ac7a7d9d64bb08d980281eea5298b93cb1085fedc19d4c60afdd52d116cfad030cf4223e50afa8031154a2263c76eb08b96b5b8fdf5e5c30825d5c918eefb89daaf0e8573f20643614d9843a1817b6186074e4e53b22cf49046d977c901ec00aef1555fa89468adc2a51a081f186c995153d1cba0f2887d585212d68be4b958d309fbe611abe98a9bfc3f4b7a7b72bb881b888d89a04ecfe08b1c1a48554a48328646e4f864fe722f12d850f0be29e3829d1f94b34083032a9b6f43abd559785c996229f8e022d4cd6dcde4aafcce6445fe8743e1fcbe8672a99f9d9e3a5ca10c01f3751d69fbd22197f0680bc1529151130b22759bf185f4dbce357f46eb9cc8e21ea78f49b298eea2756d761fe23de8bea0d2e15aed136d689f6d252c54ebadc3e46b84a397b681edf7ec63522b9a298301084d019d0020000000000000000000000000000000000000000000000000000000000000");
        PortalBlockHeaderWithProof headerWithProof;
        //Content message;headerWithProof
        SszEncoding.Decode(response, out headerWithProof);
    }
}
