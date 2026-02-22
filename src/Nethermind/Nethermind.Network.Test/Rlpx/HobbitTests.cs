// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using DotNetty.Buffers;
using DotNetty.Common;
using DotNetty.Common.Internal.Logging;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Embedded;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V63.Messages;
using Nethermind.Network.Rlpx;
using NUnit.Framework;

namespace Nethermind.Network.Test.Rlpx
{
    [TestFixture]
    public class HobbitTests : HobbitTestsBase
    {
        [TestCase(StackType.Zero, StackType.Zero, true)]
        [TestCase(StackType.Zero, StackType.Zero, false)]
        public void Get_block_bodies_there_and_back(StackType inbound, StackType outbound, bool framingEnabled)
        {
            var hashes = new Hash256[256];
            for (int i = 0; i < hashes.Length; i++)
            {
                hashes[i] = Keccak.Compute(i.ToString());
            }

            using GetBlockBodiesMessage message = new(hashes);

            GetBlockBodiesMessageSerializer serializer = new();
            byte[] data = serializer.Serialize(message);

            Packet packet = new("eth", 5, data);
            Packet decoded = Run(packet, inbound, outbound, framingEnabled);
        }

        [TestCase(StackType.Zero, StackType.Zero, true)]
        [TestCase(StackType.Zero, StackType.Zero, false)]
        public void Block_there_and_back(StackType inbound, StackType outbound, bool framingEnabled)
        {
            Transaction a = Build.A.Transaction.TestObject;
            Transaction b = Build.A.Transaction.TestObject;
            Block block = Build.A.Block.WithTransactions(a, b).TestObject;
            using NewBlockMessage newBlockMessage = new();
            newBlockMessage.Block = block;

            NewBlockMessageSerializer newBlockMessageSerializer = new();
            byte[] data = newBlockMessageSerializer.Serialize(newBlockMessage);
            Packet packet = new("eth", 7, data);
            Packet decoded = Run(packet, inbound, outbound, framingEnabled);
        }

        [TestCase(StackType.Zero, StackType.Zero, true)]
        [TestCase(StackType.Zero, StackType.Zero, false)]
        public void Two_frame_block_there_and_back(StackType inbound, StackType outbound, bool framingEnabled)
        {
            Transaction[] txs = Build.A.Transaction.SignedAndResolved().TestObjectNTimes(10);
            Block block = Build.A.Block.WithTransactions(txs).TestObject;
            using NewBlockMessage newBlockMessage = new();
            newBlockMessage.Block = block;

            NewBlockMessageSerializer newBlockMessageSerializer = new();
            byte[] data = newBlockMessageSerializer.Serialize(newBlockMessage);
            Packet packet = new("eth", 7, data);

            Packet decoded = Run(packet, inbound, outbound, framingEnabled);

            using NewBlockMessage decodedMessage = newBlockMessageSerializer.Deserialize(decoded.Data);
            Assert.That(decodedMessage.Block.Transactions.Length, Is.EqualTo(newBlockMessage.Block.Transactions.Length));
        }

        // Packet types > 128 are needed for XDC
        [TestCase(129, 8, StackType.Zero, StackType.Zero, false)]
        [TestCase(129, Frame.DefaultMaxFrameSize, StackType.Zero, StackType.Zero, false)]
        [TestCase(129, Frame.DefaultMaxFrameSize, StackType.Zero, StackType.Zero, true)]
        [TestCase(129, Frame.DefaultMaxFrameSize + 1, StackType.Zero, StackType.Zero, true)]
        [TestCase(224, 8, StackType.Zero, StackType.Zero, false)]
        [TestCase(224, Frame.DefaultMaxFrameSize, StackType.Zero, StackType.Zero, false)]
        [TestCase(224, Frame.DefaultMaxFrameSize, StackType.Zero, StackType.Zero, true)]
        [TestCase(224, Frame.DefaultMaxFrameSize + 1, StackType.Zero, StackType.Zero, true)]
        [TestCase(255, Frame.DefaultMaxFrameSize, StackType.Zero, StackType.Zero, false)]
        [TestCase(255, Frame.DefaultMaxFrameSize, StackType.Zero, StackType.Zero, true)]
        [TestCase(255, Frame.DefaultMaxFrameSize + 1, StackType.Zero, StackType.Zero, true)]
        [TestCase(256, 8, StackType.Zero, StackType.Zero, false, Ignore = "Values >255 are not supported yet")]
        public void High_packet_type_there_and_back(int packetType, int dataSize, StackType inbound, StackType outbound, bool framingEnabled)
        {
            var data = Enumerable.Range(0, dataSize).Select(i => (byte)i).ToArray();
            Run(new Packet("eth", packetType, data), inbound, outbound, framingEnabled);
        }

        [TestCase(StackType.Zero, StackType.Zero, true)]
        [TestCase(StackType.Zero, StackType.Zero, false)]
        public void Receipts_message(StackType inbound, StackType outbound, bool framingEnabled)
        {
            Hash256[] hashes = new Hash256[256];
            for (int i = 0; i < hashes.Length; i++)
            {
                hashes[i] = Keccak.Compute(i.ToString());
            }

            GetReceiptsMessage message = new(hashes.ToPooledList());

            GetReceiptsMessageSerializer serializer = new();
            byte[] data = serializer.Serialize(message);
            Packet packet = new("eth", 7, data);
            Packet decoded = Run(packet, inbound, outbound, framingEnabled);

            GetReceiptsMessage decodedMessage = serializer.Deserialize(decoded.Data);
            Assert.That(decodedMessage.Hashes.Count, Is.EqualTo(message.Hashes.Count));
        }

        [TestCase(StackType.Zero, StackType.Zero, true)]
        [TestCase(StackType.Zero, StackType.Zero, false)]
        public void Status_message(StackType inbound, StackType outbound, bool framingEnabled)
        {
            using StatusMessage message = new();
            message.BestHash = Keccak.Zero;
            message.GenesisHash = Keccak.Zero;
            message.ProtocolVersion = 63;
            message.TotalDifficulty = 10000000000;
            message.NetworkId = 5;

            StatusMessageSerializer serializer = new();
            byte[] data = serializer.Serialize(message);
            Packet packet = new("eth", 7, data);
            Packet decoded = Run(packet, inbound, outbound, framingEnabled);

            using StatusMessage decodedMessage = serializer.Deserialize(decoded.Data);
            Assert.That(decodedMessage.TotalDifficulty, Is.EqualTo(message.TotalDifficulty));
        }
    }
}
