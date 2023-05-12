// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Mev.Data;
using Nethermind.TxPool;

namespace Nethermind.Mev.Execution
{
    public class TxBundleSimulator : TxBundleExecutor<SimulatedMevBundle, TxBundleSimulator.BundleBlockTracer>, IBundleSimulator
    {
        private readonly IGasLimitCalculator _gasLimitCalculator;
        private readonly ITimestamper _timestamper;
        private readonly ITxPool _txPool;
        private long _gasLimit;

        public TxBundleSimulator(ITracerFactory tracerFactory, IGasLimitCalculator gasLimitCalculator, ITimestamper timestamper, ITxPool txPool, ISpecProvider specProvider, ISigner? signer) : base(tracerFactory, specProvider, signer)
        {
            _gasLimitCalculator = gasLimitCalculator;
            _timestamper = timestamper;
            _txPool = txPool;
        }

        public Task<SimulatedMevBundle> Simulate(MevBundle bundle, BlockHeader parent, CancellationToken cancellationToken = default)
        {
            _gasLimit = _gasLimitCalculator.GetGasLimit(parent);
            try
            {
                return Task.FromResult(ExecuteBundle(bundle, parent, cancellationToken, _timestamper.UnixTime.Seconds));
            }
            catch (OperationCanceledException)
            {
                return Task.FromResult(SimulatedMevBundle.Cancelled(bundle));
            }
        }

        protected override long GetGasLimit(BlockHeader parent) => _gasLimitCalculator.GetGasLimit(parent);

        protected override SimulatedMevBundle BuildResult(MevBundle bundle, BundleBlockTracer tracer)
        {
            UInt256 eligibleGasFeePayment = UInt256.Zero;
            UInt256 totalGasFeePayment = UInt256.Zero;
            bool success = true;
            for (int i = 0; i < bundle.Transactions.Count; i++)
            {
                BundleTransaction tx = bundle.Transactions[i];
                if (tx.Hash is null)
                    throw new ArgumentException("transaction hash was unexpectedly null while processing bundle!");
                tx.SimulatedBundleGasUsed = (UInt256)tracer.GasUsed;

                success &= tracer.TransactionResults[i];

                totalGasFeePayment += tracer.TxFees[i];
                if (!_txPool.IsKnown(tx.Hash))
                {
                    eligibleGasFeePayment += tracer.TxFees[i];
                }
            }

            for (int i = 0; i < bundle.Transactions.Count; i++)
            {
                bundle.Transactions[i].SimulatedBundleFee = totalGasFeePayment + tracer.CoinbasePayments;
            }

            if ((UInt256)decimal.MaxValue >= (UInt256)Metrics.TotalCoinbasePayments + tracer.CoinbasePayments)
            {
                Metrics.TotalCoinbasePayments += (decimal)tracer.CoinbasePayments;
            }
            else
            {
                Metrics.TotalCoinbasePayments = ulong.MaxValue;
            }

            return new(bundle, tracer.GasUsed, success, tracer.BundleFee, tracer.CoinbasePayments, eligibleGasFeePayment);
        }

        protected override BundleBlockTracer CreateBlockTracer(MevBundle mevBundle) => new(_gasLimit, Beneficiary, mevBundle.Transactions.Count);

        public class BundleBlockTracer : IBlockTracer
        {
            private readonly long _gasLimit;
            private readonly Address _beneficiary;

            private BundleTxTracer? _tracer;
            private Block? _block;

            private UInt256? _beneficiaryBalanceBefore;
            private UInt256? _beneficiaryBalanceAfter;
            private int _index = 0;

            public long GasUsed { get; private set; }

            public BundleBlockTracer(long gasLimit, Address beneficiary, int txCount)
            {
                _gasLimit = gasLimit;
                _beneficiary = beneficiary;
                TxFees = new UInt256[txCount];
                TransactionResults = new BitArray(txCount);
            }

            public bool IsTracingRewards => true;
            public UInt256 BundleFee { get; private set; }

            public UInt256[] TxFees { get; }

            public UInt256 CoinbasePayments
            {
                get
                {
                    UInt256 beneficiaryBalanceAfter = _beneficiaryBalanceAfter ?? UInt256.Zero;
                    UInt256 beneficiaryBalanceBefore = _beneficiaryBalanceBefore ?? UInt256.Zero;
                    return beneficiaryBalanceAfter > (beneficiaryBalanceBefore + BundleFee)
                        ? beneficiaryBalanceAfter - beneficiaryBalanceBefore - BundleFee
                        : UInt256.Zero;
                }
            }

            public UInt256 Reward { get; private set; }

            public BitArray TransactionResults { get; }

            public void ReportReward(Address author, string rewardType, UInt256 rewardValue)
            {
                if (author == _beneficiary)
                {
                    Reward = rewardValue;
                }
            }

            public void StartNewBlockTrace(Block block)
            {
                _block = block;
            }
            public ITxTracer StartNewTxTrace(Transaction? tx)
            {
                return tx is null
                    ? new BundleTxTracer(_beneficiary, null, -1)
                    : _tracer = new BundleTxTracer(_beneficiary, tx, _index++);
            }

