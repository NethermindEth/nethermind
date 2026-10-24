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
public class HostedAccountStateTests
{
    private const string AddressText = "0xde198901C5ee4611142E0c61DfbbCEc7ab468F32";
    private static readonly Address HostedAddress = new(AddressText);
    private static readonly Address OtherAddress = new("0x50614cc8e44f7814549c223aa31db9296e58057c");
    private static readonly VerifiedHead FirstHead = new(100, 20_000_000, Keccak.Compute("first block"), Keccak.Compute("first state"));
    private static readonly VerifiedHead NextHead = new(101, 20_000_001, Keccak.Compute("next block"), Keccak.Compute("next state"));

    [Test]
    public void Account_option_is_disabled_when_absent() => Assert.That(HostedAccountState.ParseAccounts(null), Is.Empty);

    [Test]
    public void Account_option_preserves_order_and_deduplicates_hex_case()
    {
        Address[] accounts = HostedAccountState.ParseAccounts($" {AddressText}, {OtherAddress}, {AddressText.ToLowerInvariant()} ");

        Assert.That(accounts, Is.EqualTo(new[] { HostedAddress, OtherAddress }));
    }

    [Test]
    public void Account_option_rejects_malformed_or_empty_entries(
        [Values("", " ", ",", "0x1234", "de198901C5ee4611142E0c61DfbbCEc7ab468F32",
            "0Xde198901C5ee4611142E0c61DfbbCEc7ab468F32", "0xge198901C5ee4611142E0c61DfbbCEc7ab468F32",
            AddressText + ",", "," + AddressText, AddressText + ", ," + AddressText)] string value) =>
        Assert.That(() => HostedAccountState.ParseAccounts(value), Throws.ArgumentException);

    [Test]
    public async Task Complete_snapshot_serves_known_and_missing_slots_locally()
    {
        Source source = new();
        HostedAccountState hosted = Create(source);
        await hosted.RefreshAsync(FirstHead, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(hosted.TryGetAccount(FirstHead, HostedAddress, out Account account), Is.True);
            Assert.That(account, Is.EqualTo(source.Account));
            Assert.That(hosted.TryGetStorage(HostedAddress, account, 1, out UInt256 known), Is.True);
            Assert.That(known, Is.EqualTo((UInt256)42));
            Assert.That(hosted.TryGetStorage(HostedAddress, account, UInt256.MaxValue, out UInt256 missing), Is.True);
            Assert.That(missing, Is.EqualTo(UInt256.Zero));
            Assert.That(hosted.TryGetCode(account, out byte[] code), Is.True);
            Assert.That(code, Is.EqualTo(source.Code));
            Assert.That(source.AccountFetches, Is.EqualTo(1));
            Assert.That(source.StorageFetches, Is.EqualTo(1));
            Assert.That(source.CodeFetches, Is.EqualTo(1));
            Assert.That(hosted.GetProgress(FirstHead), Is.EqualTo(new[]
            {
                new HostedAccountProgress(HostedAddress.ToString(), FirstHead.Number, FirstHead.BlockHash.ToString(),
                    FirstHead.StateRoot.ToString(), source.Storage.Count, source.Code.Length, true, "complete", null, null),
            }));
            Assert.That(hosted.GetProgress(NextHead)[0].Current, Is.False);
        }
    }

    [Test]
    public async Task Unconfigured_accounts_are_never_retained()
    {
        Source source = new();
        HostedAccountState hosted = Create(source);
        await hosted.RefreshAsync(FirstHead, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(hosted.TryGetAccount(FirstHead, OtherAddress, out _), Is.False);
            Assert.That(hosted.TryGetStorage(OtherAddress, source.Account, 1, out _), Is.False);
            Assert.That(source.RequestedAddresses, Is.EqualTo(new[] { HostedAddress }));
        }
    }

