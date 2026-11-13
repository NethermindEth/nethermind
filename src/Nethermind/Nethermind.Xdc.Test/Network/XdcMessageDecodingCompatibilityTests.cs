// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Autofac;
using DotNetty.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Network;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.Test.P2P;
using Nethermind.Xdc.P2P;
using Nethermind.Xdc.P2P.Messages;
using Nethermind.Xdc.RLP;
using Nethermind.Xdc.Test.Helpers;
using Nethermind.Xdc.Types;
using NUnit.Framework;
using static Nethermind.Network.Test.P2P.MessageDecodingCompatibility;

namespace Nethermind.Xdc.Test.Network;

/// <summary>
/// The XDC counterpart of <see cref="MessageDecodingCompatibilityTests"/>, for the serializers <see cref="XdcModule"/> registers.
/// </summary>
[TestFixture]
public class XdcMessageDecodingCompatibilityTests
{
    private XdcTestBlockchain _chain = null!;

    private IMessageSerializationService Service => _chain.Container.Resolve<IMessageSerializationService>();

    [OneTimeSetUp]
    public async Task OneTimeSetUp() => _chain = await XdcTestBlockchain.Create(0);

    [OneTimeTearDown]
    public void OneTimeTearDown() => _chain.Dispose();

    private static IEnumerable<Sample> Samples()
    {
        yield return Sample.Of(static () => new XdcGetPooledTransactionsMessage(new ArrayPoolList<ValueHash256>(1) { TestItem.KeccakA.ValueHash256 }));
        yield return Sample.Of(static () => new VoteMsg { Vote = SignedVote() });
        yield return Sample.Of(static () => new TimeoutMsg { Timeout = SignedTimeout() });
        yield return Sample.Of(static () => new SyncInfoMsg { SyncInfo = SignedSyncInfo() });
    }

    [Test]
    public void Every_registered_span_serializer_has_a_sample() =>
        AssertSampled(_chain.Container.Resolve<IReadOnlyList<SerializerInfo>>(), [.. MessageDecodingCompatibilityTests.Samples(), .. Samples()]);

    [Test]
    public void Accepts_a_trailing_byte_only_where_the_handlers_always_did([ValueSource(nameof(Samples))] Sample sample, [Values] bool trailingByte) =>
        AssertTrailingByteHandling(Service, sample, trailingByte);

    [Test]
    public void Releases_the_received_buffer_however_decoding_ends([ValueSource(nameof(Samples))] Sample sample) =>
        AssertReleasesBuffer(Service, sample);

    private static IEnumerable<TestCaseData> Payloads()
    {
        Func<IMessageSerializationService, IByteBuffer, P2PMessage> vote = Deserializer<VoteMsg>();
        Func<IMessageSerializationService, IByteBuffer, P2PMessage> timeout = Deserializer<TimeoutMsg>();
        Func<IMessageSerializationService, IByteBuffer, P2PMessage> syncInfo = Deserializer<SyncInfoMsg>();

        yield return new TestCaseData(vote, "c0", true).SetName("xdc vote without a vote");
        yield return new TestCaseData(timeout, "c0", true).SetName("xdc timeout without a timeout");
        yield return new TestCaseData(syncInfo, "c0", true).SetName("xdc sync info without sync info");
        yield return new TestCaseData(vote, "", false).SetName("xdc empty vote");
        yield return new TestCaseData(timeout, "", false).SetName("xdc empty timeout");
        yield return new TestCaseData(syncInfo, "", false).SetName("xdc empty sync info");
    }

    [TestCaseSource(nameof(Payloads))]
    public void Accepts_only_what_the_handlers_accept(Func<IMessageSerializationService, IByteBuffer, P2PMessage> deserialize, string payload, bool accepted) =>
        Assert.That(IsAccepted(Service, deserialize, Bytes.FromHexString(payload)), Is.EqualTo(accepted));

    private static IEnumerable<TestCaseData> Encodings()
    {
        yield return new TestCaseData(Sample.Of(static () => new VoteMsg { Vote = SignedVote() }), new VoteDecoder().Encode(SignedVote()).Bytes).SetName("xdc vote");
        yield return new TestCaseData(Sample.Of(static () => new TimeoutMsg { Timeout = SignedTimeout() }), new TimeoutDecoder().Encode(SignedTimeout()).Bytes).SetName("xdc timeout");
        yield return new TestCaseData(Sample.Of(static () => new SyncInfoMsg { SyncInfo = SignedSyncInfo() }), new SyncInfoDecoder().Encode(SignedSyncInfo()).Bytes).SetName("xdc sync info");
    }

    [TestCaseSource(nameof(Encodings))]
    public void Sends_the_consensus_object_as_its_decoder_encodes_it(Sample sample, byte[] encoding) =>
        Assert.That(sample.Payload(Service), Is.EqualTo(encoding));

    private static Vote SignedVote() => XdcTestHelper.BuildSignedVote(new BlockRoundInfo(TestItem.KeccakA, 123, 100), 400, TestItem.PrivateKeyA);

    private static Timeout SignedTimeout() => XdcTestHelper.BuildSignedTimeout(TestItem.PrivateKeyA, 123, 400);

    private static SyncInfo SignedSyncInfo() => XdcTestHelper.BuildSyncInfo(TestItem.PrivateKeyA, 123, 400);
}
