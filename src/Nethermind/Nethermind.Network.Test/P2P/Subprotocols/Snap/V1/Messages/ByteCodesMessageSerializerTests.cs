// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.Collections;
using Nethermind.Network.P2P.Subprotocols.Snap;
using Nethermind.Network.P2P.Subprotocols.Snap.V1.Messages;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Snap.V1.Messages
{
    [TestFixture, Parallelizable(ParallelScope.All)]
    public class ByteCodesMessageSerializerTests
    {
        [TestCase(1L, "ca01c884deadc0de82feed")]
        [TestCase(long.MaxValue, "d2887fffffffffffffffc884deadc0de82feed")]
        public void Roundtrip(long requestId, string expectedData)
        {
            ArrayPoolList<byte[]> data = new(2) { new byte[] { 0xde, 0xad, 0xc0, 0xde }, new byte[] { 0xfe, 0xed } };

            using ByteCodesMessage message = new(new ByteArrayListAdapter(data)) { RequestId = requestId };

            ByteCodesMessageSerializer serializer = new();

            // The message encodes as [requestId, codes].
            SerializerTester.TestZero(serializer, message, expectedData);
        }

        [Test]
        public void DecodeEncodeDecodeEmpty()
        {
            byte[] data = { 202, 136, 23, 106, 21, 106, 229, 131, 72, 176, 192 };
            ByteCodesMessageSerializer serializer = new();
            using ByteCodesMessage decode = serializer.Deserialize(data);
            byte[] messageEncode = serializer.Serialize(decode);
            Assert.That(messageEncode, Is.EqualTo(data));
        }

        private static IEnumerable<TestCaseData> CodesLimitCases() =>
            ByteArrayListLimitTester.BoundaryCases(SnapMessageLimits.ByteCodesRlpLimit);

        [TestCaseSource(nameof(CodesLimitCases))]
        public void Deserialize_EnforcesCodesCountLimit(int codeCount, bool shouldThrow) =>
            ByteArrayListLimitTester.AssertLimitEnforced(
                new ByteCodesMessageSerializer(),
                static codes => new ByteCodesMessage(codes) { RequestId = 1 },
                static message => message.Codes.Count,
                codeCount,
                shouldThrow);
    }
}
