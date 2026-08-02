// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Network.P2P.Subprotocols.Eth.V70.Messages;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V70;

[Parallelizable(ParallelScope.All)]
public class GetReceiptsMessageSerializer70Tests
{
    [Test]
    public void Deserialize_throws_on_null_hash()
    {
        GetReceiptsMessageSerializer70 serializer = new();
        using DisposableByteBuffer payload = Unpooled.WrappedBuffer([0xc4, 0x01, 0x80, 0xc1, 0x80]).AsDisposable();

        Assert.That(() => serializer.Deserialize(payload), Throws.TypeOf<RlpException>());
    }
}