    [Test]
    public async Task New_head_reauthenticates_account_and_reuses_unchanged_storage_and_code()
    {
        Source source = new();
        HostedAccountState hosted = Create(source);
        await hosted.RefreshAsync(FirstHead, CancellationToken.None);
        source.Account = source.Account.WithChangedBalance(999);
        await hosted.RefreshAsync(NextHead, CancellationToken.None);
        await hosted.RefreshAsync(NextHead, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(hosted.TryGetAccount(FirstHead, HostedAddress, out _), Is.False);
            Assert.That(hosted.TryGetAccount(NextHead, HostedAddress, out Account account), Is.True);
            Assert.That(account.Balance, Is.EqualTo((UInt256)999));
            Assert.That(hosted.TryGetStorage(HostedAddress, account, 1, out UInt256 value), Is.True);
            Assert.That(value, Is.EqualTo((UInt256)42));
            Assert.That(source.AccountFetches, Is.EqualTo(2));
            Assert.That(source.StorageFetches, Is.EqualTo(1));
            Assert.That(source.CodeFetches, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task Changed_storage_and_code_replace_the_previous_snapshot()
    {
        Source source = new();
        HostedAccountState hosted = Create(source);
        await hosted.RefreshAsync(FirstHead, CancellationToken.None);
        Account previous = source.Account;
        source.Code = [0x60, 0x02, 0x00];
        source.Account = new(8, 456, Keccak.Compute("changed storage"), Keccak.Compute(source.Code));
        source.Storage = new() { [Path(2)] = 123 };
        await hosted.RefreshAsync(NextHead, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(hosted.TryGetStorage(HostedAddress, previous, 1, out _), Is.False);
            Assert.That(hosted.TryGetCode(previous, out _), Is.False);
            Assert.That(hosted.TryGetAccount(NextHead, HostedAddress, out Account account), Is.True);
            Assert.That(hosted.TryGetStorage(HostedAddress, account, 1, out UInt256 deleted), Is.True);
            Assert.That(deleted, Is.EqualTo(UInt256.Zero));
            Assert.That(hosted.TryGetStorage(HostedAddress, account, 2, out UInt256 added), Is.True);
            Assert.That(added, Is.EqualTo((UInt256)123));
            Assert.That(hosted.TryGetCode(account, out byte[] code), Is.True);
            Assert.That(code, Is.EqualTo(source.Code));
            Assert.That(source.StorageFetches, Is.EqualTo(2));
            Assert.That(source.CodeFetches, Is.EqualTo(2));
        }
    }

    [Test]
    public async Task BAL_updates_are_automatic_after_the_fork_and_failed_updates_use_the_full_download(
        [Values("before-fork", "after-fork", "fallback", "failed-fallback", "code-failure")] string mode)
    {
        Source source = new();
        source.Account = source.Account.WithChangedStorageRoot(StorageRoot(source.Storage));
        Account original = source.Account;
        HostedAccountState hosted = Create(source);
        await hosted.RefreshAsync(FirstHead, CancellationToken.None);

        source.Storage = new() { [Path(1)] = 99 };
        source.Code = [0x60, 0x02, 0x00];
        source.Account = new(9, 789, StorageRoot(source.Storage), Keccak.Compute(source.Code));
        source.Header = new(FirstHead.BlockHash, Keccak.OfAnEmptySequenceRlp, Address.Zero, UInt256.Zero,
            NextHead.Number, 30_000_000, 1_800_000_000, [])
        {
            StateRoot = NextHead.StateRoot,
            BlockAccessListHash = Keccak.OfAnEmptySequenceRlp,
        };
        source.Bal = new([new(HostedAddress,
            [new(1, [new(1, mode is "fallback" or "failed-fallback" ? (UInt256)100 : (UInt256)99)])], [], [], [], [])], 2);
        byte[] encodedBal = BlockAccessListDecoder.EncodeToBytes(source.Bal);
        source.Header.BlockAccessListHash = mode == "before-fork" ? null : Keccak.Compute(encodedBal);
        source.Header.Hash = source.Header.CalculateHash();
        source.Bal = BlockAccessListDecoder.Instance.DecodeCompleteNotNull(encodedBal);
        source.FailStage = mode switch { "failed-fallback" => "storage", "code-failure" => "code", _ => null };
        VerifiedHead next = NextHead with { BlockHash = source.Header.Hash };

        await hosted.RefreshAsync(next, CancellationToken.None);

        if (mode is "failed-fallback" or "code-failure")
        {
            Assert.That(hosted.TryGetAccount(next, HostedAddress, out _), Is.False);
            Assert.That(hosted.TryGetAccount(FirstHead, HostedAddress, out Account retained), Is.True);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(retained, Is.EqualTo(original));
                Assert.That(hosted.TryGetStorage(HostedAddress, retained, 1, out UInt256 value), Is.True);
                Assert.That(value, Is.EqualTo((UInt256)42));
                Assert.That(source.StorageFetches, Is.EqualTo(mode == "failed-fallback" ? 2 : 1));
            }
            return;
        }
        Assert.That(hosted.TryGetAccount(next, HostedAddress, out Account current), Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(current.Nonce, Is.EqualTo(9));
            Assert.That(current.Balance, Is.EqualTo((UInt256)789));
            Assert.That(hosted.TryGetStorage(HostedAddress, current, 1, out UInt256 value), Is.True);
            Assert.That(value, Is.EqualTo((UInt256)99));
            Assert.That(hosted.TryGetCode(current, out byte[] code), Is.True);
            Assert.That(code, Is.EqualTo(source.Code));
            Assert.That(source.StorageFetches, Is.EqualTo(mode == "after-fork" ? 1 : 2));
            Assert.That(source.BalFetches, Is.EqualTo(mode == "before-fork" ? 0 : 1));
            Assert.That(source.HeaderFetches, Is.EqualTo(1));
            Assert.That(source.AccountFetches, Is.EqualTo(2));
            Assert.That(source.CodeFetches, Is.EqualTo(2));
        }
    }

    [Test]
    public async Task Reorg_reauthenticates_the_selected_branch_without_relabeling_state()
    {
        Source source = new();
        HostedAccountState hosted = Create(source);
        await hosted.RefreshAsync(FirstHead, CancellationToken.None);
        source.Account = source.Account.WithChangedNonce(8);
        await hosted.RefreshAsync(NextHead, CancellationToken.None);
        VerifiedHead reorg = FirstHead with { BlockHash = Keccak.Compute("replacement block"), StateRoot = Keccak.Compute("replacement state") };
        source.Account = source.Account.WithChangedNonce(9);
        await hosted.RefreshAsync(reorg, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(hosted.TryGetAccount(FirstHead, HostedAddress, out _), Is.False);
            Assert.That(hosted.TryGetAccount(NextHead, HostedAddress, out _), Is.False);
            Assert.That(hosted.TryGetAccount(reorg, HostedAddress, out Account account), Is.True);
            Assert.That(account.Nonce, Is.EqualTo(9));
            Assert.That(source.AccountFetches, Is.EqualTo(3));
        }
    }

    [Test]
    public async Task Failed_refresh_preserves_previous_snapshot_and_retries_the_same_head(
        [Values("account", "storage", "code")] string stage)
    {
        Source source = new();
        HostedAccountState hosted = Create(source);
        await hosted.RefreshAsync(FirstHead, CancellationToken.None);
        Account previous = source.Account;
        source.Account = new(8, 999, Keccak.Compute("new storage"), Keccak.Compute("new code"));
        source.FailStage = stage;
        await hosted.RefreshAsync(NextHead, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(hosted.TryGetAccount(FirstHead, HostedAddress, out Account account), Is.True);
            Assert.That(account, Is.EqualTo(previous));
            Assert.That(hosted.TryGetAccount(NextHead, HostedAddress, out _), Is.False);
            Assert.That(hosted.TryGetStorage(HostedAddress, previous, 1, out UInt256 value), Is.True);
            Assert.That(value, Is.EqualTo((UInt256)42));
            Assert.That(hosted.GetProgress(NextHead)[0].Stage, Is.EqualTo("failed"));
            Assert.That(hosted.GetProgress(NextHead)[0].BlockNumber, Is.EqualTo(FirstHead.Number));
            Assert.That(hosted.GetProgress(NextHead)[0].RefreshBlock, Is.EqualTo(NextHead.Number));
            Assert.That(hosted.GetProgress(NextHead)[0].Current, Is.False);
            Assert.That(hosted.GetProgress(NextHead)[0].LastError, Is.Not.Null.And.Not.Empty);
        }

        source.FailStage = null;
        source.Account = source.Account.WithChangedCodeHash(Keccak.Compute(source.Code));
        await hosted.RefreshAsync(NextHead, CancellationToken.None);
        Assert.That(hosted.TryGetAccount(NextHead, HostedAddress, out _), Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(hosted.GetProgress(NextHead)[0].Stage, Is.EqualTo("complete"));
            Assert.That(hosted.GetProgress(NextHead)[0].Current, Is.True);
            Assert.That(hosted.GetProgress(NextHead)[0].LastError, Is.Null);
        }
    }

    [Test]
    public async Task Invalid_code_hash_prevents_snapshot_publication()
    {
        Source source = new();
        source.Account = source.Account.WithChangedCodeHash(Keccak.Compute("different code"));
        HostedAccountState hosted = Create(source);

        await hosted.RefreshAsync(FirstHead, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(hosted.TryGetAccount(FirstHead, HostedAddress, out _), Is.False);
            Assert.That(hosted.TryGetCode(source.Account, out _), Is.False);
            Assert.That(hosted.TryGetStorage(HostedAddress, source.Account, 1, out _), Is.False);
        }
    }

    [Test]
    public async Task Failed_account_does_not_prevent_other_accounts_from_refreshing()
    {
        Source source = new() { FailAddress = HostedAddress };
        HostedAccountState hosted = Create(source, [HostedAddress, OtherAddress]);

        await hosted.RefreshAsync(FirstHead, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(hosted.TryGetAccount(FirstHead, HostedAddress, out _), Is.False);
            Assert.That(hosted.TryGetAccount(FirstHead, OtherAddress, out _), Is.True);
        }
        source.FailAddress = null;
        await hosted.RefreshAsync(FirstHead, CancellationToken.None);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(hosted.TryGetAccount(FirstHead, HostedAddress, out _), Is.True);
            Assert.That(source.AccountFetches, Is.EqualTo(3));
        }
    }

    [Test]
    public async Task Empty_accounts_need_no_storage_or_code_download([Values] bool absent)
    {
        Source source = new() { Account = absent ? Account.TotallyEmpty : new Account(7, 123) };
        HostedAccountState hosted = Create(source);

        await hosted.RefreshAsync(FirstHead, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(hosted.TryGetAccount(FirstHead, HostedAddress, out Account account), Is.True);
            Assert.That(account, Is.EqualTo(source.Account));
            Assert.That(hosted.TryGetStorage(HostedAddress, account, 1, out UInt256 value), Is.True);
            Assert.That(value, Is.EqualTo(UInt256.Zero));
            Assert.That(hosted.TryGetCode(account, out byte[] code), Is.True);
            Assert.That(code, Is.Empty);
            Assert.That(source.StorageFetches, Is.Zero);
            Assert.That(source.CodeFetches, Is.Zero);
        }
    }

    [Test]
    public async Task Account_deletion_replaces_storage_and_code_with_authenticated_empty_state()
    {
        Source source = new();
        HostedAccountState hosted = Create(source);
        await hosted.RefreshAsync(FirstHead, CancellationToken.None);
        Account previous = source.Account;
        source.Account = Account.TotallyEmpty;

        await hosted.RefreshAsync(NextHead, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(hosted.TryGetAccount(NextHead, HostedAddress, out Account account), Is.True);
            Assert.That(account, Is.EqualTo(Account.TotallyEmpty));
            Assert.That(hosted.TryGetStorage(HostedAddress, previous, 1, out _), Is.False);
            Assert.That(hosted.TryGetCode(previous, out _), Is.False);
            Assert.That(hosted.TryGetStorage(HostedAddress, account, 1, out UInt256 value), Is.True);
            Assert.That(value, Is.EqualTo(UInt256.Zero));
            Assert.That(source.StorageFetches, Is.EqualTo(1));
            Assert.That(source.CodeFetches, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task Retained_storage_and_code_are_owned_by_the_snapshot()
    {
        Source source = new();
        HostedAccountState hosted = Create(source);
        byte[] expectedCode = source.Code.AsSpan().ToArray();
        await hosted.RefreshAsync(FirstHead, CancellationToken.None);
        source.Storage[Path(1)] = 999;
        source.Code[0] = 0xff;
        Assert.That(hosted.TryGetCode(source.Account, out byte[] callerCode), Is.True);
        callerCode[0] = 0xfe;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(hosted.TryGetStorage(HostedAddress, source.Account, 1, out UInt256 value), Is.True);
            Assert.That(value, Is.EqualTo((UInt256)42));
            Assert.That(hosted.TryGetCode(source.Account, out byte[] retainedCode), Is.True);
            Assert.That(retainedCode, Is.EqualTo(expectedCode));
        }
    }

    [Test]
    public async Task Refresh_publishes_account_storage_and_code_only_after_all_downloads_complete()
    {
        Source source = new();
        HostedAccountState hosted = Create(source);
        await hosted.RefreshAsync(FirstHead, CancellationToken.None);
        Account previous = source.Account;
        source.Account = source.Account.WithChangedStorageRoot(Keccak.Compute("changed storage"));
        source.Storage = new() { [Path(1)] = 999 };
        source.StorageGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task refresh = hosted.RefreshAsync(NextHead, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(hosted.TryGetAccount(NextHead, HostedAddress, out _), Is.False);
            Assert.That(hosted.TryGetAccount(FirstHead, HostedAddress, out _), Is.True);
            Assert.That(hosted.TryGetStorage(HostedAddress, previous, 1, out UInt256 value), Is.True);
            Assert.That(value, Is.EqualTo((UInt256)42));
            Assert.That(hosted.GetProgress(NextHead)[0].Stage, Is.EqualTo("refreshing"));
            Assert.That(hosted.GetProgress(NextHead)[0].BlockNumber, Is.EqualTo(FirstHead.Number));
            Assert.That(hosted.GetProgress(NextHead)[0].Current, Is.False);
            Assert.That(hosted.GetProgress(NextHead)[0].RefreshBlock, Is.EqualTo(NextHead.Number));
        }
        source.StorageGate.SetResult();
        await refresh;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(hosted.TryGetAccount(NextHead, HostedAddress, out Account current), Is.True);
            Assert.That(hosted.TryGetStorage(HostedAddress, current, 1, out UInt256 value), Is.True);
            Assert.That(value, Is.EqualTo((UInt256)999));
        }
    }

    [Test]
    public async Task Cancellation_during_download_does_not_publish_partial_state()
    {
        Source source = new() { StorageGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        HostedAccountState hosted = Create(source);
        using CancellationTokenSource stop = new();
        Task refresh = hosted.RefreshAsync(FirstHead, stop.Token);
        Assert.That(hosted.GetProgress(FirstHead)[0].Stage, Is.EqualTo("refreshing"));

        await stop.CancelAsync();

        await Assert.ThrowsAsync<TaskCanceledException>(async () => await refresh);
        Assert.That(hosted.TryGetAccount(FirstHead, HostedAddress, out _), Is.False);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(hosted.GetProgress(FirstHead)[0].Stage, Is.EqualTo("canceled"));
            Assert.That(hosted.GetProgress(FirstHead)[0].BlockNumber, Is.Null);
            Assert.That(hosted.GetProgress(FirstHead)[0].Current, Is.False);
        }
    }

    [Test]
    public async Task Worker_retries_failed_accounts_without_a_new_head_and_stops_on_cancellation()
    {
        Source source = new() { FailStage = "storage" };
        HostedAccountState hosted = Create(source);
        ManualClock clock = new();
        using CancellationTokenSource stop = new();
        Task worker = hosted.RunAsync(() => FirstHead, clock, stop.Token);
        Assert.That(hosted.TryGetAccount(FirstHead, HostedAddress, out _), Is.False);
        source.FailStage = null;

        clock.Expire();
        await clock.NextDelay.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.That(hosted.TryGetAccount(FirstHead, HostedAddress, out _), Is.True);
        await stop.CancelAsync();
        await Assert.ThrowsAsync<TaskCanceledException>(async () => await worker);
    }

    private static HostedAccountState Create(Source source, Address[]? accounts = null) =>
        new(accounts ?? [HostedAddress], source, source.DownloadStorage, NullLogger.Instance);

    private static ValueHash256 Path(UInt256 slot)
    {
        Span<byte> bytes = stackalloc byte[32];
        slot.ToBigEndian(bytes);
        return ValueKeccak.Compute(bytes);
    }

    private static Hash256 StorageRoot(IReadOnlyDictionary<ValueHash256, UInt256> storage)
    {
        PatriciaTree tree = new();
        foreach ((ValueHash256 key, UInt256 value) in storage) tree.Set(key.Bytes, Rlp.Encode(value).Bytes);
        tree.UpdateRootHash();
        return tree.RootHash;
    }

    private sealed class Source : IExecutionStateSource
    {
        internal byte[] Code = [0x60, 0x01, 0x00];
        internal Account Account = new(7, 123, Keccak.Compute("storage"), Keccak.Compute((ReadOnlySpan<byte>)[0x60, 0x01, 0x00]));
        internal Dictionary<ValueHash256, UInt256> Storage = new() { [Path(1)] = 42 };
        internal List<Address> RequestedAddresses = [];
        internal string? FailStage;
        internal Address? FailAddress;
        internal TaskCompletionSource? StorageGate;
        internal int AccountFetches;
        internal int StorageFetches;
        internal int CodeFetches;
        internal int HeaderFetches;
        internal int BalFetches;
        internal BlockHeader? Header;
        internal ReadOnlyBlockAccessList? Bal;

        public Task<Account> GetAccountAsync(VerifiedHead head, Address address, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AccountFetches++;
            RequestedAddresses.Add(address);
            if (FailStage == "account" || address == FailAddress) throw new InvalidDataException("Account proof failed.");
            return Task.FromResult(Account);
        }

        public Task<byte[]> GetCodeAsync(Account account, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CodeFetches++;
            if (FailStage == "code") throw new InvalidDataException("Code request failed.");
            return Task.FromResult(Code);
        }

        internal async Task<IReadOnlyDictionary<ValueHash256, UInt256>> DownloadStorage(VerifiedHead head, Address address,
            Account account, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StorageFetches++;
            if (StorageGate is not null) await StorageGate.Task.WaitAsync(cancellationToken);
            if (FailStage == "storage") throw new InvalidDataException("Storage proof failed.");
            return Storage;
        }

        public Task<UInt256> GetStorageAsync(VerifiedHead head, Address address, Account account, UInt256 key,
            CancellationToken cancellationToken) => throw new AssertionException("Individual storage slots must not be fetched.");
        public Task<BlockHeader> GetHeaderAsync(VerifiedHead head, CancellationToken cancellationToken)
        {
            HeaderFetches++;
            return Task.FromResult(Header ?? throw new NotSupportedException());
        }
        public Task<ReadOnlyBlockAccessList> GetBlockAccessListAsync(BlockHeader header, CancellationToken cancellationToken)
        {
            BalFetches++;
            return Task.FromResult(Bal ?? throw new NotSupportedException());
        }
        public Task<Hash256[]> GetAncestorHashesAsync(VerifiedHead head, ulong firstNumber,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class ManualClock : TimeProvider
    {
        private TimerCallback? _callback;
        private object? _state;
        private int _delays;
        internal TaskCompletionSource NextDelay { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _callback = callback;
            _state = state;
            if (++_delays == 2) NextDelay.TrySetResult();
            return new Timer();
        }

        internal void Expire() => _callback!(_state);

        private sealed class Timer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => default;
        }
    }
}
