// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.LightClient.Rpc;

internal sealed partial class EvmExecutor
{
    private const int MaxHintAccounts = 2;
    private const int MaxHintSlots = 4;
    private readonly SemaphoreSlim _prefetchSlots = new(2);

    private StatePrefetch? StartPrefetch(VerifiedHead head, Transaction tx, IReleaseSpec spec, MissingKind firstMiss,
        bool trace, CancellationToken token)
    {
        if (tx.Type == TxType.FrameTx || tx.To is not { } recipient) return null;
        Address sender = tx.SenderAddress!;
        bool fetchCode = !spec.IsPrecompile(recipient);
        AccessList? hints = spec.IsEip2930Enabled && tx.AccessList is { IsEmpty: false } list
            && list.Count is { AddressesCount: <= MaxHintAccounts, StorageKeysCount: <= MaxHintSlots } ? list : null;
        if (firstMiss == MissingKind.Blockhash || (firstMiss == MissingKind.Storage && hints is null)) return null;

        return _prefetchSlots.Wait(0, token) ? new StatePrefetch(this, head, sender, recipient, fetchCode, hints, trace, token) : null;
    }

    /// <summary>Read-only proof requests started after the first EVM attempt observes missing state.</summary>
    private sealed class StatePrefetch : IAsyncDisposable
    {
        private readonly EvmExecutor _owner;
        private readonly VerifiedHead _head;
        private readonly Address _recipient;
        private readonly CancellationTokenSource _stop;
        private readonly bool _trace;
        private readonly Dictionary<Address, Task<Account>> _accounts = [];
        private readonly Dictionary<(Address, UInt256), Task> _storage = [];
        private readonly Task? _code;

        public StatePrefetch(EvmExecutor owner, VerifiedHead head, Address sender, Address recipient, bool fetchCode,
            AccessList? accessList, bool trace, CancellationToken token)
        {
            _owner = owner;
            _head = head;
            _recipient = recipient;
            _stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            _trace = trace;
            GetAccount(sender);
            Task<Account> target = GetAccount(recipient);
            if (fetchCode) _code = WarmCodeAsync(target);
            if (accessList is null) return;
            foreach ((Address address, AccessList.StorageKeysEnumerable slots) in accessList)
            {
                Task<Account> account = GetAccount(address);
                foreach (UInt256 slot in slots)
                {
                    if (!_storage.ContainsKey((address, slot))) _storage.Add((address, slot), WarmStorageAsync(address, slot, account));
                }
            }
        }

        public Task? GetPending(MissingCallState missing) => missing.Kind switch
        {
            MissingKind.Account => _accounts.GetValueOrDefault(missing.Address!),
            MissingKind.Storage => _storage.GetValueOrDefault((missing.Address!, missing.Slot)),
            MissingKind.Code when _code is not null && _owner._cache.TryGetValue((_head.StateRoot, _recipient), out Account? recipient)
                && recipient!.CodeHash.ValueHash256 == missing.CodeHash => _code,
            _ => null,
        };

        private Task<Account> GetAccount(Address address)
        {
            if (!_accounts.TryGetValue(address, out Task<Account>? account))
                _accounts[address] = account = WarmAccountAsync(address);
            return account;
        }

        private async Task<Account> WarmAccountAsync(Address address)
        {
            long started = _trace ? Stopwatch.GetTimestamp() : 0;
            Task<Account> account = _owner.GetAccountAsync(_head, address, _stop.Token);
            await ObserveProofAsync(account, MissingKind.Account, started);
            return await account;
        }

        private async Task WarmCodeAsync(Task<Account> owner)
        {
            if (await GetHintOwnerAsync(owner) is { HasCode: true } account)
            {
                long started = _trace ? Stopwatch.GetTimestamp() : 0;
                await ObserveProofAsync(_owner.GetCodeAsync(account, _stop.Token), MissingKind.Code, started);
            }
        }

        private async Task WarmStorageAsync(Address address, UInt256 slot, Task<Account> owner)
        {
            if (await GetHintOwnerAsync(owner) is { HasStorage: true } account)
            {
                long started = _trace ? Stopwatch.GetTimestamp() : 0;
                await ObserveProofAsync(_owner.GetStorageAsync(_head, address, account, slot, _stop.Token),
                    MissingKind.Storage, started);
            }
        }

        private static async ValueTask<Account?> GetHintOwnerAsync(Task<Account> account)
        {
            await ((Task)account).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            return account.IsCompletedSuccessfully ? await account : null;
        }

        private async Task ObserveProofAsync(Task proof, MissingKind kind, long started)
        {
            try { await proof; }
            finally
            {
                if (_trace)
                    _owner._logger.Debug($"LC call id={Activity.Current?.Id} phase=prefetch kind={kind} outcome={(proof.IsCompletedSuccessfully ? "ready" : "unavailable")} elapsedMs={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F3}");
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await _stop.CancelAsync();
                // Undemanded proof failures do not change the result of the call.
                Task[] work = new Task[_accounts.Count + _storage.Count + (_code is null ? 0 : 1)];
                int index = 0;
                foreach (Task account in _accounts.Values) work[index++] = account;
                foreach (Task slot in _storage.Values) work[index++] = slot;
                if (_code is not null) work[index] = _code;
                await Task.WhenAll(work).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
            finally
            {
                _stop.Dispose();
                _owner._prefetchSlots.Release();
            }
        }
    }
}
