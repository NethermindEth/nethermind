// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.AccountAbstraction.Executor
{
    public class UserOperationTxTracer : ITxTracer
    {
        private static readonly Instruction[] _bannedOpcodes =
        {
            Instruction.GASPRICE, Instruction.GASLIMIT, Instruction.PREVRANDAO, Instruction.TIMESTAMP,
            Instruction.BASEFEE, Instruction.BLOCKHASH, Instruction.NUMBER, Instruction.SELFBALANCE,
            Instruction.BALANCE, Instruction.ORIGIN, Instruction.COINBASE, Instruction.CREATE
        };

        private readonly Address _entryPointAddress;
        private readonly ILogger _logger;
        private readonly Address _paymaster;
        private readonly Address _sender;

        private readonly bool _paymasterWhitelisted;
        private readonly bool _hasInitCode;

        private bool _paymasterValidationMode;
        private int _numberOfCreate2Calls;
        private bool _nextOpcodeMustBeCall; // GAS is allowed only if it followed immediately by a CALL, DELEGATECALL, STATICCALL, or CALLCODE

        public UserOperationTxTracer(
            Transaction? transaction,
            bool paymasterWhitelisted,
            bool hasInitCode,
            Address sender,
            Address paymaster,
            Address entryPointAddress,
            ILogger logger)
        {
            Success = true;
            AccessedStorage = new Dictionary<Address, HashSet<UInt256>>();
            AccessedAddresses = ImmutableHashSet<Address>.Empty;
            _paymasterWhitelisted = paymasterWhitelisted;
            _hasInitCode = hasInitCode;
            _sender = sender;
            _paymaster = paymaster;
            _entryPointAddress = entryPointAddress;
            _logger = logger;
            Output = Array.Empty<byte>();
        }

        public IDictionary<Address, HashSet<UInt256>> AccessedStorage { get; }
        public IReadOnlySet<Address> AccessedAddresses { get; private set; }
        public bool Success { get; private set; }
        public string? Error { get; private set; }
        public byte[] Output { get; private set; }

        public bool IsTracingReceipt => true;
        public bool IsTracingActions => true;
        public bool IsTracingOpLevelStorage => true;
        public bool IsTracingMemory => false;
        public bool IsTracingInstructions => true;
        public bool IsTracingRefunds => false;
        public bool IsTracingCode => false;
        public bool IsTracingStack => false;
        public bool IsTracingState => true;
        public bool IsTracingStorage => false;
        public bool IsTracingBlockHash => false;
        public bool IsTracingAccess => true;
        public bool IsTracingFees => false;
        public bool IsTracing => IsTracingReceipt || IsTracingActions || IsTracingOpLevelStorage || IsTracingMemory || IsTracingInstructions || IsTracingRefunds || IsTracingCode || IsTracingStack || IsTracingBlockHash || IsTracingAccess || IsTracingFees;

        public void MarkAsSuccess(Address recipient, long gasSpent, byte[] output, LogEntry[] logs,
            Keccak? stateRoot = null)
        {
            Output = output;
        }

        public void MarkAsFailed(Address recipient, long gasSpent, byte[] output, string error,
            Keccak? stateRoot = null)
        {
            Success = false;
            Error = error;
            Output = output;
        }

        public void ReportBalanceChange(Address address, UInt256? before, UInt256? after)
        {
        }

        public void ReportCodeChange(Address address, byte[]? before, byte[]? after)
        {
        }

        public void ReportNonceChange(Address address, UInt256? before, UInt256? after)
        {
        }

        public void ReportAccountRead(Address address)
        {
        }

        public void ReportStorageChange(in StorageCell storageCell, byte[] before, byte[] after)
        {
            throw new NotImplementedException();
        }

        public void ReportStorageRead(in StorageCell storageCell)
        {
            throw new NotImplementedException();
        }

        public void StartOperation(int depth, long gas, Instruction opcode, int pc, bool isPostMerge = false)
        {
            if (_nextOpcodeMustBeCall)
            {
                _nextOpcodeMustBeCall = false;

                if (opcode != Instruction.CALL &&
                    opcode != Instruction.STATICCALL &&
                    opcode != Instruction.DELEGATECALL &&
                    opcode != Instruction.CALLCODE)
                {
                    Success = false;
                    Error ??= $"simulation error: GAS opcode was called at depth {depth} pc {pc} but was not immediately followed by CALL, DELEGATECALL, STATICCALL, or CALLCODE";
                }
            }

            if (depth > 1 && opcode == Instruction.GAS)
            {
                _nextOpcodeMustBeCall = true;
            }

            // spec: These opcodes are forbidden because their outputs may differ between simulation and execution,
            // so simulation of calls using these opcodes does not reliably tell what would happen if these calls are later done on-chain.
            if (depth > 1 && _bannedOpcodes.Contains(opcode))
            {
                if (_paymasterValidationMode && _paymasterWhitelisted)
                {
                    return;
                }

                _logger.Info($"AA: Encountered banned opcode {opcode} during simulation at depth {depth} pc {pc}");
                Success = false;
                Error ??= $"simulation failed: encountered banned opcode {opcode} at depth {depth} pc {pc}";
            }
            // in the simulateWallet function of the entryPoint, NUMBER is called once
            // signalling that validation is switching from the wallet to the paymaster
            else if (depth == 1 && opcode == Instruction.NUMBER)
            {
                _paymasterValidationMode = true;
            }
            else if (opcode == Instruction.CREATE2)
            {
                _numberOfCreate2Calls++;

                if (_paymasterValidationMode && _paymasterWhitelisted)
                {
                    return;
                }

                if (_hasInitCode)
                {
                    if (_numberOfCreate2Calls > 1)
                    {
                        Success = false;
                        Error = $"simulation failed: op with non-empty initCode called CREATE2 more than once";
                    }
                }
                else
                {
                    if (_numberOfCreate2Calls > 0)
                    {
                        Success = false;
                        Error = $"simulation failed: op with empty initCode called CREATE2";
                    }
                }
            }
        }

        public void ReportOperationError(EvmExceptionType error)
        {
        }

        public void ReportOperationRemainingGas(long gas)
        {
        }

        public void SetOperationStack(List<string> stackTrace)
        {
            throw new NotImplementedException();
        }

        public void ReportStackPush(in ReadOnlySpan<byte> stackItem)
        {
        }

        public void SetOperationMemory(IEnumerable<string> memoryTrace)
        {
            throw new NotImplementedException();
        }

        public void SetOperationMemorySize(ulong newSize)
        {
            throw new NotImplementedException();
        }

        public void ReportMemoryChange(long offset, in ReadOnlySpan<byte> data)
        {
        }

        public void ReportStorageChange(in ReadOnlySpan<byte> key, in ReadOnlySpan<byte> value)
        {
        }

        public void SetOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> newValue,
            ReadOnlySpan<byte> currentValue)
        {
            HandleStorageAccess(address, storageIndex);
        }

        public void LoadOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> value)
        {
            HandleStorageAccess(address, storageIndex);
        }

        private void HandleStorageAccess(Address address, UInt256 storageIndex)
        {
            AddToAccessedStorage(address, storageIndex);

            if (address == _entryPointAddress) return;
            // spec: The call does not access mutable state of any contract except the wallet/paymaster itself
            if (!_paymasterValidationMode)
            {
                if (address != _sender)
                {
                    Success = false;
                    Error ??= $"simulation failed: accessed external storage at address {address} storage key {storageIndex.ToHexString(false)} during wallet validation";
                }
            }
            else
            {
                if (address != _paymaster && !_paymasterWhitelisted)
                {
                    Success = false;
                    Error ??= $"simulation failed: accessed external storage at address {address} storage key {storageIndex.ToHexString(false)} during paymaster validation";
                }
            }
        }

        public void ReportSelfDestruct(Address address, UInt256 balance, Address refundAddress)
        {
            //TODO: would this ever be allowed?
        }

        public void ReportAction(long gas, UInt256 value, Address from, Address to, ReadOnlyMemory<byte> input,
            ExecutionType callType,
            bool isPrecompileCall = false)
        {
            if (value == 0) return;

            if (from == _sender && to == _entryPointAddress) return;

            Success = false;
            Error ??= $"simulation failed: balance write allowed only from sender to entrypoint, instead found from: {from} to: {_entryPointAddress} with value {value}";
        }

        public void ReportActionEnd(long gas, ReadOnlyMemory<byte> output)
        {
        }

        public void ReportActionError(EvmExceptionType evmExceptionType)
        {
            if (evmExceptionType == EvmExceptionType.OutOfGas)
            {
                Success = false;
                Error ??= "simulation failed: a call during simulation ran out of gas";
            }
        }

        public void ReportActionEnd(long gas, Address deploymentAddress, ReadOnlyMemory<byte> deployedCode)
        {
        }

        public void ReportBlockHash(Keccak blockHash)
        {
            throw new NotImplementedException();
        }

        public void ReportByteCode(byte[] byteCode)
        {
            throw new NotImplementedException();
        }

        public void ReportGasUpdateForVmTrace(long refund, long gasAvailable)
        {
        }

        public void ReportRefund(long refund)
        {
            throw new NotImplementedException();
        }

        public void ReportExtraGasPressure(long extraGasPressure)
        {
            throw new NotImplementedException();
        }

        public void ReportAccess(IReadOnlySet<Address> accessedAddresses,
            IReadOnlySet<StorageCell> accessedStorageCells)
        {
            AccessedAddresses = accessedAddresses;
        }

        private void AddToAccessedStorage(Address address, UInt256 index)
        {
            if (AccessedStorage.TryGetValue(address, out HashSet<UInt256>? values))
            {
                values.Add(index);
                return;
            }

            AccessedStorage.Add(address, new HashSet<UInt256> { index });
        }

        public void ReportFees(UInt256 fees, UInt256 burntFees)
        {
            throw new NotImplementedException();
        }
    }
}
