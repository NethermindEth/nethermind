// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Frozen;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using ILogger = Nethermind.Logging.ILogger;

namespace Nethermind.LightClient;

/// <summary>Retains one complete authenticated account snapshot per configured address without cache eviction.</summary>
internal sealed class HostedAccountState
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(5);
    private readonly Dictionary<Address, Entry> _accounts = [];
    private readonly IExecutionStateSource _execution;
    private readonly Func<VerifiedHead, Address, Account, CancellationToken, Task<IReadOnlyDictionary<ValueHash256, UInt256>>> _downloadStorage;
    private readonly ILogger _logger;
    private readonly HostedAccountBalUpdater? _balUpdater;

    internal HostedAccountState(Address[] accounts, IExecutionStateSource execution,
        Func<VerifiedHead, Address, Account, CancellationToken, Task<IReadOnlyDictionary<ValueHash256, UInt256>>> downloadStorage,
        ILogger logger, bool useBal = false)
    {
        foreach (Address address in accounts) _accounts.TryAdd(address, new Entry());
        _execution = execution;
        _downloadStorage = downloadStorage;
        _logger = logger;
        if (useBal) _balUpdater = new(execution, logger);
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
        if (_accounts.TryGetValue(address, out Entry? entry) && Volatile.Read(ref entry.Current) is Snapshot snapshot &&
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
        if (_accounts.TryGetValue(address, out Entry? entry) && Volatile.Read(ref entry.Current) is Snapshot snapshot &&
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
            if (Volatile.Read(ref entry.Current) is Snapshot snapshot && snapshot.Account.CodeHash == authenticatedAccount.CodeHash)
            {
                code = snapshot.Code.AsSpan().ToArray();
                return true;
            }
        }
        code = [];
        return false;
    }

    internal async Task RefreshAsync(VerifiedHead head, CancellationToken cancellationToken)
    {
        foreach ((Address address, Entry entry) in _accounts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Snapshot? previous = Volatile.Read(ref entry.Current);
            if (previous?.Head.StateRoot == head.StateRoot) continue;
            try
            {
                Account account = await _execution.GetAccountAsync(head, address, cancellationToken);
                FrozenDictionary<ValueHash256, UInt256> storage;
                if (!account.HasStorage) storage = FrozenDictionary<ValueHash256, UInt256>.Empty;
                else if (previous?.Account.StorageRoot == account.StorageRoot) storage = previous.Storage;
                else
                {
                    IReadOnlyDictionary<ValueHash256, UInt256>? updated = previous is not null && _balUpdater is not null
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
                Volatile.Write(ref entry.Current, new Snapshot(head, account, storage, code));
                if (_logger.IsInfo) _logger.Info($"Hosted account {address} at block {head.Number}: {storage.Count} storage slots, {code.Length} code bytes.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                if (_logger.IsWarn) _logger.Warn($"Could not refresh hosted account {address} at block {head.Number}; retaining its last verified snapshot: {exception.Message}");
            }
        }
    }

    internal async Task RunAsync(Func<VerifiedHead?> getHead, TimeProvider clock, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (getHead() is VerifiedHead head) await RefreshAsync(head, cancellationToken);
            await Task.Delay(RefreshInterval, clock, cancellationToken);
        }
    }

    private sealed class Entry
    {
        internal Snapshot? Current;
    }

    private sealed record Snapshot(VerifiedHead Head, Account Account, FrozenDictionary<ValueHash256, UInt256> Storage, byte[] Code);
}
