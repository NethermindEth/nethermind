// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Reflection;
using Nethermind.Consensus.Stateless;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Trie;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.Stateless;

public class WitnessTrieStoreScopeProviderTests
{
    private const int KeyNibbles = 64;
    private const int BranchChildren = 16;
    private static readonly byte[] LeafValue = new byte[Hash256.Size];

    /// <remarks>The leaf sits in a slot no lookup reaches, so only a walk of the witness finds it.</remarks>
    [TestCase(KeyNibbles - 1, true, TestName = "Ends at the key")]
    [TestCase(KeyNibbles - 2, false, TestName = "Ends before the key")]
    [TestCase(KeyNibbles, false, TestName = "Exceeds the key across the branch")]
    [TestCase(KeyNibbles + 3, false, TestName = "Exceeds the key in the leaf alone")]
    public void Scope_opens_only_when_every_state_trie_path_ends_at_the_key(int unreadLeafNibbles, bool valid)
    {
        byte[] readLeaf = Leaf(KeyNibbles - 1);
        byte[] unreadLeaf = Leaf(unreadLeafNibbles);
        byte[] root = Branch(readLeaf, unreadLeaf);

        AssertScopeOpens(valid, readLeaf, unreadLeaf, root);
    }

    /// <remarks>Values of each RLP length form, so the leaf's list takes a short and a long prefix.</remarks>
    [Test]
    public void Scope_checks_leaves_whatever_their_value_length([Values(1, 20, 56, 200)] int valueLength, [Values] bool valid)
    {
        byte[] readLeaf = Leaf(KeyNibbles - 1);
        byte[] unreadLeaf = Leaf(KeyNibbles - 1 + (valid ? 0 : 1), new byte[valueLength]);

        AssertScopeOpens(valid, readLeaf, unreadLeaf, Branch(readLeaf, unreadLeaf));
    }

    /// <remarks>A branch of sixteen hashed children, most of them absent from the witness, as nearly all are.</remarks>
    [Test]
    public void Scope_checks_the_leaves_below_full_branches([Values(1, 2)] int levels, [Values] bool valid)
    {
        byte[] node = Leaf(KeyNibbles - levels + (valid ? 0 : 1));
        byte[][] state = new byte[levels + 1][];
        state[0] = node;
        for (int level = 1; level <= levels; level++)
        {
            byte[][] children = new byte[BranchChildren][];
            for (int i = 0; i < BranchChildren - 1; i++)
            {
                children[i] = Keccak.Compute([(byte)level, (byte)i]).BytesToArray();
            }

            children[^1] = node;
            state[level] = node = Branch(children);
        }

        AssertScopeOpens(valid, state);
    }

    /// <remarks>Every path to the leaf is checked, yet each node once per depth: walked path by path, this tree of 16^16
    /// leaves would not finish. The leaf is short enough to be inlined in the branches above it.</remarks>
    [Test]
    public void Scope_checks_a_node_every_child_shares_once_per_depth([Values] bool valid)
    {
        const int Levels = 16;
        byte[] node = Leaf(KeyNibbles - Levels + (valid ? 0 : 1), [0x01]);
        byte[][] state = new byte[Levels + 1][];
        state[0] = node;
        for (int level = 1; level <= Levels; level++)
        {
            byte[][] children = new byte[BranchChildren][];
            Array.Fill(children, node);
            state[level] = node = Branch(children);
        }

        AssertScopeOpens(valid, state);
    }

    /// <remarks>The walk's memo budget spans the block's walks: a fresh one per storage root would let roots sharing a
    /// subtree each walk twice the witness' bytes before the memo engages. A full branch whose children the witness lacks
    /// is walked on the fast path alone.</remarks>
    [Test]
    public void Walk_spends_one_memo_budget_across_walks()
    {
        byte[][] children = new byte[BranchChildren][];
        for (int i = 0; i < BranchChildren; i++)
        {
            children[i] = Keccak.Compute([(byte)i]).BytesToArray();
        }

        byte[] root = Branch(children);
        using Witness witness = CreateWitness(root);
        WitnessTrieStoreScopeProvider provider = new(witness, NullLogManager.Instance);
        FieldInfo budget = typeof(WitnessTrieStoreScopeProvider).GetField("_bytesBeforeMemo", BindingFlags.NonPublic | BindingFlags.Instance)!;
        long before = (long)budget.GetValue(provider)!;

        Assert.That(provider.TryBeginScope(Build.A.BlockHeader.WithStateRoot(Keccak.Compute(root)).TestObject, new LocalMetrics(),
            out IWorldStateScopeProvider.IScope scope), Is.True);
        scope.Dispose();

        Assert.That((long)budget.GetValue(provider)!, Is.EqualTo(before - root.Length));
    }

