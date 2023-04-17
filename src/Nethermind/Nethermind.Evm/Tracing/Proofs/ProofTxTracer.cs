// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Evm.Tracing.Proofs
{
    public class ProofTxTracer : ITxTracer
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

        public bool IsTracingBlockHash => true;
        public bool IsTracingAccess => false;
        public bool IsTracingReceipt => true;
        public bool IsTracingActions => false;
        public bool IsTracingOpLevelStorage => false;
        public bool IsTracingMemory => false;
        public bool IsTracingInstructions => false;
        public bool IsTracingRefunds => false;
        public bool IsTracingCode => false;
        public bool IsTracingStack => false;
        public bool IsTracingState => true;
        public bool IsTracingStorage => true;
        public bool IsTracingFees => false;

        public void ReportActionEnd(long gas, Address deploymentAddress, ReadOnlyMemory<byte> deployedCode)
        {
            throw new NotSupportedException();
        }

        public void ReportBlockHash(Keccak blockHash)
        {
            BlockHashes.Add(blockHash);
        }

        public void ReportByteCode(byte[] byteCode)
        {
            throw new NotSupportedException();
        }

        public void ReportGasUpdateForVmTrace(long refund, long gasAvailable)
        {
            throw new NotSupportedException();
        }

        public void ReportRefund(long refund)
        {
            throw new NotSupportedException();
        }

        public void ReportExtraGasPressure(long extraGasPressure)
        {
            throw new NotSupportedException();
        }

        public void ReportAccess(IReadOnlySet<Address> accessedAddresses, IReadOnlySet<StorageCell> accessedStorageCells)
        {
            throw new NotImplementedException();
        }

        public void ReportBalanceChange(Address address, UInt256? before, UInt256? after)
        {
            if (_treatSystemAccountDifferently && Address.SystemUser == address && before is null && after == UInt256.Zero)
            {
                return;
            }

            Accounts.Add(address);
        }

        public void ReportCodeChange(Address address, byte[]? before, byte[]? after)
        {
            if (_treatSystemAccountDifferently && Address.SystemUser == address && before is null && after == Array.Empty<byte>())
            {
                return;
            }

            Accounts.Add(address);
        }

        public void ReportNonceChange(Address address, UInt256? before, UInt256? after)
        {
            if (_treatSystemAccountDifferently && Address.SystemUser == address && before is null && after == UInt256.Zero)
            {
                return;
            }

            Accounts.Add(address);
        }

        public void ReportStorageChange(in StorageCell storageCell, byte[] before, byte[] after)
        {
            // implicit knowledge here that if we read storage then for sure we have at least asked for the account's balance
            // and so we do not need to add account to Accounts
            Storages.Add(storageCell);
        }

        public void ReportStorageRead(in StorageCell storageCell)
        {
            // implicit knowledge here that if we read storage then for sure we have at least asked for the account's balance
            // and so we do not need to add account to Accounts
            Storages.Add(storageCell);
        }

        private bool _wasSystemAccountAccessedOnceAlready;

        public void ReportAccountRead(Address address)
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

        public void MarkAsSuccess(Address recipient, long gasSpent, byte[] output, LogEntry[] logs, Keccak? stateRoot = null)
        {
            Output = output;
        }

        public void MarkAsFailed(Address recipient, long gasSpent, byte[] output, string error, Keccak? stateRoot = null)
        {
            Output = output;
        }

        public void StartOperation(int depth, long gas, Instruction opcode, int pc, bool isPostMerge = false)
        {
            throw new NotSupportedException();
        }

        public void ReportOperationError(EvmExceptionType error)
        {
            throw new NotSupportedException();
        }

        public void ReportOperationRemainingGas(long gas)
        {
            throw new NotSupportedException();
        }

        public void SetOperationStack(List<string> stackTrace)
        {
            throw new NotSupportedException();
        }

        public void ReportStackPush(in ReadOnlySpan<byte> stackItem)
        {
            throw new NotSupportedException();
        }

        public void SetOperationMemory(List<string> memoryTrace)
        {
            throw new NotSupportedException();
        }

        public void SetOperationMemorySize(ulong newSize)
        {
            throw new NotSupportedException();
        }

        public void ReportMemoryChange(long offset, in ReadOnlySpan<byte> data)
        {
            throw new NotSupportedException();
        }

        public void ReportStorageChange(in ReadOnlySpan<byte> key, in ReadOnlySpan<byte> value)
        {
            throw new NotSupportedException();
        }

        public void SetOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> newValue, ReadOnlySpan<byte> currentValue)
        {
            throw new NotSupportedException();
        }

        public void LoadOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> value)
        {
            throw new NotSupportedException();
        }

        public void ReportSelfDestruct(Address address, UInt256 balance, Address refundAddress)
        {
            throw new NotSupportedException();
        }

        public void ReportAction(long gas, UInt256 value, Address @from, Address to, ReadOnlyMemory<byte> input, ExecutionType callType, bool isPrecompileCall = false)
        {
            throw new NotSupportedException();
        }

        public void ReportActionEnd(long gas, ReadOnlyMemory<byte> output)
        {
            throw new NotSupportedException();
        }

        public void ReportActionError(EvmExceptionType evmExceptionType)
        {
            throw new NotSupportedException();
        }

        public void ReportFees(UInt256 fees, UInt256 burntFees)
        {
            throw new NotImplementedException();
        }
    }
}
