// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Evm.Tracing.Proofs
{
    public class ProofTxTracer : TxTracer
    {
        private readonly bool _treatSystemAccountDifferently;

        public ProofTxTracer(bool treatSystemAccountDifferently)
        {
            _treatSystemAccountDifferently = treatSystemAccountDifferently;
        }

        public HashSet<Address> Accounts { get; } = new();

        public HashSet<StorageCell> Storages { get; } = new();

        public HashSet<Keccak> BlockHashes { get; } = new();

        public byte[]? Output { get; private set; }

        public override bool IsTracingBlockHash => true;
        public override bool IsTracingReceipt => true;
        public override bool IsTracingState => true;
        public override bool IsTracingStorage => true;

        public override void ReportBlockHash(Keccak blockHash)
        {
            BlockHashes.Add(blockHash);
        }

        public override void ReportBalanceChange(Address address, UInt256? before, UInt256? after)
        {
            if (_treatSystemAccountDifferently && Address.SystemUser == address && before is null && after == UInt256.Zero)
            {
                return;
            }

            Accounts.Add(address);
        }

        public override void ReportCodeChange(Address address, byte[]? before, byte[]? after)
        {
            if (_treatSystemAccountDifferently && Address.SystemUser == address && before is null && after == Array.Empty<byte>())
            {
                return;
            }

            Accounts.Add(address);
        }

        public override void ReportNonceChange(Address address, UInt256? before, UInt256? after)
        {
            if (_treatSystemAccountDifferently && Address.SystemUser == address && before is null && after == UInt256.Zero)
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

        private bool _wasSystemAccountAccessedOnceAlready;

        public override void ReportAccountRead(Address address)
        {
            if (_treatSystemAccountDifferently && !_wasSystemAccountAccessedOnceAlready && address == Address.SystemUser)
            {
                // we want to ignore the system account the first time only
                // TODO: I think this should only be done if the system account should be treated differently?
                _wasSystemAccountAccessedOnceAlready = true;
                return;
            }

            Accounts.Add(address);
        }

        public override void MarkAsSuccess(Address recipient, long gasSpent, byte[] output, LogEntry[] logs, Keccak? stateRoot = null)
        {
            Output = output;
        }

        public override void MarkAsFailed(Address recipient, long gasSpent, byte[] output, string error, Keccak? stateRoot = null)
        {
            Output = output;
        }
    }
}
