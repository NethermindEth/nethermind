// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Shutter.Test;

[TestFixture]
public class ShutterP2PTests
{
    // Any gossip peer can publish these; none may escape the parser and end the receive loop.
    private static readonly byte[][] MalformedMessages =
    [
        [],
        new Dto.Envelope().ToByteArray(),
        new Dto.Envelope { Message = Any.Pack(new Dto.DecryptionTrigger()) }.ToByteArray(),
        new Dto.Envelope { Message = Any.Pack(new Dto.DecryptionKeys { InstanceID = 7 }) }.ToByteArray(),
        new Dto.Envelope { Message = Any.Pack(new Dto.DecryptionKeys { InstanceID = 7, Optimism = new() }) }.ToByteArray(),
        [0xff, 0xff, 0xff],
    ];

    [Test]
    public void Malformed_gossip_does_not_parse_and_does_not_throw([ValueSource(nameof(MalformedMessages))] byte[] message)
    {
        bool parsed = ShutterP2P.TryParseDecryptionKeys(message, LimboLogs.Instance.GetClassLogger<ShutterP2P>(), out Dto.DecryptionKeys? keys);

        Assert.That(parsed, Is.False);
        Assert.That(keys, Is.Null);
    }

    [Test]
    public void Decryption_keys_envelope_parses()
    {
        Dto.DecryptionKeys expected = new() { InstanceID = 7, Eon = 3, Gnosis = new() { Slot = 11 } };
        byte[] message = new Dto.Envelope { Message = Any.Pack(expected) }.ToByteArray();

        bool parsed = ShutterP2P.TryParseDecryptionKeys(message, LimboLogs.Instance.GetClassLogger<ShutterP2P>(), out Dto.DecryptionKeys? keys);

        Assert.That(parsed, Is.True);
        Assert.That(keys, Is.EqualTo(expected));
    }
}
