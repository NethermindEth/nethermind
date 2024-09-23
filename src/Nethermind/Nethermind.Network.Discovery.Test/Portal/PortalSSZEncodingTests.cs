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
using Nethermind.Network.Discovery.Portal.Messages;
using Nethermind.Serialization;

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
                        new Discovery.Portal.Messages.Enr { Data = RlpEncodeEnr("enr:-HW4QBzimRxkmT18hMKaAL3IcZF1UcfTMPyi3Q1pxwZZbcZVRI8DC5infUAB_UauARLOJtYTxaagKoGmIjzQxO2qUygBgmlkgnY0iXNlY3AyNTZrMaEDymNMrg1JrLQB2KTGtv6MVbcNEVv0AHacwUAPMljNMTg") },
                        new Discovery.Portal.Messages.Enr { Data = RlpEncodeEnr("enr:-HW4QNfxw543Ypf4HXKXdYxkyzfcxcO-6p9X986WldfVpnVTQX1xlTnWrktEWUbeTZnmgOuAY_KUhbVV1Ft98WoYUBMBgmlkgnY0iXNlY3AyNTZrMaEDDiy3QkHAxPyOgWbxp5oF1bDdlYE6dLCUUp8xfVw50jU") },
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
                        new Discovery.Portal.Messages.Enr { Data = RlpEncodeEnr("enr:-HW4QBzimRxkmT18hMKaAL3IcZF1UcfTMPyi3Q1pxwZZbcZVRI8DC5infUAB_UauARLOJtYTxaagKoGmIjzQxO2qUygBgmlkgnY0iXNlY3AyNTZrMaEDymNMrg1JrLQB2KTGtv6MVbcNEVv0AHacwUAPMljNMTg") },
                        new Discovery.Portal.Messages.Enr { Data = RlpEncodeEnr("enr:-HW4QNfxw543Ypf4HXKXdYxkyzfcxcO-6p9X986WldfVpnVTQX1xlTnWrktEWUbeTZnmgOuAY_KUhbVV1Ft98WoYUBMBgmlkgnY0iXNlY3AyNTZrMaEDDiy3QkHAxPyOgWbxp5oF1bDdlYE6dLCUUp8xfVw50jU") },
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
}
