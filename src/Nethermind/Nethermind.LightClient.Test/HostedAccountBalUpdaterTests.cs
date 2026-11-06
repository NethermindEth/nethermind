// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Serialization.Rlp.Eip7928;
using Nethermind.Trie;

namespace Nethermind.LightClient.Test;

[TestFixture]
public class HostedAccountBalUpdaterTests
{
    private static readonly Address AccountAddress = new("0xde198901C5ee4611142E0c61DfbbCEc7ab468F32");
    private static readonly Address OtherAddress = new("0x50614cc8e44f7814549c223aa31db9296e58057c");

    [Test]
    public async Task Applies_final_slot_changes_in_chain_order_without_mutating_previous_storage([Values(1, 2)] int blocks)
    {
        Source source = new(blocks);
        Dictionary<ValueHash256, UInt256> previous = Storage((1, 1), (2, 2), (4, 44));
        Dictionary<ValueHash256, UInt256> expected = blocks == 1
            ? Storage((1, 12), (4, 44), (5, 55), (UInt256.MaxValue, 77))
            : Storage((1, 13), (3, 33), (4, 44), (UInt256.MaxValue, 77));
        source.SetAccessList(0, AccessList(AccountAddress,
            [new(1, [new(1, 11), new(2, 12)]), new(2, [new(1, 0)]), new(5, [new(1, 55)]), new(UInt256.MaxValue, [new(2, 77)])],
            [99]));
        if (blocks == 2)
            source.SetAccessList(1, AccessList(AccountAddress,
                [new(1, [new(1, 13)]), new(3, [new(1, 33)]), new(5, [new(1, 0)])]));

        IReadOnlyDictionary<ValueHash256, UInt256>? result = await Update(source, previous, Root(expected));

        Assert.That(result, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EquivalentTo(expected));
            Assert.That(result, Is.Not.SameAs(previous));
            Assert.That(previous, Is.EquivalentTo(Storage((1, 1), (2, 2), (4, 44))));
            Assert.That(source.Requests, Is.EqualTo(blocks == 1
                ? new[] { "header:101", "bal:101" }
                : new[] { "header:102", "header:101", "bal:101", "bal:102" }));
        }
    }

    [Test]
    public async Task Reads_and_other_accounts_do_not_modify_retained_storage([Values] bool targetRootMatches)
    {
        Source source = new(2);
        Dictionary<ValueHash256, UInt256> previous = Storage((1, 42));
        source.SetAccessList(0, AccessList(OtherAddress, [new(1, [new(1, 999)])]));
        source.SetAccessList(1, AccessList(AccountAddress, [], [1, 2, 3]));

        IReadOnlyDictionary<ValueHash256, UInt256>? result = await Update(source, previous,
            targetRootMatches ? Root(previous) : Root(Storage((1, 999))));

        if (targetRootMatches) Assert.That(result, Is.EquivalentTo(previous));
        else Assert.That(result, Is.Null);
        Assert.That(previous, Is.EquivalentTo(Storage((1, 42))));
    }

    [Test]
    public async Task Removing_the_last_slot_produces_the_empty_storage_root()
    {
        Source source = new(1);
        source.SetAccessList(0, AccessList(AccountAddress, [new(1, [new(1, 0)])]));

        IReadOnlyDictionary<ValueHash256, UInt256>? result = await Update(source, Storage((1, 42)), Keccak.EmptyTreeHash);

        Assert.That(result, Is.Not.Null.And.Empty);
    }

    [Test]
    public async Task Incomplete_or_invalid_updates_fall_back_without_publishing_partial_changes(
        [Values("root", "unavailable", "missing-bal", "empty-change", "wire-hash", "cancelled-source")] string failure)
    {
        Source source = new(2);
        Dictionary<ValueHash256, UInt256> previous = Storage((1, 42));
        source.SetAccessList(0, AccessList(AccountAddress, [new(1, [new(1, 43)])]));
        source.SetAccessList(1, AccessList(AccountAddress, [new(1, [new(1, 44)])]));
        Hash256 expectedRoot = Root(Storage((1, failure == "root" ? (UInt256)45 : (UInt256)44)));
        switch (failure)
        {
            case "unavailable": source.BalFailure = new NotSupportedException(); break;
            case "missing-bal": source.AccessLists.Remove(1); break;
            case "empty-change": source.AccessLists[1] = AccessList(AccountAddress, [new(1, [])]); break;
            case "wire-hash": source.AccessLists[1] = new([], 0, Keccak.Compute("uncommitted BAL")); break;
            case "cancelled-source": source.BalFailure = new OperationCanceledException(new CancellationToken(canceled: true)); break;
        }

        IReadOnlyDictionary<ValueHash256, UInt256>? result = await Update(source, previous, expectedRoot);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.Null);
            Assert.That(previous, Is.EquivalentTo(Storage((1, 42))));
        }
        source.BalFailure = null;
        source.SetAccessList(1, AccessList(AccountAddress, [new(1, [new(1, 44)])]));
        Assert.That(await Update(source, previous, Root(Storage((1, 44)))), Is.EquivalentTo(Storage((1, 44))));
    }

    [Test]
    public async Task Authenticates_the_entire_ancestry_before_requesting_any_bal(
        [Values("target-hash", "cached-hash", "target-number", "target-state", "intermediate-hash",
            "intermediate-number", "parent", "target-no-bal", "intermediate-no-bal", "missing-header")] string failure)
    {
        Source source = new(2);
        switch (failure)
        {
            case "target-hash": source.Headers[1].ExtraData = [1]; break;
            case "cached-hash": source.Headers[1].Hash = Keccak.Compute("forged cached hash"); break;
            case "target-number": source.Headers[1].Number++; source.Rehash(); break;
            case "target-state": source.Target = source.Target with { StateRoot = Keccak.Compute("unverified state") }; break;
            case "intermediate-hash": source.Headers[0].ExtraData = [1]; break;
            case "intermediate-number": source.Headers[0].Number--; source.Rehash(); break;
            case "parent": source.Headers[0].ParentHash = Keccak.Compute("other branch"); source.Rehash(); break;
            case "target-no-bal": source.Headers[1].BlockAccessListHash = null; source.Rehash(); break;
            case "intermediate-no-bal": source.Headers[0].BlockAccessListHash = null; source.Rehash(); break;
            case "missing-header": source.MissingIntermediate = true; break;
        }

        IReadOnlyDictionary<ValueHash256, UInt256>? result = await Update(source, Storage((1, 42)), Root(Storage((1, 42))));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.Null);
            Assert.That(source.Requests, Has.None.StartsWith("bal:"));
        }
    }

    [Test]
    public async Task Does_not_request_headers_for_reverse_reorgs_same_height_or_excessive_gaps(
        [Values(99UL, 100UL, 357UL)] ulong number)
    {
        Source source = new(1);
        source.Target = source.Target with { Number = number };

        IReadOnlyDictionary<ValueHash256, UInt256>? result = await Update(source, Storage((1, 42)), Root(Storage((1, 42))));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.Null);
            Assert.That(source.Requests, Is.Empty);
        }
    }

    [Test]
    public async Task Accepts_the_maximum_supported_forward_gap()
    {
        Source source = new(256);
        Dictionary<ValueHash256, UInt256> previous = Storage((1, 42));

        IReadOnlyDictionary<ValueHash256, UInt256>? result = await Update(source, previous, Root(previous));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EquivalentTo(previous));
            Assert.That(source.Requests.Count(request => request.StartsWith("bal:", StringComparison.Ordinal)), Is.EqualTo(256));
        }
    }

    [Test]
    public async Task Caller_cancellation_during_a_bal_fetch_propagates_and_keeps_the_previous_snapshot()
    {
        Source source = new(1) { BalGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        Dictionary<ValueHash256, UInt256> previous = Storage((1, 42));
        using CancellationTokenSource cancellation = new();
        Task<IReadOnlyDictionary<ValueHash256, UInt256>?> update = Update(source, previous, Root(previous), cancellation.Token);
        await source.BalStarted.Task;

        cancellation.Cancel();

        await Assert.ThatAsync(async () => await update, Throws.InstanceOf<OperationCanceledException>());
        Assert.That(previous, Is.EquivalentTo(Storage((1, 42))));
    }

    [Test]
    public async Task Caller_cancellation_is_checked_before_reorg_fallback()
    {
        Source source = new(1);
        source.Target = source.Previous;

        await Assert.ThatAsync(async () =>
            await Update(source, Storage((1, 42)), Keccak.EmptyTreeHash, new CancellationToken(canceled: true)),
            Throws.InstanceOf<OperationCanceledException>());
        Assert.That(source.Requests, Is.Empty);
    }

    private static Task<IReadOnlyDictionary<ValueHash256, UInt256>?> Update(Source source,
        IReadOnlyDictionary<ValueHash256, UInt256> previous, Hash256 targetRoot, CancellationToken cancellationToken = default) =>
        new HostedAccountBalUpdater(source, LimboLogs.Instance.GetClassLogger<HostedAccountBalUpdaterTests>())
            .TryUpdateAsync(source.Previous, source.Target, AccountAddress, previous, targetRoot, cancellationToken);

    private static ReadOnlyBlockAccessList AccessList(Address address, ReadOnlySlotChanges[] changes, UInt256[]? reads = null) =>
        new([new(address, changes, reads ?? [], [], [], [])], changes.Length + (reads?.Length ?? 0));

    private static Dictionary<ValueHash256, UInt256> Storage(params (UInt256 Slot, UInt256 Value)[] entries) =>
        entries.ToDictionary(entry => ValueKeccak.Compute(entry.Slot.ToBigEndian()), entry => entry.Value);

    private static Hash256 Root(IReadOnlyDictionary<ValueHash256, UInt256> storage)
    {
        PatriciaTree trie = new();
        foreach ((ValueHash256 path, UInt256 value) in storage) trie.Set(path.Bytes, Rlp.Encode(value).Bytes);
        trie.UpdateRootHash();
        return trie.RootHash;
    }

    private sealed class Source : IExecutionStateSource
    {
        internal readonly VerifiedHead Previous = new(100, 100, Keccak.Compute("previous block"), Keccak.Compute("previous state"));
        internal readonly BlockHeader[] Headers;
        internal readonly Dictionary<int, ReadOnlyBlockAccessList> AccessLists = [];
        internal readonly List<string> Requests = [];
        internal readonly TaskCompletionSource BalStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal VerifiedHead Target;
        internal Exception? BalFailure;
        internal TaskCompletionSource? BalGate;
        internal bool MissingIntermediate;

        internal Source(int blocks)
        {
            Headers = new BlockHeader[blocks];
            for (int i = 0; i < blocks; i++)
            {
                Headers[i] = new(Previous.BlockHash, Keccak.OfAnEmptySequenceRlp, Address.Zero, UInt256.Zero,
                    Previous.Number + (ulong)i + 1, 30_000_000, 1_800_000_000 + (ulong)i, [])
                {
                    StateRoot = Keccak.Compute($"state {i}"),
                    BlockAccessListHash = Keccak.OfAnEmptySequenceRlp,
                };
                AccessLists[i] = BlockAccessListDecoder.Instance.DecodeCompleteNotNull([0xc0]);
            }
            Rehash();
            Target = new(Previous.Slot + (ulong)blocks, Previous.Number + (ulong)blocks, Headers[^1].Hash!, Headers[^1].StateRoot!);
        }

        internal void SetAccessList(int index, ReadOnlyBlockAccessList accessList)
        {
            byte[] encoded = BlockAccessListDecoder.EncodeToBytes(accessList);
            AccessLists[index] = BlockAccessListDecoder.Instance.DecodeCompleteNotNull(encoded);
            Headers[index].BlockAccessListHash = Keccak.Compute(encoded);
            Rehash();
        }

        internal void Rehash()
        {
            for (int i = 0; i < Headers.Length; i++)
            {
                if (i > 0) Headers[i].ParentHash = Headers[i - 1].Hash;
                Headers[i].Hash = Headers[i].CalculateHash();
            }
            if (Target is not null) Target = Target with { BlockHash = Headers[^1].Hash! };
        }

        public Task<BlockHeader> GetHeaderAsync(VerifiedHead head, CancellationToken cancellationToken)
        {
            Requests.Add($"header:{Headers[^1].Number}");
            return Task.FromResult(Headers[^1]);
        }

        public Task<BlockHeader> GetHeaderByHashAsync(Hash256 hash, CancellationToken cancellationToken)
        {
            if (MissingIntermediate) throw new InvalidDataException("Missing header.");
            BlockHeader header = Headers.Single(header => header.Hash == hash);
            Requests.Add($"header:{header.Number}");
            return Task.FromResult(header);
        }

        public async Task<ReadOnlyBlockAccessList> GetBlockAccessListAsync(BlockHeader header, CancellationToken cancellationToken)
        {
            Requests.Add($"bal:{header.Number}");
            if (BalFailure is not null) throw BalFailure;
            BalStarted.TrySetResult();
            if (BalGate is not null) await BalGate.Task.WaitAsync(cancellationToken);
            return AccessLists[Array.IndexOf(Headers, header)];
        }

        public Task<Account> GetAccountAsync(VerifiedHead head, Address address, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<UInt256> GetStorageAsync(VerifiedHead head, Address address, Account account, UInt256 key, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<byte[]> GetCodeAsync(Account account, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Hash256[]> GetAncestorHashesAsync(VerifiedHead head, ulong firstNumber, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
