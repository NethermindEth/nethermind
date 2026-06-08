// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;

namespace Nethermind.Consensus.Processing.ParallelProcessing.BlockStm;

/// <summary>
/// Captures account and storage writes during pre-block system-contract execution
/// (EIP-4788 BeaconRoots, EIP-2935 blockhash). <see cref="BlockStmTransactionsExecutor"/>
/// uses the captured writes to seed the per-block <see cref="MultiVersionMemory"/> so
/// per-tx parallel reads see the freshly-written state instead of the stale view that
/// the read-only trie store would otherwise return.
/// </summary>
public sealed class MvmmSystemWriteCapture : TxTracer
{
    private readonly Dictionary<Address, AccountSlot> _accountChanges = [];
    private readonly Dictionary<StorageCell, byte[]> _storageChanges = [];

    public MvmmSystemWriteCapture()
    {
        IsTracingState = true;
        IsTracingStorage = true;
    }

    public override void ReportBalanceChange(Address address, UInt256? before, UInt256? after)
    {
        ref AccountSlot slot = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(_accountChanges, address, out _);
        slot.Balance = after;
        slot.BalanceSet = true;
    }

    public override void ReportCodeChange(Address address, byte[]? before, byte[]? after)
    {
        ref AccountSlot slot = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(_accountChanges, address, out _);
        slot.CodeHash = after is null || after.Length == 0 ? Keccak.OfAnEmptyString.ValueHash256 : Keccak.Compute(after).ValueHash256;
        slot.CodeHashSet = true;
    }

    public override void ReportNonceChange(Address address, UInt256? before, UInt256? after)
    {
        ref AccountSlot slot = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(_accountChanges, address, out _);
        slot.Nonce = after;
        slot.NonceSet = true;
    }

    public override void ReportStorageChange(in StorageCell storageCell, byte[] before, byte[] after) => _storageChanges[storageCell] = after;

    /// <summary>
    /// Builds the MVMM seed from captured writes, reading any missing account fields
    /// (balance/nonce/codehash) from <paramref name="state"/> so each entry is a complete Account.
    /// </summary>
    public Dictionary<ParallelStateKey, object> BuildOverlay(IWorldState state)
    {
        Dictionary<ParallelStateKey, object> overlay = new(_accountChanges.Count + _storageChanges.Count);
        foreach (KeyValuePair<Address, AccountSlot> entry in _accountChanges)
        {
            Address address = entry.Key;
            AccountSlot s = entry.Value;
            if (!state.AccountExists(address))
            {
                overlay[ParallelStateKey.ForAccount(address)] = null!;
                continue;
            }
            UInt256 balance = s.BalanceSet && s.Balance.HasValue ? s.Balance.Value : state.GetBalance(address);
            UInt256 nonce = s.NonceSet && s.Nonce.HasValue ? s.Nonce.Value : state.GetNonce(address);
            ValueHash256 codeHash = s.CodeHashSet ? s.CodeHash : state.GetCodeHash(address);
            overlay[ParallelStateKey.ForAccount(address)] = new Account(nonce, balance, Keccak.EmptyTreeHash, new Hash256(codeHash));
        }
        foreach (KeyValuePair<StorageCell, byte[]> entry in _storageChanges)
        {
            overlay[ParallelStateKey.ForStorage(entry.Key)] = entry.Value;
        }
        return overlay;
    }

    public void Reset()
    {
        _accountChanges.Clear();
        _storageChanges.Clear();
    }

    public bool IsEmpty => _accountChanges.Count == 0 && _storageChanges.Count == 0;

    private struct AccountSlot
    {
        public UInt256? Balance;
        public UInt256? Nonce;
        public ValueHash256 CodeHash;
        public bool BalanceSet;
        public bool NonceSet;
        public bool CodeHashSet;
    }
}
