// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Frozen;
using System.Threading.Channels;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using ILogger = Nethermind.Logging.ILogger;

namespace Nethermind.LightClient;

internal sealed record HostedAccountProgress(string Address, ulong? BlockNumber, string? BlockHash, string? StateRoot,
    int StorageSlots, int CodeBytes, bool Current, string Stage, ulong? RefreshBlock, string? LastError);

/// <summary>Retains one complete authenticated account snapshot per configured address without cache eviction.</summary>
internal sealed class HostedAccountState
{
    private const int MaxConcurrentRefreshes = 4;
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(1);
    private readonly Dictionary<Address, Entry> _accounts = [];
    private readonly IExecutionStateSource _execution;
    private readonly Func<VerifiedHead, Address, Account, CancellationToken, Task<IReadOnlyDictionary<ValueHash256, UInt256>>> _downloadStorage;
    private readonly ILogger _logger;
    private readonly HostedAccountBalUpdater _balUpdater;

    internal HostedAccountState(Address[] accounts, IExecutionStateSource execution,
        Func<VerifiedHead, Address, Account, CancellationToken, Task<IReadOnlyDictionary<ValueHash256, UInt256>>> downloadStorage,
        ILogger logger)
    {
        foreach (Address address in accounts) _accounts.TryAdd(address, new Entry());
        _execution = execution;
        _downloadStorage = downloadStorage;
        _logger = logger;
        _balUpdater = new(execution, logger);
    }

    internal static Address[] ParseAccounts(string? value)
    {
        if (value is null) return [];
        HashSet<Address> seen = [];
        List<Address> accounts = [];
        foreach (string item in value.Split(',', StringSplitOptions.TrimEntries))
        {
            if (item.Length != 42 || !Address.IsValidAddress(item, allowPrefix: true))
                throw new ArgumentException("--host-accounts must contain comma-separated 0x-prefixed, 20-byte hexadecimal addresses.", nameof(value));
            Address address = new(item);
            if (seen.Add(address)) accounts.Add(address);
        }
        return accounts.ToArray();
    }

    internal bool TryGetAccount(VerifiedHead head, Address address, out Account account)
    {
        if (_accounts.TryGetValue(address, out Entry? entry) && Volatile.Read(ref entry.State).Snapshot is Snapshot snapshot &&
            snapshot.Head.StateRoot == head.StateRoot)
        {
            account = snapshot.Account;
            return true;
        }
        account = Account.TotallyEmpty;
        return false;
    }

    internal bool TryGetStorage(Address address, Account authenticatedAccount, UInt256 slot, out UInt256 value)
    {
        if (_accounts.TryGetValue(address, out Entry? entry) && Volatile.Read(ref entry.State).Snapshot is Snapshot snapshot &&
            snapshot.Account.StorageRoot == authenticatedAccount.StorageRoot)
        {
            Span<byte> slotBytes = stackalloc byte[32];
            slot.ToBigEndian(slotBytes);
            snapshot.Storage.TryGetValue(ValueKeccak.Compute(slotBytes), out value);
            return true;
        }
        value = UInt256.Zero;
        return false;
    }

    internal bool TryGetCode(Account authenticatedAccount, out byte[] code)
    {
        foreach (Entry entry in _accounts.Values)
        {
            if (Volatile.Read(ref entry.State).Snapshot is Snapshot snapshot && snapshot.Account.CodeHash == authenticatedAccount.CodeHash)
            {
                code = snapshot.Code.AsSpan().ToArray();
                return true;
            }
        }
        code = [];
        return false;
    }

    internal HostedAccountProgress[] GetProgress(VerifiedHead? target)
    {
        HostedAccountProgress[] progress = new HostedAccountProgress[_accounts.Count];
        int index = 0;
        foreach ((Address address, Entry entry) in _accounts)
        {
            EntryState state = Volatile.Read(ref entry.State);
            Snapshot? snapshot = state.Snapshot;
            progress[index++] = new(address.ToString(), snapshot?.Head.Number, snapshot?.Head.BlockHash.ToString(),
                snapshot?.Head.StateRoot.ToString(), snapshot?.Storage.Count ?? 0, snapshot?.Code.Length ?? 0,
                snapshot is not null && target is not null && snapshot.Head.StateRoot == target.StateRoot,
                state.Stage, state.RefreshBlock, state.LastError);
        }
        return progress;
    }