            public void EndTxTrace()
            {
                GasUsed += _tracer!.GasSpent;

                _beneficiaryBalanceBefore ??= (_tracer.BeneficiaryBalanceBefore ?? 0);
                _beneficiaryBalanceAfter = _tracer.BeneficiaryBalanceAfter;

                Transaction? tx = _tracer.Transaction;
                if (tx is not null)
                {
                    if (tx.TryCalculatePremiumPerGas(_block!.BaseFeePerGas, out UInt256 premiumPerGas))
                    {
                        UInt256 txFee = (UInt256)_tracer.GasSpent * premiumPerGas;
                        BundleFee += txFee;
                        TxFees[_tracer.Index] = txFee;
                    }

                    TransactionResults[_tracer.Index] =
                        _tracer.Success ||
                        (tx is BundleTransaction { CanRevert: true } && _tracer.Error == "revert");
                }

                if (GasUsed > _gasLimit)
                {
                    throw new OperationCanceledException("Block gas limit exceeded.");
                }
            }

            public void EndBlockTrace()
            {
            }
        }

        public class BundleTxTracer : ITxTracer
        {
            public Transaction? Transaction { get; }
            public int Index { get; }
            private readonly Address _beneficiary;

            public BundleTxTracer(Address beneficiary, Transaction? transaction, int index)
            {
                Transaction = transaction;
                Index = index;
                _beneficiary = beneficiary;
            }

            public bool IsTracingReceipt => true;
            public bool IsTracingActions => false;
            public bool IsTracingOpLevelStorage => false;
            public bool IsTracingMemory => false;
            public bool IsTracingInstructions => false;
            public bool IsTracingRefunds => false;
            public bool IsTracingCode => false;
            public bool IsTracingStack => false;
            public bool IsTracingState => true;
            public bool IsTracingStorage => false;
            public bool IsTracingBlockHash => false;
            public bool IsTracingAccess => false;
            public long GasSpent { get; set; }
            public UInt256? BeneficiaryBalanceBefore { get; private set; }
            public UInt256? BeneficiaryBalanceAfter { get; private set; }
            public bool IsTracingFees => false;

            public bool Success { get; private set; }
            public string? Error { get; private set; }

            public void MarkAsSuccess(Address recipient, long gasSpent, byte[] output, LogEntry[] logs, Keccak? stateRoot = null)
            {
                GasSpent = gasSpent;
                Success = true;
            }

            public void MarkAsFailed(Address recipient, long gasSpent, byte[] output, string error, Keccak? stateRoot = null)
            {
                GasSpent = gasSpent;
                Success = false;
                Error = error;
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

            public void ReportStorageChange(in UInt256 key, in UInt256 value)
            {
                throw new NotSupportedException();
            }

            public void SetOperationStorage(Address address, in UInt256 storageIndex, in UInt256 newValue, in UInt256 currentValue)
            {
                throw new NotSupportedException();
            }

            public void LoadOperationStorage(Address address, in UInt256 storageIndex, in UInt256 value)
            {
                throw new NotSupportedException();
            }

            public void ReportSelfDestruct(Address address, in UInt256 balance, Address refundAddress)
            {
                throw new NotSupportedException();
            }

            public void ReportBalanceChange(Address address, UInt256? before, UInt256? after)
            {
                if (address == _beneficiary)
                {
                    BeneficiaryBalanceBefore ??= before;
                    BeneficiaryBalanceAfter = after;
                }
            }

            public void ReportCodeChange(Address address, byte[]? before, byte[]? after)
            {
                throw new NotSupportedException();
            }

            public void ReportNonceChange(Address address, UInt256? before, UInt256? after)
            {
            }

            public void ReportAccountRead(Address address)
            {
            }

            public void ReportStorageChange(in StorageCell storageCell, in UInt256 before, in UInt256 after)
            {
                throw new NotSupportedException();
            }

            public void ReportStorageRead(in StorageCell storageCell)
            {
                throw new NotImplementedException();
            }

            public void ReportAction(long gas, in UInt256 value, Address from, Address to, ReadOnlyMemory<byte> input, ExecutionType callType, bool isPrecompileCall = false)
            {
                throw new NotSupportedException();
            }

            public void ReportActionEnd(long gas, ReadOnlyMemory<byte> output)
            {
                throw new NotSupportedException();
            }

            public void ReportActionError(EvmExceptionType exceptionType)
            {
                throw new NotSupportedException();
            }

            public void ReportActionEnd(long gas, Address deploymentAddress, ReadOnlyMemory<byte> deployedCode)
            {
                throw new NotSupportedException();
            }

            public void ReportBlockHash(Keccak blockHash)
            {
                throw new NotSupportedException();
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
                throw new NotImplementedException();
            }

            public void ReportAccess(IReadOnlySet<Address> accessedAddresses, IReadOnlySet<StorageCell> accessedStorageCells)
            {
                throw new NotImplementedException();
            }

            public void ReportFees(UInt256 fees, UInt256 burntFees)
            {
                throw new NotImplementedException();
            }
        }
    }
}
