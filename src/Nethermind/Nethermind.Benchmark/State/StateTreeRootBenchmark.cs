// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie.Pruning;

namespace Nethermind.Benchmarks.State;

/// <summary>
/// Measures <see cref="StateTree"/> root computation over a mix of dirty accounts. Account leaves in
/// this benchmark's 1000-account tree are all single-block for Keccak (measured &lt;= 135 bytes); the
/// two-block (&gt;= 136-byte) population in this tree is branch nodes, not leaves (see
/// <see cref="GenerateAccount"/> for the measurement).
/// </summary>
/// <remarks>
/// The timed region covers the <c>Set</c> loop as well as the root walk. On a 9950X at 500 dirty
/// accounts it reports about 1.8 ms, of which the walk is about 0.35 ms; trie descent and node
/// resolution account for the rest. Read it as an A/B between arms, not as a root-walk timing. The
/// writes cannot move to <see cref="IterationSetup"/> as it stands, because BenchmarkDotNet invokes
/// the method several times per iteration and a repeated write leaves the nodes clean; splitting
/// them out needs <c>UnrollFactor = 1</c> and a per-invocation variant index.
/// </remarks>
[MemoryDiagnoser]
[NoTieredCompilation]
public class StateTreeRootBenchmark
{
    private const int MaxAccounts = 1000;

    private static readonly Hash256 _contractStorageRoot = TestHash(8);
    private static readonly Hash256 _codeHash = TestHash(9);

    [Params(1, 8, 64, 1000)]
    public int DirtyAccounts { get; set; }

    private MemDb _backingDb = null!;
    private Address[] _addresses = null!;
    private Account[] _variantA = null!;
    private Account[] _variantB = null!;
    private Hash256 _baseRootHash = null!;

    private StateTree _tree = null!;
    private bool _parity;

    [GlobalSetup]
    public void GlobalSetup()
    {
        FlatWorldStateBenchmarkHarness.RequireTieredCompilationDisabled();

        _backingDb = new MemDb();
        _addresses = new Address[MaxAccounts];
        _variantA = new Account[MaxAccounts];
        _variantB = new Account[MaxAccounts];
        Account[] baseAccounts = new Account[MaxAccounts];

        for (int i = 0; i < MaxAccounts; i++)
        {
            _addresses[i] = Address.FromNumber((UInt256)(ulong)(i + 1));
            baseAccounts[i] = GenerateAccount(i, flavor: 0);
            _variantA[i] = GenerateAccount(i, flavor: 1);
            _variantB[i] = GenerateAccount(i, flavor: 2);
        }

        StateTree setupTree = new(new RawScopedTrieStore(_backingDb), NullLogManager.Instance);
        for (int i = 0; i < MaxAccounts; i++)
        {
            setupTree.Set(_addresses[i], baseAccounts[i]);
        }
        setupTree.Commit();
        _baseRootHash = setupTree.RootHash;
    }

    [IterationSetup]
    public void IterationSetup()
    {
        // A fresh StateTree/RawScopedTrieStore pair per iteration carries no resolved-node cache from a
        // prior iteration, so every dirty account and its ancestor branch/extension nodes are resolved
        // and re-hashed for real rather than reusing another iteration's in-memory node graph.
        _tree = new StateTree(new RawScopedTrieStore(_backingDb), NullLogManager.Instance) { RootHash = _baseRootHash };
        _parity = false;
    }

    [IterationCleanup]
    public void IterationCleanup() => _tree = null!;

    [Benchmark]
    public Hash256 UpdateAccountsAndComputeRoot()
    {
        // Alternating between two distinct account-value sets on every call (rather than repeatedly
        // writing the same values) guarantees each invocation actually changes the leaf bytes, even
        // when BenchmarkDotNet invokes this method several times against the one tree built by
        // IterationSetup. Writing the same value twice in a row would leave the touched nodes clean,
        // so UpdateRootHash would have nothing to re-hash on the second call - a silent no-op.
        Account[] variants = _parity ? _variantB : _variantA;
        _parity = !_parity;

        for (int i = 0; i < DirtyAccounts; i++)
        {
            _tree.Set(_addresses[i], variants[i]);
        }
        _tree.UpdateRootHash();
        return _tree.RootHash;
    }

    /// <summary>
    /// Generates account field values that alternate, by index parity, between a "small" account with a
    /// short nonce/balance encoding and a "large" one with the widest nonce (8 significant bytes) and a
    /// balance built to need a wide encoding too (20 significant bytes).
    /// </summary>
    /// <remarks>
    /// Measured against the actual tree this benchmark builds (1000 accounts committed to a
    /// <see cref="StateTree"/>, every stored node walked and RLP-classified as leaf/extension/branch):
    /// all 1000 leaves are single-block, ranging from 111 to exactly 135 bytes - never 136, regardless
    /// of tier. A single-entry tree (one leaf carrying the full 64-nibble path) measures larger for the
    /// same "large" account - 136 bytes, versus 114 for the same "small" one - but that is not the node
    /// shape this benchmark's leaves take: in the real tree, shared address prefixes are consumed by
    /// branch/extension nodes above each leaf, shortening its remaining path and so its RLP length.
    /// <para/>
    /// 135 is in fact the largest message length that still fits Keccak's 136-byte block: the padding
    /// appends a start byte (0x01) and ORs a stop bit into the final padding byte (0x80). When exactly
    /// one byte of the block is left after the message (message length = rate - 1 = 135), those two land
    /// in the same trailing byte as 0x81 and no second block is needed; at 136 bytes the block is already
    /// full, so the pad byte has no room and forces a whole second block. The "large" tier's account
    /// content is sized to sit at that edge once embedded as a real tree leaf.
    /// <para/>
    /// Two-block (&gt;= 136-byte) nodes do occur in this tree, but as branch nodes: 146 of 340 branch
    /// nodes measured &gt;= 136 bytes, up to 532 bytes (a branch can carry up to 17 encoded children).
    /// <para/>
    /// Code hash and storage root were measured to have no effect on account RLP content length, both in
    /// isolation (72 bytes whether an account has no code/storage, code only, or code and storage) and
    /// for the actual generated pool (index 1, which gets code/storage, and index 3, which does not,
    /// both encode to 98 bytes): <see cref="StateTree"/> uses the non-slim <c>AccountDecoder</c>, which
    /// always encodes both fields as full 32-byte hashes regardless of emptiness. Only nonce and balance
    /// magnitude change the account's contribution to its eventual leaf length. The <paramref name="flavor"/>
    /// parameter only perturbs the low bits of the balance (verified to never change which tier - small or
    /// large - the account's encoding belongs to), giving distinct same-tier variants to update to.
    /// </remarks>
    private static Account GenerateAccount(int index, int flavor)
    {
        bool large = (index % 2) == 1;
        ulong nonce = large ? (ulong.MaxValue - (ulong)index) : (ulong)(index + 1);
        UInt256 balanceBase = large
            ? (UInt256.One << 152) + (UInt256)(uint)index
            : (UInt256)(uint)(index + 1) << 40;
        UInt256 balance = balanceBase ^ (UInt256)(uint)flavor;

        if (large && (index % 4) == 1)
        {
            return new Account(nonce, balance, _contractStorageRoot, _codeHash);
        }
        return new Account(nonce, balance);
    }

    private static Hash256 TestHash(byte seed)
    {
        byte[] bytes = new byte[32];
        Array.Fill(bytes, seed);
        return new Hash256(bytes);
    }
}
