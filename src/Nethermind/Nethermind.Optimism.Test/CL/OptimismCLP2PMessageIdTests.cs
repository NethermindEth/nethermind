// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Nethermind.Core.Extensions;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;
using Nethermind.Optimism.CL.P2P;
using NUnit.Framework;
using Snappier;

namespace Nethermind.Optimism.Test.CL;

public class OptimismCLP2PMessageIdTests
{
    private const string Topic = "/optimism/10/2/blocks";

    [Test]
    public void Valid_snappy_message_id_matches_independent_computation()
    {
        byte[] payload = Enumerable.Range(0, 1000).Select(static i => (byte)(i % 7)).ToArray();
        byte[] compressed = Snappy.CompressToArray(payload);

        byte[] expected = SHA256.HashData(
            [0x01, 0x00, 0x00, 0x00, .. BitConverter.GetBytes((ulong)Topic.Length), .. Encoding.ASCII.GetBytes(Topic), .. payload])[..20];

        MessageId id = OptimismCLP2P.CalculateMessageId(CreateMessage(compressed));

        Assert.That(id.Bytes, Is.EqualTo(expected));
    }

    [Test]
    public void Valid_snappy_message_id_matches_known_vector()
    {
        byte[] compressed = [0x05, 0x10, .. "hello"u8];

        MessageId id = OptimismCLP2P.CalculateMessageId(CreateMessage(compressed));

        Assert.That(id.Bytes, Is.EqualTo(Bytes.FromHexString("0x0089d50f80a1e1668e93fef3bd0d075261438c71")));
    }

    [TestCase("0xffffffffffff", "0x7c253cded5906d0b2b3beb0da6a58f918cd547e7")]
    [TestCase("0x05ff", "0xed7e56215e25b17ad2a74ce39fc2f7e451f40dbf")]
    public void Invalid_snappy_message_id_uses_invalid_domain_and_raw_data(string data, string expectedId)
    {
        byte[] raw = Bytes.FromHexString(data);

        byte[] expected = SHA256.HashData(
            [0x00, 0x00, 0x00, 0x00, .. BitConverter.GetBytes((ulong)Topic.Length), .. Encoding.ASCII.GetBytes(Topic), .. raw])[..20];

        MessageId id = OptimismCLP2P.CalculateMessageId(CreateMessage(raw));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(id.Bytes, Is.EqualTo(expected));
            Assert.That(id.Bytes, Is.EqualTo(Bytes.FromHexString(expectedId)));
        }
    }

    private static Message CreateMessage(byte[] data) => new() { Topic = Topic, Data = ByteString.CopyFrom(data) };
}
