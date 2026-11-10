// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using BenchmarkDotNet.Attributes;
using DotNetty.Buffers;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State.Snap;
using Snap1 = Nethermind.Network.P2P.Subprotocols.Snap.V1.Messages;
using Snap2 = Nethermind.Network.P2P.Subprotocols.Snap.V2.Messages;

namespace Nethermind.Network.Benchmarks;

/// <summary>
/// Measures serializing snap messages into a packet the way the packet sender does, and
/// deserializing them from a received packet the way the snap protocol handlers do.
/// </summary>
[MemoryDiagnoser]
public class SnapMessageBenchmarks
{
    public enum Message
    {
        GetAccountRange,
        AccountRange,
        GetStorageRanges,
        StorageRanges,
        GetByteCodes,
        ByteCodes,
        GetTrieNodes,
        TrieNodes,
        GetBlockAccessLists,
        BlockAccessLists
    }

    private const long RequestId = 0x1234_5678_9abc;
    private const long ResponseBytes = 512 * 1024;

    private IContainer _container = null!;
    private Func<int> _serialize = null!;
    private Func<ZeroPacket, P2PMessage> _deserialize = null!;
    private NettyBufferPacket _body = null!;

    [ParamsAllValues]
    public Message Type { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        ChainSpec chainSpec = new()
        {
            Parameters = new ChainParameters(),
            Allocations = [],
            Genesis = Build.A.Block.TestObject
        };
        _container = new ContainerBuilder()
            .AddModule(new PseudoNethermindModule(chainSpec, new ConfigProvider(), LimboLogs.Instance))
            .AddModule(new TestEnvironmentModule(TestItem.PrivateKeyA, null))
            .Build();
        IMessageSerializationService service = _container.Resolve<IMessageSerializationService>();

        switch (Type)
        {
            case Message.GetAccountRange:
                SetUp(service, new Snap1.GetAccountRangeMessage
                {
                    RequestId = RequestId,
                    AccountRange = new(TestItem.KeccakA, TestItem.KeccakB.ValueHash256, TestItem.KeccakC.ValueHash256),
                    ResponseBytes = ResponseBytes
                });
                break;
            case Message.AccountRange:
                ArrayPoolList<PathWithAccount> accounts = [with(256)];
                foreach (Hash256 hash in Hashes(256))
                    accounts.Add(new PathWithAccount(hash.ValueHash256, new Account((ulong)accounts.Count, UInt256.One << 64, hash, TestItem.KeccakA)));
                SetUp(service, new Snap1.AccountRangeMessage { RequestId = RequestId, PathsWithAccounts = accounts, Proofs = Nodes(8, 532) });
                break;
            case Message.GetStorageRanges:
                ArrayPoolList<PathWithAccount> storageAccounts = [with(128)];
                foreach (Hash256 hash in Hashes(128))
                    storageAccounts.Add(new PathWithAccount { Path = hash.ValueHash256 });
                SetUp(service, new Snap1.GetStorageRangeMessage
                {
                    RequestId = RequestId,
                    StorageRange = new() { RootHash = TestItem.KeccakA, Accounts = storageAccounts, StartingHash = Keccak.Zero, LimitHash = Keccak.MaxValue },
                    ResponseBytes = ResponseBytes
                });
                break;
            case Message.StorageRanges:
                ArrayPoolList<IOwnedReadOnlyList<PathWithStorageSlot>> slots = [with(4)];
                for (int i = 0; i < 4; i++)
                {
                    ArrayPoolList<PathWithStorageSlot> accountSlots = [with(128)];
                    foreach (Hash256 hash in Hashes(128))
                        accountSlots.Add(new PathWithStorageSlot(hash.ValueHash256, Rlp.Encode(hash.Bytes[..20]).Bytes));
                    slots.Add(accountSlots);
                }
                SetUp(service, new Snap1.StorageRangeMessage { RequestId = RequestId, Slots = slots, Proofs = Nodes(8, 532) });
                break;
            case Message.GetByteCodes:
                ArrayPoolList<ValueHash256> codeHashes = [with(64)];
                foreach (Hash256 hash in Hashes(64)) codeHashes.Add(hash.ValueHash256);
                SetUp(service, new Snap1.GetByteCodesMessage { RequestId = RequestId, Hashes = codeHashes, Bytes = ResponseBytes });
                break;
            case Message.ByteCodes:
                SetUp(service, new Snap1.ByteCodesMessage(Nodes(64, 8 * 1024)) { RequestId = RequestId });
                break;
            case Message.GetTrieNodes:
                PathGroup[] groups = new PathGroup[64];
                for (int i = 0; i < groups.Length; i++)
                    groups[i] = new PathGroup { Group = i % 2 == 0 ? [[(byte)i, 0x12]] : [Keccak.Compute([(byte)i]).BytesToArray(), [(byte)i], [(byte)i, 0x34]] };
                SetUp(service, new Snap1.GetTrieNodesMessage { RequestId = RequestId, RootHash = TestItem.KeccakA, Paths = PathGroup.EncodeToRlpPathGroupList(groups), Bytes = ResponseBytes });
                break;
            case Message.TrieNodes:
                SetUp(service, new Snap1.TrieNodesMessage(Nodes(384, 100)) { RequestId = RequestId });
                break;
            case Message.GetBlockAccessLists:
                ArrayPoolList<ValueHash256> blockHashes = [with(64)];
                foreach (Hash256 hash in Hashes(64)) blockHashes.Add(hash.ValueHash256);
                SetUp(service, new Snap2.GetBlockAccessListsMessage { RequestId = RequestId, BlockHashes = blockHashes, Bytes = ResponseBytes });
                break;
            case Message.BlockAccessLists:
                ArrayPoolList<byte[]> blockAccessLists = [with(16)];
                for (int i = 0; i < 16; i++)
                    blockAccessLists.Add(Rlp.Encode(new byte[16 * 1024]).Bytes);
                SetUp(service, new Snap2.BlockAccessListsMessage(new ByteArrayListAdapter(blockAccessLists)) { RequestId = RequestId });
                break;
        }
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _body.Release();
        _container.Dispose();
    }

    [Benchmark]
    public int Serialize() => _serialize();

    [Benchmark]
    public int Deserialize()
    {
        using P2PMessage message = _deserialize(_body);
        return message.PacketType;
    }

    private void SetUp<T>(IMessageSerializationService service, T message) where T : P2PMessage
    {
        _serialize = () =>
        {
            IByteBuffer packet = service.ZeroSerialize(message);
            int length = packet.ReadableBytes;
            packet.Release();
            return length;
        };
        _deserialize = body => service.Deserialize<T>(body);

        IByteBuffer packet = service.ZeroSerialize(message);
        packet.SkipBytes(Rlp.LengthOf(message.AdaptivePacketType));
        _body = new NettyBufferPacket(Unpooled.WrappedBuffer(packet.ReadAllBytesAsArray()));
        packet.Release();
    }

    private static Hash256[] Hashes(int count)
    {
        Hash256[] hashes = new Hash256[count];
        for (int i = 0; i < count; i++)
            hashes[i] = Keccak.Compute(BitConverter.GetBytes(i));
        Array.Sort(hashes);
        return hashes;
    }

    private static ByteArrayListAdapter Nodes(int count, int size)
    {
        ArrayPoolList<byte[]> nodes = [with(count)];
        for (int i = 0; i < count; i++)
        {
            byte[] node = new byte[size];
            node[0] = (byte)i;
            nodes.Add(node);
        }

        return new ByteArrayListAdapter(nodes);
    }
}
