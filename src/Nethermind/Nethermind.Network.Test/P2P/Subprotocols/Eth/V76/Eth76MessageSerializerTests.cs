// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.P2P.Subprotocols.Eth.V76;
using Nethermind.Network.P2P.Subprotocols.Eth.V76.Messages;
using Nethermind.Serialization.FluentRlp;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V76;

[Parallelizable(ParallelScope.All)]
public class Eth76MessageSerializerTests
{
    // EIP-8518 assets/eip-8518/announcement-vectors.json, first entry, inside the message's list.
    private const string VectorEntry =
        "f866820374a074a7c142cb06efdf93d5a1a98937c0e18acb1a1214f2760b7d85d49abf0ffaa5a0593eed8f1bd308761eb8e4fc09f16a000c81de7a5d9108feea2973bee75efa01a0450aa1895ab2464f0bbbf0c53abdbdcbb6d34caa741c1f3eb0b7a8c70936257c";

    [Test]
    public void Announcement_DecodesTheSpecVector_AndRoundTrips()
    {
        byte[] encoded = Bytes.FromHexString("f868" + VectorEntry);
        NewProofWrapperHashesMessageSerializer serializer = new();

        NewProofWrapperHashesMessage message = serializer.Deserialize(encoded);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(message.Announcements, Has.Count.EqualTo(1));
            Assert.That(message.Announcements[0].Length, Is.EqualTo(0x374));
            Assert.That(message.Announcements[0].Hash, Is.EqualTo(new ValueHash256("0x74a7c142cb06efdf93d5a1a98937c0e18acb1a1214f2760b7d85d49abf0ffaa5")));
            Assert.That(Serialize(serializer, message), Is.EqualTo(encoded));
        }
    }

    [Test]
    public void Announcement_AtTheMaximum_FitsItsByteBound()
    {
        List<WrapperAnnouncement> announcements = [];
        for (int i = 0; i < Eth76Protocol.MaxAnnouncedWrappers; i++)
        {
            announcements.Add(new WrapperAnnouncement(1 << 21, Keccak.Compute([(byte)i]).ValueHash256, TestItem.KeccakA.ValueHash256, TestItem.KeccakB.ValueHash256));
        }

        announcements.Sort(static (left, right) => left.Hash.CompareTo(right.Hash));
        NewProofWrapperHashesMessageSerializer serializer = new();
        byte[] encoded = Serialize(serializer, new NewProofWrapperHashesMessage(announcements));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(encoded, Has.Length.EqualTo(Eth76Protocol.MaxAnnouncementBytes));
            Assert.That(serializer.Deserialize(encoded).Announcements, Has.Count.EqualTo(Eth76Protocol.MaxAnnouncedWrappers));
        }
    }

    public enum Defect { Unordered, Repeated, TooMany, ZeroLength, TooLong, Empty }

    [TestCase(Defect.Unordered)]
    [TestCase(Defect.Repeated)]
    [TestCase(Defect.TooMany)]
    [TestCase(Defect.ZeroLength)]
    [TestCase(Defect.TooLong)]
    [TestCase(Defect.Empty)]
    public void Announcement_TransportViolation_Throws(Defect defect)
    {
        int count = defect switch { Defect.TooMany => Eth76Protocol.MaxAnnouncedWrappers + 1, Defect.Empty => 0, _ => 2 };
        List<WrapperAnnouncement> announcements = [];
        for (int i = 0; i < count; i++)
        {
            int length = defect switch { Defect.ZeroLength => 0, Defect.TooLong => (1 << 21) + 1, _ => 100 };
            announcements.Add(new WrapperAnnouncement(length, Keccak.Compute([(byte)i]).ValueHash256, default, default));
        }

        announcements.Sort(static (left, right) => left.Hash.CompareTo(right.Hash));
        if (defect == Defect.Unordered) announcements.Reverse();
        if (defect == Defect.Repeated) announcements[1] = announcements[0];
        NewProofWrapperHashesMessageSerializer serializer = new();
        byte[] encoded = Serialize(serializer, new NewProofWrapperHashesMessage(announcements));

        Assert.That(() => serializer.Deserialize(encoded), Throws.InstanceOf<RlpReaderException>());
    }

    [TestCase(true)]
    [TestCase(false)]
    public void GetProofWrappers_RoundTrips(bool compact)
    {
        GetProofWrappersMessageSerializer serializer = new();
        GetProofWrappersMessage message = new(7, [TestItem.KeccakA.ValueHash256, TestItem.KeccakB.ValueHash256], compact);

        GetProofWrappersMessage decoded = serializer.Deserialize(Serialize(serializer, message));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded.RequestId, Is.EqualTo(7));
            Assert.That(decoded.Compact, Is.EqualTo(compact));
            Assert.That(decoded.Hashes, Is.EqualTo(message.Hashes));
        }
    }

    [Test]
    public void GetProofWrappers_RepeatedOrExcessHashes_Throw()
    {
        GetProofWrappersMessageSerializer serializer = new();
        ValueHash256[] tooMany = new ValueHash256[Eth76Protocol.MaxRequestedWrappers + 1];
        for (int i = 0; i < tooMany.Length; i++) tooMany[i] = Keccak.Compute([(byte)i]).ValueHash256;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => serializer.Deserialize(Serialize(serializer, new GetProofWrappersMessage(1, [TestItem.KeccakA.ValueHash256, TestItem.KeccakA.ValueHash256], true))),
                Throws.InstanceOf<RlpReaderException>());
            Assert.That(() => serializer.Deserialize(Serialize(serializer, new GetProofWrappersMessage(1, tooMany, true))), Throws.InstanceOf<RlpLimitException>());
        }
    }

    [Test]
    public void ProofWrappers_KeepTheEmbeddedItem()
    {
        ProofWrappersMessageSerializer serializer = new();
        byte[] item = Bytes.FromHexString("c3c0c080");
        ProofWrappersMessage message = new(3, [new ServedWrapper(TestItem.KeccakA.ValueHash256, item)]);

        ProofWrappersMessage decoded = serializer.Deserialize(Serialize(serializer, message));

        Assert.That(decoded.Wrappers[0].Item, Is.EqualTo(item));
    }

    [Test]
    public void Manifests_RoundTrip()
    {
        ProofWrapperManifestsMessageSerializer serializer = new();
        ProofWrapperManifestsMessage message = new(3, [new ServedManifest(TestItem.KeccakA.ValueHash256, [TestItem.KeccakB.ValueHash256, TestItem.KeccakC.ValueHash256])]);

        ProofWrapperManifestsMessage decoded = serializer.Deserialize(Serialize(serializer, message));

        Assert.That(decoded.Manifests[0].TransactionHashes, Is.EqualTo(message.Manifests[0].TransactionHashes));
    }

    [TestCase(Eth76MessageCode.NewProofWrapperHashes, Eth76Protocol.MaxAnnouncementBytes, false)]
    [TestCase(Eth76MessageCode.NewProofWrapperHashes, Eth76Protocol.MaxAnnouncementBytes + 1, true)]
    [TestCase(Eth76MessageCode.ProofWrappers, Eth76Protocol.LocalMaxResponseBytes + 1, true)]
    [TestCase(Eth76MessageCode.GetProofWrappers, Eth76Protocol.LocalMaxResponseBytes + 1, false)]
    public void ExceedsMessageLimit(int code, int length, bool expected) =>
        Assert.That(Eth76Protocol.ExceedsMessageLimit(code, length), Is.EqualTo(expected));

    private static byte[] Serialize<T>(IMessageSerializer<T> serializer, T message) where T : MessageBase
    {
        byte[] buffer = new byte[serializer.GetLength(message)];
        serializer.Serialize(buffer, message);
        return buffer;
    }
}
