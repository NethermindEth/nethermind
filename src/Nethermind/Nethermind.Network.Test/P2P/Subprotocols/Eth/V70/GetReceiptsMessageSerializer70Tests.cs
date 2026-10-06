// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using DotNetty.Buffers;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.P2P.Subprotocols.Eth.V70.Messages;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V70;

[Parallelizable(ParallelScope.All)]
public class GetReceiptsMessageSerializer70Tests
{
    [Test]
    public void Deserialize_disposes_a_message_rejected_for_trailing_data([Values] bool trailingData)
    {
        CapturingSerializer serializer = new();
        byte[] bytes = trailingData
            ? [0xe5, 0x01, 0x80, 0xe1, 0xa0, .. TestItem.KeccakA.Bytes, 0x80]
            : [0xe4, 0x01, 0x80, 0xe1, 0xa0, .. TestItem.KeccakA.Bytes];
        try
        {
            if (trailingData)
            {
                Assert.Throws<RlpException>(() => serializer.Deserialize(bytes, out _));
                Assert.Throws<ObjectDisposedException>(() => { _ = serializer.Decoded!.Hashes.AsSpan(); });
            }
            else
            {
                GetReceiptsMessage70 message = serializer.Deserialize(bytes, out _);
                Assert.That(message.Hashes.AsSpan().Length, Is.EqualTo(1));
            }
        }
        finally
        {
            serializer.Decoded?.Dispose();
        }
    }

    private sealed class CapturingSerializer : GetReceiptsMessageSerializer70
    {
        public GetReceiptsMessage70? Decoded { get; private set; }

        protected override GetReceiptsMessage70 DeserializeInternal(ref RlpReader reader, long requestId) =>
            Decoded = base.DeserializeInternal(ref reader, requestId);
    }

    [Test]
    public void Deserialize_throws_on_null_hash()
    {
        GetReceiptsMessageSerializer70 serializer = new();
        byte[] payload = [0xc4, 0x01, 0x80, 0xc1, 0x80];

        Assert.That(() => serializer.Deserialize(payload, out _), Throws.InstanceOf<RlpException>());
    }
}
