// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
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

        public class BundleBlockTracer : BlockTracer
        {
            private readonly long _gasLimit;
            private readonly Address _beneficiary;

            private BundleTxTracer? _tracer;
            private Block? _block;

            private UInt256? _beneficiaryBalanceBefore;
            private UInt256? _beneficiaryBalanceAfter;
            private int _index;

            public long GasUsed { get; private set; }

            public BundleBlockTracer(long gasLimit, Address beneficiary, int txCount)
            {
                _gasLimit = gasLimit;
                _beneficiary = beneficiary;
                TxFees = new UInt256[txCount];
                TransactionResults = new BitArray(txCount);
            }

            public override bool IsTracingRewards => true;
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

            public override void ReportReward(Address author, string rewardType, UInt256 rewardValue)
            {
                if (author == _beneficiary)
                {
                    Reward = rewardValue;
                }
            }

            public override void StartNewBlockTrace(Block block)
            {
                _block = block;
            }
            public override ITxTracer StartNewTxTrace(Transaction? tx)
            {
                return tx is null
                    ? new BundleTxTracer(_beneficiary, null, -1)
                    : _tracer = new BundleTxTracer(_beneficiary, tx, _index++);
            }

            public override void EndTxTrace()
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
        }

        private class BundleTxTracer : TxTracer
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

            public override bool IsTracingReceipt => true;
            public override bool IsTracingState => true;
            public long GasSpent { get; private set; }
            public UInt256? BeneficiaryBalanceBefore { get; private set; }
            public UInt256? BeneficiaryBalanceAfter { get; private set; }
            public bool Success { get; private set; }
            public string? Error { get; private set; }

            public override void MarkAsSuccess(Address recipient, long gasSpent, byte[] output, LogEntry[] logs, Keccak? stateRoot = null)
            {
                GasSpent = gasSpent;
                Success = true;
            }

            public override void MarkAsFailed(Address recipient, long gasSpent, byte[] output, string error, Keccak? stateRoot = null)
            {
                GasSpent = gasSpent;
                Success = false;
                Error = error;
            }

            public override void ReportBalanceChange(Address address, UInt256? before, UInt256? after)
            {
                if (address == _beneficiary)
                {
                    BeneficiaryBalanceBefore ??= before;
                    BeneficiaryBalanceAfter = after;
                }
            }
        }
    }
}
