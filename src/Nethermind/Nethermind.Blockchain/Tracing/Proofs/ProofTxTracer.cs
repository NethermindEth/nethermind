// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;

namespace Nethermind.Blockchain.Tracing.Proofs;

public class ProofTxTracer(bool treatSystemAccountDifferently) : TxTracer
{
    public HashSet<Address> Accounts { get; } = new();

    public HashSet<StorageCell> Storages { get; } = new();

    public HashSet<Hash256> BlockHashes { get; } = new();

    public byte[]? Output { get; private set; }

    public override bool IsTracingBlockHash => true;
    public override bool IsTracingReceipt => true;
    public override bool IsTracingState => true;
    public override bool IsTracingStorage => true;

    public override void ReportBlockHash(Hash256 blockHash)
    {
        BlockHashes.Add(blockHash);
    }

    public override void ReportBalanceChange(Address address, UInt256? before, UInt256? after)
    {
        if (treatSystemAccountDifferently && Address.SystemUser == address && before is null && after?.IsZero != false)
        {
            return;
        }

        Accounts.Add(address);
    }

    public override void ReportCodeChange(Address address, byte[]? before, byte[]? after)
    {
        if (treatSystemAccountDifferently && Address.SystemUser == address && before is null &&
            after == Array.Empty<byte>())
        {
            return;
        }

        Accounts.Add(address);
    }

    public override void ReportNonceChange(Address address, UInt256? before, UInt256? after)
    {
        if (treatSystemAccountDifferently && Address.SystemUser == address && before is null && after?.IsZero != false)
        {
            return;
        }

        Accounts.Add(address);
    }

    public override void ReportStorageChange(in StorageCell storageCell, byte[] before, byte[] after)
    {
        // implicit knowledge here that if we read storage then for sure we have at least asked for the account's balance
        // and so we do not need to add account to Accounts
        Storages.Add(storageCell);
    }

    public override void ReportStorageRead(in StorageCell storageCell)
    {
        // implicit knowledge here that if we read storage then for sure we have at least asked for the account's balance
        // and so we do not need to add account to Accounts
        Storages.Add(storageCell);
    }

    public override void ReportAccountRead(Address address)
    {
        Accounts.Add(address);
    }

    public override void MarkAsSuccess(Address recipient, GasConsumed gasSpent, byte[] output, LogEntry[] logs,
        Hash256? stateRoot = null)
    {
        Output = output;
    }

    public override void MarkAsFailed(Address recipient, GasConsumed gasSpent, byte[] output, string? error,
        Hash256? stateRoot = null)
    {
        Output = output;
    }
}
