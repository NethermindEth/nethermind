// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Trie;
using NUnit.Framework;

namespace Nethermind.State.Flat.Rust.Test;

/// <summary>The state reader of each package: a state it does not hold, and the plain slot reads it serves.</summary>
/// <remarks>Not parallel: it reads a process-wide metric.</remarks>
[NonParallelizable]
public class ForkStateReaderTests
{
    private static readonly Address A = new("0x1111111100000000000000000000000000000001");

    [Test]
    public async Task Read_WhenStateUnavailable_ThrowsMissingTrieNodeException([Values] FlatDbBackend backend)
    {
        if (backend != FlatDbBackend.CSharp) RustNativeLibrary.Require();
        await using WorldStateNode node = new(backend);
        BlockHeader unknown = Build.A.BlockHeader.WithNumber(5).WithStateRoot(Keccak.Compute("unknown")).TestObject;

        Assert.That(() => node.WorldStateManager.GlobalStateReader.TryGetAccount(unknown, A, out _),
            Throws.TypeOf<MissingTrieNodeException>().With.InnerException.TypeOf<StateNotRetainedException>());
    }

    /// <summary>A plain slot read, as the state reader serves <c>eth_getStorageAt</c>, builds no slot filter over the snapshots.</summary>
    [Test]
    public async Task A_plain_slot_read_builds_no_slot_filter([Values] FlatDbBackend backend)
    {
        if (backend != FlatDbBackend.CSharp) RustNativeLibrary.Require();
        await using WorldStateNode node = new(backend);
        IStateReader reader = node.WorldStateManager.GlobalStateReader;
        BlockHeader? head = null;
        for (ulong number = 0; number < 3; number++)
        {
            head = WorldStateNode.Commit(node.WorldStateManager.GlobalWorldState, head, number, WorldStateNode.Write(A, new Account(number, number), false, (number + 1, number + 1)));
        }

        long builds = Publish(node, backend);
        reader.GetStorage(head!, A, 1, out UInt256 value);

        Assert.That((value, Publish(node, backend) - builds), Is.EqualTo(((UInt256)1, 0L)));
    }

    private static long Publish(WorldStateNode node, FlatDbBackend backend)
    {
        if (backend == FlatDbBackend.Rust) node.Resolve<Fork.NativeFlatManager>().PublishMetrics();
        return Metrics.InMemorySlotFilterBuilds;
    }
}
