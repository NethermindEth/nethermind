// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Subprotocols.Eth.V72.Messages;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V72;

[TestFixture, Parallelizable(ParallelScope.All)]
public class Eth72MessageSerializerTests
{
    [Test]
    public void GetCellsMessageSerializer_should_reject_invalid_cell_mask_length()
    {
        GetCellsMessageSerializer72 serializer = new();
        using GetCellsMessage72 message = new([Hash256.Zero], [1, 2]);

        using DisposableByteBuffer buffer = PooledByteBufferAllocator.Default.Buffer().AsDisposable();
        serializer.Serialize(buffer, message);

        Assert.That(() => serializer.Deserialize(buffer), Throws.TypeOf<RlpException>());
    }

    [Test]
    public void CellsMessageSerializer_should_reject_invalid_cell_mask_length()
    {
        CellsMessageSerializer72 serializer = new();
        using CellsMessage72 message = new([Hash256.Zero], [[[]]], [1, 2]);

        using DisposableByteBuffer buffer = PooledByteBufferAllocator.Default.Buffer().AsDisposable();
        serializer.Serialize(buffer, message);

        Assert.That(() => serializer.Deserialize(buffer), Throws.TypeOf<RlpException>());
    }

    [Test]
    public void NewPooledTransactionHashesMessageSerializer_should_reject_invalid_non_empty_cell_mask_length()
    {
        NewPooledTransactionHashesMessageSerializer72 serializer = new();
        using NewPooledTransactionHashesMessage72 message = new([1], [1], [Hash256.Zero], [1, 2]);

        using DisposableByteBuffer buffer = PooledByteBufferAllocator.Default.Buffer().AsDisposable();
        serializer.Serialize(buffer, message);

        Assert.That(() => serializer.Deserialize(buffer), Throws.TypeOf<RlpException>());
    }
}
