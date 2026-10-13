// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Trie;
using BlockPlan = Nethermind.State.Flat.Rust.Test.RustFlatDbManagerDifferentialTests.BlockPlan;

namespace Nethermind.State.Flat.Rust.Test;

/// <summary>
/// A small chain for the tests of the forked bundles: block 0 persisted, blocks 1 and 2 in memory. Block 0 holds
/// <see cref="Persisted"/> with a slot and the nodes at <see cref="PersistedPath"/> and <see cref="ShadowedPath"/>;
/// block 1 holds <see cref="InMemory"/> with a slot, a node larger than a stack buffer at <see cref="InMemoryPath"/>
/// and a placeholder at <see cref="ShadowedPath"/>, and clears the storage of <see cref="Persisted"/> when asked.
/// </summary>
internal static class ForkTestChain
{
    public static readonly Address Persisted = new("0xdddddddd00000000000000000000000000000001");
    public static readonly Address InMemory = new("0xdddddddd00000000000000000000000000000002");
    public static readonly Address Absent = new("0xdddddddd00000000000000000000000000000003");
    public static readonly Hash256 PersistedOwner = Persisted.ToAccountPath.ToCommitment();
    public static readonly TreePath PersistedPath = TreePath.FromHexString("1");
    public static readonly TreePath InMemoryPath = TreePath.FromHexString("2");
    public static readonly TreePath ShadowedPath = TreePath.FromHexString("3");
    public static readonly Hash256 PersistedHash = Keccak.Compute("persisted");
    public static readonly Hash256 InMemoryHash = Keccak.Compute("in memory");
    public static readonly Hash256 ShadowedHash = Keccak.Compute("shadowed");
    public static readonly Hash256 PlaceholderHash = Keccak.Compute("placeholder");
    public static readonly byte[] PersistedRlp = Bytes.FromHexString("0xc3820101");
    public static readonly byte[] ShadowedRlp = Bytes.FromHexString("0xc3820303");
    public static readonly byte[] InMemoryRlp = Bytes.FromHexString($"0xb907d0{new string('a', 4000)}");

    /// <summary>Adds the chain to <paramref name="container"/>.</summary>
    /// <returns>The states of blocks 0, 1 and 2.</returns>
    public static StateId[] Build(FlatManagerContainer container, bool clearInMemory)
    {
        StateId[] blocks = [Block(0), Block(1), Block(2)];
        container.Arm.Add(new BlockPlan(
            [(Persisted, new Account(1, 100))],
            [(Persisted, 1, 11)],
            [],
            [(PersistedPath, PersistedHash, PersistedRlp), (ShadowedPath, ShadowedHash, ShadowedRlp)],
            [(PersistedOwner, PersistedPath, PersistedHash, PersistedRlp)]), StateId.PreGenesis, blocks[0]);
        container.Arm.FlushCache(CancellationToken.None);
        container.Arm.Add(new BlockPlan(
            [(InMemory, new Account(2, 200))],
            [(InMemory, 1, 22)],
            clearInMemory ? [(Persisted, false)] : [],
            [(InMemoryPath, InMemoryHash, InMemoryRlp), (ShadowedPath, PlaceholderHash, null)],
            []), blocks[0], blocks[1]);
        container.Arm.Add(BlockPlan.Empty, blocks[1], blocks[2]);
        return blocks;
    }

    private static StateId Block(ulong number) => new(number, Keccak.Compute($"fork test chain {number}"));
}
