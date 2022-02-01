//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Abi;
using Nethermind.AccountAbstraction.Data;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.AccountAbstraction.Executor
{
    public class UserOperationBlockTracer : IBlockTracer
    {
        private readonly AbiDefinition _abi;
        private readonly AbiEncoder _abiEncoder = new();
        private readonly Address _create2FactoryAddress;
        private readonly Address _entryPointAddress;
        private readonly long _gasLimit;
        private readonly ILogger _logger;
        private readonly IStateProvider _stateProvider;
        private readonly IUserOperationTxBuilder _userOperationTxBuilder;
        private readonly UserOperation _userOperation;

        private UserOperationTxTracer? _tracer;

        public UserOperationBlockTracer(
            long gasLimit, 
            UserOperation userOperation, 
            IStateProvider stateProvider,
            IUserOperationTxBuilder userOperationTxBuilder,
            AbiDefinition abi, 
            Address create2FactoryAddress, 
            Address entryPointAddress, 
            ILogger logger)
        {
            _gasLimit = gasLimit;
            _userOperation = userOperation;
            _stateProvider = stateProvider;
            _userOperationTxBuilder = userOperationTxBuilder;
            _abi = abi;
            _create2FactoryAddress = create2FactoryAddress;
            _entryPointAddress = entryPointAddress;
            _logger = logger;

            Output = Array.Empty<byte>();
            AccessedStorage = new Dictionary<Address, HashSet<UInt256>>();
        }

        public bool Success { get; private set; } = true;
        public long GasUsed { get; private set; }
        public byte[] Output { get; private set; }
        public FailedOp? FailedOp { get; private set; }
        public string? Error { get; private set; }

        public IDictionary<Address, HashSet<UInt256>> AccessedStorage { get; private set; }
        public bool IsTracingRewards => true;

        public void ReportReward(Address author, string rewardType, UInt256 rewardValue)
        {
        }

        public void StartNewBlockTrace(Block block)
        {
        }

        public ITxTracer StartNewTxTrace(Transaction? tx)
        {
            return tx is null
                ? new UserOperationTxTracer(null, _stateProvider, _userOperation.Sender, _userOperation.Paymaster,
                    _create2FactoryAddress, _entryPointAddress, _logger)
                : _tracer = new UserOperationTxTracer(tx, _stateProvider, _userOperation.Sender,
                    _userOperation.Paymaster, _create2FactoryAddress, _entryPointAddress, _logger);
        }

        public void EndTxTrace()
        {
            if (_tracer is null) throw new ArgumentNullException(nameof(_tracer));

            Output = _tracer.Output;
            Error = _tracer.Error;

            FailedOp = _userOperationTxBuilder.DecodeEntryPointOutputError(Output);

            if (!_tracer!.Success)
            {
                Success = false;
                return;
            }

            GasUsed += _tracer!.GasSpent;

            if (GasUsed > _gasLimit)
            {
                Success = false;
                return;
            }

            AccessedStorage = _tracer.AccessedStorage;
        }

        public void EndBlockTrace()
        {
        }
    }

    public class UserOperationTxTracer : ITxTracer
    {
        private static readonly Instruction[] _bannedOpcodes =
        {
            Instruction.GASPRICE, Instruction.GASLIMIT, Instruction.DIFFICULTY, Instruction.TIMESTAMP,
            Instruction.BASEFEE, Instruction.BLOCKHASH, Instruction.NUMBER, Instruction.SELFBALANCE,
            Instruction.BALANCE, Instruction.ORIGIN, Instruction.COINBASE
        };

        private readonly Address _create2FactoryAddress;
        private readonly Address _entryPointAddress;
        private readonly ILogger _logger;
        private readonly Address _paymaster;
        private readonly Address _sender;

        private readonly IStateProvider _stateProvider;

        private bool _paymasterValidationMode;
        private Address[] _excludedAddresses;
        
        public UserOperationTxTracer(
            Transaction? transaction,
            IStateProvider stateProvider,
            Address sender,
            Address paymaster,
            Address create2FactoryAddress,
            Address entryPointAddress,
            ILogger logger)
        {
            Transaction = transaction;
            Success = true;
            AccessedStorage = new Dictionary<Address, HashSet<UInt256>>();
            _stateProvider = stateProvider;
            _sender = sender;
            _paymaster = paymaster;
            _create2FactoryAddress = create2FactoryAddress;
            _entryPointAddress = entryPointAddress;
            _logger = logger;
            Output = Array.Empty<byte>();

            _excludedAddresses = new[] {_create2FactoryAddress, Address.Zero, _entryPointAddress, _paymaster, _sender};
        }

        public Transaction? Transaction { get; }
        public IDictionary<Address, HashSet<UInt256>> AccessedStorage { get; }
        public bool Success { get; private set; }
        public string? Error { get; private set; }
        public long GasSpent { get; set; }
        public byte[] Output { get; private set; }


        public bool IsTracingReceipt => true;
        public bool IsTracingActions => true;
        public bool IsTracingOpLevelStorage => false;
        public bool IsTracingMemory => false;
        public bool IsTracingInstructions => true;
        public bool IsTracingRefunds => false;
        public bool IsTracingCode => false;
        public bool IsTracingStack => false;
        public bool IsTracingState => true;
        public bool IsTracingStorage => true;
        public bool IsTracingBlockHash => false;
        public bool IsTracingAccess => true;

        public void MarkAsSuccess(Address recipient, long gasSpent, byte[] output, LogEntry[] logs,
            Keccak? stateRoot = null)
        {
            GasSpent = gasSpent;
            Output = output;
        }

        public void MarkAsFailed(Address recipient, long gasSpent, byte[] output, string error,
            Keccak? stateRoot = null)
        {
            GasSpent = gasSpent;
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

        public void ReportStorageChange(StorageCell storageCell, byte[] before, byte[] after)
        {
            throw new NotImplementedException();
        }

        public void ReportStorageRead(StorageCell storageCell)
        {
            throw new NotImplementedException();
        }

        public void StartOperation(int depth, long gas, Instruction opcode, int pc)
        {
            // spec: These opcodes are forbidden because their outputs may differ between simulation and execution,
            // so simulation of calls using these opcodes does not reliably tell what would happen if these calls are later done on-chain.
            if (depth > 1 && _bannedOpcodes.Contains(opcode))
            {
                _logger.Info($"AA: Encountered banned opcode {opcode} during simulation at depth {depth} pc {pc}");
                Success = false;
                Error ??= $"simulation: encountered banned opcode {opcode} at depth {depth} pc {pc}";
            }
            // in the simulateWallet function of the entryPoint, selfbalance is called twice
            // signalling that validation is switching from the wallet to the paymaster
            else if (depth == 1 && opcode == Instruction.NUMBER)
            { 
                _paymasterValidationMode = true;
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

        public void SetOperationMemory(List<string> memoryTrace)
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
            throw new NotImplementedException();
        }

        public void ReportSelfDestruct(Address address, UInt256 balance, Address refundAddress)
        {
            //TODO: would this ever be allowed?
        }

        public void ReportAction(long gas, UInt256 value, Address from, Address to, ReadOnlyMemory<byte> input,
            ExecutionType callType,
            bool isPrecompileCall = false)
        {
            // the paymaster can never even access any contract which either selfdestruct or delegatecall
            if (_paymasterValidationMode)
            {
                if (to is not null)
                {
                    if (ContainsSelfDestructOrDelegateCall(to)) Success = false;
                }
            }
        }

        public void ReportActionEnd(long gas, ReadOnlyMemory<byte> output)
        {
        }

        public void ReportActionError(EvmExceptionType evmExceptionType)
        {
        }

        public void ReportActionEnd(long gas, Address deploymentAddress, ReadOnlyMemory<byte> deployedCode)
        {
            // the paymaster can never even access any contract which either selfdestruct or delegatecall
            if (_paymasterValidationMode)
            {
                if (ContainsSelfDestructOrDelegateCall(deploymentAddress)) Success = false;
            }
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
            void AddToAccessedStorage(StorageCell storageCell)
            {
                if (AccessedStorage.TryGetValue(storageCell.Address, out HashSet<UInt256>? values))
                {
                    values.Add(storageCell.Index);
                    return;
                }

                AccessedStorage.Add(storageCell.Address, new HashSet<UInt256> {storageCell.Index});
            }

            Address walletAddress = _sender;
            Address? paymasterAddress = _paymaster == Address.Zero ? null : _paymaster;
            Address[] furtherAddresses = accessedAddresses
                .Except(_excludedAddresses)
                .ToArray();

            // spec: The call does not access mutable state of any contract except the wallet/paymaster itself
            foreach (StorageCell accessedStorageCell in accessedStorageCells)
            {
                if (accessedStorageCell.Address == paymasterAddress || accessedStorageCell.Address == walletAddress)
                    AddToAccessedStorage(accessedStorageCell);

                if (furtherAddresses.Contains(accessedStorageCell.Address)) Success = false;
            }
        }

        private bool ContainsSelfDestructOrDelegateCall(Address address)
        {
            // simple static analysis
            byte[] code = _stateProvider.GetCode(address);

            int i = 0;
            while (i < code.Length)
            {
                byte currentInstruction = code[i];

                if (currentInstruction == (byte)Instruction.SELFDESTRUCT
                    || currentInstruction == (byte)Instruction.DELEGATECALL)
                    return true;

                // push opcodes
                if (currentInstruction >= 0x60 && currentInstruction <= 0x7f) i += currentInstruction - 0x5f;

                i++;
            }

            return false;
        }
    }
}