    public enum StorageAccess { Read, Write, Clear, ClearThenWrite }

    /// <remarks>As in the spec, a storage trie is checked once the block reads its old storage, or writes it without
    /// clearing it first. A clear discards the old storage undecoded, as a CREATE does at an address that has storage but
    /// no nonce or code.</remarks>
    [Test]
    public void Storage_trie_paths_are_checked_once_the_old_storage_is_used([Values] StorageAccess access)
    {
        byte[] storageLeaf = Leaf(KeyNibbles - 1);
        byte[] overlongStorageLeaf = Leaf(KeyNibbles);
        byte[] storageRoot = Branch(storageLeaf, overlongStorageLeaf);
        Account account = new Account(UInt256.One).WithChangedStorageRoot(Keccak.Compute(storageRoot));
        byte[] stateRoot = Rlp.Encode(
            Rlp.Encode(HexPrefix.ToBytes(Nibbles.BytesToNibbleBytes(Keccak.Compute(TestItem.AddressA.Bytes).Bytes), isLeaf: true)),
            Rlp.Encode(Rlp.Encode(account).Bytes)).Bytes;
        using Witness witness = CreateWitness(stateRoot, storageRoot, storageLeaf, overlongStorageLeaf);
        StatelessBlockProcessingEnv env = new(witness, new TestSpecProvider(Amsterdam.Instance), Always.Valid, NullLogManager.Instance);
        using IDisposable scope = env.WorldState.BeginScope(Build.A.BlockHeader.WithStateRoot(Keccak.Compute(stateRoot)).TestObject);

        Assert.That(env.WorldState.GetBalance(TestItem.AddressA), Is.EqualTo(UInt256.One));
        Assert.That(() =>
        {
            StorageCell cell = new(TestItem.AddressA, UInt256.One);
            switch (access)
            {
                case StorageAccess.Read:
                    env.WorldState.Get(cell, out _);
                    return;
                case StorageAccess.Write:
                    env.WorldState.Set(cell, UInt256.One);
                    break;
                case StorageAccess.Clear:
                    env.WorldState.ClearStorage(TestItem.AddressA);
                    break;
                case StorageAccess.ClearThenWrite:
                    env.WorldState.ClearStorage(TestItem.AddressA);
                    env.WorldState.Set(cell, UInt256.One);
                    break;
            }

            env.WorldState.Commit(Amsterdam.Instance);
            env.WorldState.CommitTree(1);
        }, access is StorageAccess.Read or StorageAccess.Write ? Throws.TypeOf<TrieException>() : Throws.Nothing);
    }

    /// <param name="state">The witness' state nodes, the state root last.</param>
    private static void AssertScopeOpens(bool valid, params byte[][] state)
    {
        using Witness witness = CreateWitness(state);
        StatelessBlockProcessingEnv env = new(witness, new TestSpecProvider(Amsterdam.Instance), Always.Valid, NullLogManager.Instance);

        Assert.That(() => env.WorldState.BeginScope(Build.A.BlockHeader.WithStateRoot(Keccak.Compute(state[^1])).TestObject).Dispose(),
            valid ? Throws.Nothing : Throws.TypeOf<TrieException>());
    }

    private static byte[] Leaf(int nibbles, byte[] value = null) =>
        Rlp.Encode(Rlp.Encode(HexPrefix.ToBytes(new byte[nibbles], isLeaf: true)), Rlp.Encode(value ?? LeafValue)).Bytes;

    /// <remarks>Inlines a child shorter than a keccak, as a trie does.</remarks>
    private static byte[] Branch(params byte[][] children)
    {
        Rlp[] items = new Rlp[17];
        Array.Fill(items, Rlp.OfEmptyByteArray);
        for (int i = 0; i < children.Length; i++)
        {
            items[i] = children[i].Length < Hash256.Size ? new Rlp(children[i]) : Rlp.Encode(Keccak.Compute(children[i]).Bytes);
        }

        return Rlp.Encode(items).Bytes;
    }

    private static Witness CreateWitness(params byte[][] state) => new()
    {
        Codes = ArrayPoolList<byte[]>.Empty(),
        State = new ArrayPoolList<byte[]>(state),
        Keys = ArrayPoolList<byte[]>.Empty(),
        Headers = ArrayPoolList<byte[]>.Empty()
    };
}