    internal async Task RefreshAsync(VerifiedHead head, CancellationToken cancellationToken)
    {
        foreach ((Address address, Entry entry) in _accounts)
            await RefreshAccountAsync(head, address, entry, cancellationToken);
    }

    private async Task RefreshAccountAsync(VerifiedHead head, Address address, Entry entry, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Snapshot? previous = Volatile.Read(ref entry.State).Snapshot;
        if (previous?.Head.StateRoot == head.StateRoot) return;
        Volatile.Write(ref entry.State, new(previous, "refreshing", head.Number, null));
        try
        {
            Account account = await _execution.GetAccountAsync(head, address, cancellationToken);
            FrozenDictionary<ValueHash256, UInt256> storage;
            if (!account.HasStorage) storage = FrozenDictionary<ValueHash256, UInt256>.Empty;
            else if (previous?.Account.StorageRoot == account.StorageRoot) storage = previous.Storage;
            else
            {
                IReadOnlyDictionary<ValueHash256, UInt256>? updated = previous is not null
                    ? await _balUpdater.TryUpdateAsync(previous.Head, head, address, previous.Storage, account.StorageRoot, cancellationToken)
                    : null;
                storage = (updated ?? await _downloadStorage(head, address, account, cancellationToken)).ToFrozenDictionary();
            }

            byte[] code;
            if (!account.HasCode) code = [];
            else if (previous?.Account.CodeHash == account.CodeHash) code = previous.Code;
            else
            {
                code = (await _execution.GetCodeAsync(account, cancellationToken)).AsSpan().ToArray();
                ExecutionProofVerifier.VerifyCode(account.CodeHash, code);
            }
            cancellationToken.ThrowIfCancellationRequested();
            Volatile.Write(ref entry.State, new(new Snapshot(head, account, storage, code), "complete", null, null));
            if (_logger.IsInfo) _logger.Info($"Hosted account {address} at block {head.Number}: {storage.Count} storage slots, {code.Length} code bytes.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Volatile.Write(ref entry.State, new(previous, "canceled", head.Number, null));
            throw;
        }
        catch (Exception exception)
        {
            Volatile.Write(ref entry.State, new(previous, "failed", head.Number,
                exception.Message.Length <= 512 ? exception.Message : exception.Message[..512]));
            if (_logger.IsWarn) _logger.Warn($"Could not refresh hosted account {address} at block {head.Number}; retaining its last verified snapshot: {exception.Message}");
        }
    }

    internal void NotifyHeadChanged()
    {
        foreach (Entry entry in _accounts.Values) entry.Wake.Writer.TryWrite(0);
    }

    internal async Task RunAsync(Func<VerifiedHead?> getHead, TimeProvider clock, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using CancellationTokenSource workersStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using SemaphoreSlim capacity = new(MaxConcurrentRefreshes, MaxConcurrentRefreshes);
        Task[] workers = new Task[_accounts.Count + 1];
        int index = 0;
        foreach ((Address address, Entry entry) in _accounts)
        {
            entry.Wake.Writer.TryWrite(0);
            workers[index++] = RunAccountAsync(address, entry);
        }
        workers[index] = RetryAsync();
        await Task.WhenAny(workers);
        await workersStop.CancelAsync();
        await Task.WhenAll(workers);

        async Task RunAccountAsync(Address address, Entry entry)
        {
            while (true)
            {
                await entry.Wake.Reader.ReadAsync(workersStop.Token);
                await capacity.WaitAsync(workersStop.Token);
                try
                {
                    if (getHead() is VerifiedHead head)
                        await RefreshAccountAsync(head, address, entry, workersStop.Token);
                }
                finally { capacity.Release(); }
            }
        }

        async Task RetryAsync()
        {
            using PeriodicTimer timer = new(RetryInterval, clock);
            while (await timer.WaitForNextTickAsync(workersStop.Token)) NotifyHeadChanged();
        }
    }

    private sealed class Entry
    {
        internal EntryState State = new(null, "waiting", null, null);
        internal readonly Channel<byte> Wake = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropWrite,
            AllowSynchronousContinuations = false,
        });
    }

    private sealed record EntryState(Snapshot? Snapshot, string Stage, ulong? RefreshBlock, string? LastError);

    private sealed record Snapshot(VerifiedHead Head, Account Account, FrozenDictionary<ValueHash256, UInt256> Storage, byte[] Code);
}
