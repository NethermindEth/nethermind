// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.State;
using Nethermind.State.Tracing;
using static Nethermind.Core.Extensions.MemoryExtensions;

using static Nethermind.Evm.VirtualMachine;

namespace Nethermind.Evm.TransactionProcessing
{
    public class TransactionProcessor : ITransactionProcessor
    {
        protected EthereumEcdsa Ecdsa { get; private init; }
        protected ILogger Logger { get; private init; }
        protected ISpecProvider SpecProvider { get; private init; }
        protected IWorldState WorldState { get; private init; }
        protected IVirtualMachine VirtualMachine { get; private init; }

        [Flags]
        protected enum ExecutionOptions
        {
            /// <summary>
            /// Just accumulate the state
            /// </summary>
            None = 0,

            /// <summary>
            /// Commit the state after execution
            /// </summary>
            Commit = 1,

            /// <summary>
            /// Restore state after execution
            /// </summary>
            Restore = 2,

            /// <summary>
            /// Skip potential fail checks
            /// </summary>
            NoValidation = Commit | 4,

            /// <summary>
            /// Commit and later restore state also skip validation, use for CallAndRestore
            /// </summary>
            CommitAndRestore = Commit | Restore | NoValidation
        }

        public TransactionProcessor(
            ISpecProvider? specProvider,
            IWorldState? worldState,
            IVirtualMachine? virtualMachine,
            ILogManager? logManager)
        {
            Logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            SpecProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            WorldState = worldState ?? throw new ArgumentNullException(nameof(worldState));
            VirtualMachine = virtualMachine ?? throw new ArgumentNullException(nameof(virtualMachine));
            Ecdsa = new EthereumEcdsa(specProvider.ChainId, logManager);
        }

        public void CallAndRestore(Transaction transaction, BlockExecutionContext blCtx, ITxTracer txTracer)
        {
            Execute(transaction, blCtx, txTracer, ExecutionOptions.CommitAndRestore);
        }

        public void BuildUp(Transaction transaction, BlockExecutionContext blCtx, ITxTracer txTracer)
        {
            // we need to treat the result of previous transaction as the original value of next transaction
            // when we do not commit
            WorldState.TakeSnapshot(true);
            Execute(transaction, blCtx, txTracer, ExecutionOptions.None);
        }

        public void Execute(Transaction transaction, BlockExecutionContext blCtx, ITxTracer txTracer)
        {
            Execute(transaction, blCtx, txTracer, ExecutionOptions.Commit);
        }

        public void Trace(Transaction transaction, BlockExecutionContext blCtx, ITxTracer txTracer)
        {
            Execute(transaction, blCtx, txTracer, ExecutionOptions.NoValidation);
        }

        protected virtual void Execute(Transaction tx, BlockExecutionContext blCtx, ITxTracer tracer, ExecutionOptions opts)
        {
            BlockHeader header = blCtx.Header;
            IReleaseSpec spec = SpecProvider.GetSpec(header);
            if (tx.IsSystem())
                spec = new SystemTransactionReleaseSpec(spec);

            // restore is CallAndRestore - previous call, we will restore state after the execution
            bool restore = opts.HasFlag(ExecutionOptions.Restore);
            // commit - is for standard execute, we will commit thee state after execution
            // !commit - is for build up during block production, we won't commit state after each transaction to support rollbacks
            // we commit only after all block is constructed
            bool commit = opts.HasFlag(ExecutionOptions.Commit) || !spec.IsEip658Enabled;

            if (!ValidateStatic(tx, header, spec, tracer, opts, out long intrinsicGas))
                return;

            UInt256 effectiveGasPrice =
                tx.CalculateEffectiveGasPrice(spec.IsEip1559Enabled, header.BaseFeePerGas);

            if (opts == ExecutionOptions.Commit || opts == ExecutionOptions.None)
            {
                decimal gasPrice = (decimal)effectiveGasPrice / 1_000_000_000m;
                Metrics.MinGasPrice = Math.Min(gasPrice, Metrics.MinGasPrice);
                Metrics.MaxGasPrice = Math.Max(gasPrice, Metrics.MaxGasPrice);

                Metrics.BlockMinGasPrice = Math.Min(gasPrice, Metrics.BlockMinGasPrice);
                Metrics.BlockMaxGasPrice = Math.Max(gasPrice, Metrics.BlockMaxGasPrice);

                Metrics.AveGasPrice = (Metrics.AveGasPrice * Metrics.Transactions + gasPrice) / (Metrics.Transactions + 1);
                Metrics.EstMedianGasPrice += Metrics.AveGasPrice * 0.01m * decimal.Sign(gasPrice - Metrics.EstMedianGasPrice);
                Metrics.Transactions++;

                Metrics.BlockAveGasPrice = (Metrics.BlockAveGasPrice * Metrics.BlockTransactions + gasPrice) / (Metrics.BlockTransactions + 1);
                Metrics.BlockEstMedianGasPrice += Metrics.BlockAveGasPrice * 0.01m * decimal.Sign(gasPrice - Metrics.BlockEstMedianGasPrice);
                Metrics.BlockTransactions++;
            }

            bool deleteCallerAccount = RecoverSenderIfNeeded(tx, spec, opts, effectiveGasPrice);

            if (!ValidateSender(tx, header, spec, tracer, opts))
                return;

            if (!BuyGas(tx, header, spec, tracer, opts, effectiveGasPrice, out UInt256 premiumPerGas, out UInt256 senderReservedGasPayment))
                return;

            if (!IncrementNonce(tx, header, spec, tracer, opts))
                return;

            if (commit)
                WorldState.Commit(spec, tracer.IsTracingState ? tracer : NullTxTracer.Instance);

            ExecutionEnvironment env = BuildExecutionEnvironmnet(tx, blCtx, spec, tracer, opts, effectiveGasPrice);

            long gasAvailable = tx.GasLimit - intrinsicGas;
            if (!ExecuteEVMCall(tx, header, spec, tracer, opts, gasAvailable, env, out TransactionSubstate? substate, out long spentGas, out byte statusCode))
                return;

            if (!PayFees(tx, header, spec, tracer, substate, spentGas, premiumPerGas, statusCode))
                return;

            // Finalize
            if (restore)
            {
                WorldState.Reset();
                if (deleteCallerAccount)
                {
                    WorldState.DeleteAccount(tx.SenderAddress);
                }
                else
                {
                    if (!opts.HasFlag(ExecutionOptions.NoValidation))
                        WorldState.AddToBalance(tx.SenderAddress, senderReservedGasPayment, spec);
                    if (!tx.IsSystem())
                        WorldState.DecrementNonce(tx.SenderAddress);

                    WorldState.Commit(spec);
                }
            }
            else if (commit)
            {
                WorldState.Commit(spec, tracer.IsTracingState ? tracer : NullStateTracer.Instance);
            }

            if (tracer.IsTracingReceipt)
            {
                Hash256 stateRoot = null;
                if (!spec.IsEip658Enabled)
                {
                    WorldState.RecalculateStateRoot();
                    stateRoot = WorldState.StateRoot;
                }

                if (statusCode == StatusCode.Failure)
                {
                    byte[] output = (substate?.ShouldRevert ?? false) ? substate.Output.ToArray() : Array.Empty<byte>();
                    tracer.MarkAsFailed(env.ExecutingAccount, spentGas, output, substate?.Error, stateRoot);
                }
                else
                {
                    LogEntry[] logs = substate.Logs.Count != 0 ? substate.Logs.ToArray() : Array.Empty<LogEntry>();
                    tracer.MarkAsSuccess(env.ExecutingAccount, spentGas, substate.Output.ToArray(), logs, stateRoot);
                }
            }
        }

        protected void QuickFail(Transaction tx, BlockHeader block, IReleaseSpec spec, ITxTracer txTracer, string? reason)
        {
            block.GasUsed += tx.GasLimit;

            Address recipient = tx.To ?? ContractAddress.From(
                tx.SenderAddress ?? Address.Zero,
                WorldState.GetNonce(tx.SenderAddress ?? Address.Zero));

            if (txTracer.IsTracingReceipt)
            {
                Hash256? stateRoot = null;
                if (!spec.IsEip658Enabled)
                {
                    WorldState.RecalculateStateRoot();
                    stateRoot = WorldState.StateRoot;
                }

                txTracer.MarkAsFailed(recipient, tx.GasLimit, Array.Empty<byte>(), reason ?? "invalid", stateRoot);
            }
        }


        /// <summary>
        /// Validates the transaction, in a static manner (i.e. without accesing state/storage).
        /// It basically ensures the transaction is well formed (i.e. no null values where not allowed, no overflows, etc).
        /// As a part of validating the transaction the premium per gas will be calculated, to save computation this
        /// is returned in an out parameter.
        /// </summary>
        /// <param name="tx">The transaction to validate</param>
        /// <param name="header">The block containing the transaction. Only BaseFee is being used from the block atm.</param>
        /// <param name="spec">The release spec with which the transaction will be executed</param>
        /// <param name="tracer">The transaction tracer</param>
        /// <param name="opts">Options (Flags) to use for execution</param>
        /// <param name="premium">Computed premium per gas</param>
        /// <returns></returns>
        protected virtual bool ValidateStatic(Transaction tx, BlockHeader header, IReleaseSpec spec, ITxTracer tracer, ExecutionOptions opts, out long intrinsicGas)
        {
            intrinsicGas = IntrinsicGasCalculator.Calculate(tx, spec);

            bool validate = !opts.HasFlag(ExecutionOptions.NoValidation);

            if (tx.SenderAddress is null)
            {
                TraceLogInvalidTx(tx, "SENDER_NOT_SPECIFIED");
                QuickFail(tx, header, spec, tracer, "sender not specified");
                return false;
            }

            if (validate && tx.Nonce >= ulong.MaxValue - 1)
            {
                // we are here if nonce is at least (ulong.MaxValue - 1). If tx is contract creation,
                // it is max possible value. Otherwise, (ulong.MaxValue - 1) is allowed, but ulong.MaxValue not.
                if (tx.IsContractCreation || tx.Nonce == ulong.MaxValue)
                {
                    TraceLogInvalidTx(tx, "NONCE_OVERFLOW");
                    QuickFail(tx, header, spec, tracer, "nonce overflow");
                    return false;
                }
            }

            if (tx.IsAboveInitCode(spec))
            {
                TraceLogInvalidTx(tx, $"CREATE_TRANSACTION_SIZE_EXCEEDS_MAX_INIT_CODE_SIZE {tx.DataLength} > {spec.MaxInitCodeSize}");
                QuickFail(tx, header, spec, tracer, "EIP-3860 - transaction size over max init code size");
                return false;
            }

            if (!tx.IsSystem())
            {
                if (tx.GasLimit < intrinsicGas)
                {
                    TraceLogInvalidTx(tx, $"GAS_LIMIT_BELOW_INTRINSIC_GAS {tx.GasLimit} < {intrinsicGas}");
                    QuickFail(tx, header, spec, tracer, "gas limit below intrinsic gas");
                    return false;
                }

                if (validate && tx.GasLimit > header.GasLimit - header.GasUsed)
                {
                    TraceLogInvalidTx(tx, $"BLOCK_GAS_LIMIT_EXCEEDED {tx.GasLimit} > {header.GasLimit} - {header.GasUsed}");
                    QuickFail(tx, header, spec, tracer, "block gas limit exceeded");
                    return false;
                }
            }

            return true;
        }

        // TODO Should we remove this already
        protected bool RecoverSenderIfNeeded(Transaction tx, IReleaseSpec spec, ExecutionOptions opts, in UInt256 effectiveGasPrice)
        {
            bool commit = opts.HasFlag(ExecutionOptions.Commit) || !spec.IsEip658Enabled;
            bool restore = opts.HasFlag(ExecutionOptions.Restore);
            bool noValidation = opts.HasFlag(ExecutionOptions.NoValidation);

            bool deleteCallerAccount = false;

            if (!WorldState.AccountExists(tx.SenderAddress))
            {
                if (Logger.IsDebug) Logger.Debug($"TX sender account does not exist {tx.SenderAddress} - trying to recover it");

                Address prevSender = tx.SenderAddress;
                // hacky fix for the potential recovery issue
                if (tx.Signature is not null)
                    tx.SenderAddress = Ecdsa.RecoverAddress(tx, !spec.ValidateChainId);

                if (prevSender != tx.SenderAddress)
                {
                    if (Logger.IsWarn)
                        Logger.Warn($"TX recovery issue fixed - tx was coming with sender {prevSender} and the now it recovers to {tx.SenderAddress}");
                }
                else
                {
                    TraceLogInvalidTx(tx, $"SENDER_ACCOUNT_DOES_NOT_EXIST {tx.SenderAddress}");
                    if (!commit || noValidation || effectiveGasPrice == UInt256.Zero)
                    {
                        deleteCallerAccount = !commit || restore;
                        WorldState.CreateAccount(tx.SenderAddress, UInt256.Zero);
                    }
                }

                if (tx.SenderAddress is null)
                {
                    throw new InvalidDataException($"Failed to recover sender address on tx {tx.Hash} when previously recovered sender account did not exist.");
                }
            }

            return deleteCallerAccount;
        }


        protected virtual bool ValidateSender(Transaction tx, BlockHeader header, IReleaseSpec spec, ITxTracer tracer, ExecutionOptions opts)
        {
            bool validate = !opts.HasFlag(ExecutionOptions.NoValidation);

            if (validate && WorldState.IsInvalidContractSender(spec, tx.SenderAddress))
            {
                TraceLogInvalidTx(tx, "SENDER_IS_CONTRACT");
                QuickFail(tx, header, spec, tracer, "sender has deployed code");
                return false;
            }

            return true;
        }

        protected virtual bool BuyGas(Transaction tx, BlockHeader header, IReleaseSpec spec, ITxTracer tracer, ExecutionOptions opts,
                in UInt256 effectiveGasPrice, out UInt256 premiumPerGas, out UInt256 senderReservedGasPayment)
        {
            premiumPerGas = UInt256.Zero;
            senderReservedGasPayment = UInt256.Zero;
            bool validate = !opts.HasFlag(ExecutionOptions.NoValidation);

            if (!tx.IsSystem() && validate)
            {
                if (!tx.TryCalculatePremiumPerGas(header.BaseFeePerGas, out premiumPerGas))
                {
                    TraceLogInvalidTx(tx, "MINER_PREMIUM_IS_NEGATIVE");
                    QuickFail(tx, header, spec, tracer, "miner premium is negative");
                    return false;
                }

                UInt256 senderBalance = WorldState.GetBalance(tx.SenderAddress);
                if (UInt256.SubtractUnderflow(senderBalance, tx.Value, out UInt256 balanceLeft))
                {
                    TraceLogInvalidTx(tx, $"INSUFFICIENT_SENDER_BALANCE: ({tx.SenderAddress})_BALANCE = {senderBalance}");
                    QuickFail(tx, header, spec, tracer, "insufficient sender balance");
                    return false;
                }


                bool overflows;
                if (spec.IsEip1559Enabled && !tx.IsFree())
                {
                    overflows = UInt256.MultiplyOverflow((UInt256)tx.GasLimit, tx.MaxFeePerGas, out UInt256 maxGasFee);
                    if (overflows || balanceLeft < maxGasFee)
                    {
                        TraceLogInvalidTx(tx, $"INSUFFICIENT_MAX_FEE_PER_GAS_FOR_SENDER_BALANCE: ({tx.SenderAddress})_BALANCE = {senderBalance}, MAX_FEE_PER_GAS: {tx.MaxFeePerGas}");
                        QuickFail(tx, header, spec, tracer, "insufficient MaxFeePerGas for sender balance");
                        return false;
                    }
                    if (tx.SupportsBlobs)
                    {
                        overflows = UInt256.MultiplyOverflow(BlobGasCalculator.CalculateBlobGas(tx), (UInt256)tx.MaxFeePerBlobGas, out UInt256 maxBlobGasFee);
                        if (overflows || UInt256.AddOverflow(maxGasFee, maxBlobGasFee, out UInt256 multidimGasFee) || multidimGasFee > balanceLeft)
                        {
                            TraceLogInvalidTx(tx, $"INSUFFICIENT_MAX_FEE_PER_BLOB_GAS_FOR_SENDER_BALANCE: ({tx.SenderAddress})_BALANCE = {senderBalance}");
                            QuickFail(tx, header, spec, tracer, "insufficient sender balance");
                            return false;
                        }
                    }
                }

                overflows = UInt256.MultiplyOverflow((UInt256)tx.GasLimit, effectiveGasPrice, out senderReservedGasPayment);
                if (!overflows && tx.SupportsBlobs)
                {
                    overflows = !BlobGasCalculator.TryCalculateBlobGasPrice(header, tx, out UInt256 blobGasFee);
                    if (!overflows)
                    {
                        overflows = UInt256.AddOverflow(senderReservedGasPayment, blobGasFee, out senderReservedGasPayment);
                    }
                }

                if (overflows || senderReservedGasPayment > balanceLeft)
                {
                    TraceLogInvalidTx(tx, $"INSUFFICIENT_SENDER_BALANCE: ({tx.SenderAddress})_BALANCE = {senderBalance}");
                    QuickFail(tx, header, spec, tracer, "insufficient sender balance");
                    return false;
                }
            }

            if (validate)
                WorldState.SubtractFromBalance(tx.SenderAddress, senderReservedGasPayment, spec);

            return true;
        }

        protected virtual bool IncrementNonce(Transaction tx, BlockHeader header, IReleaseSpec spec, ITxTracer tracer, ExecutionOptions opts)
        {
            if (tx.IsSystem())
                return true;

            if (tx.Nonce != WorldState.GetNonce(tx.SenderAddress))
            {
                TraceLogInvalidTx(tx, $"WRONG_TRANSACTION_NONCE: {tx.Nonce} (expected {WorldState.GetNonce(tx.SenderAddress)})");
                QuickFail(tx, header, spec, tracer, "wrong transaction nonce");
                return false;
            }

            WorldState.IncrementNonce(tx.SenderAddress);
            return true;
        }

        protected virtual ExecutionEnvironment BuildExecutionEnvironmnet(
            Transaction tx, BlockExecutionContext blCtx, IReleaseSpec spec, ITxTracer tracer, ExecutionOptions opts,
            in UInt256 effectiveGasPrice)
        {
            Address recipient = tx.GetRecipient(tx.IsContractCreation ? WorldState.GetNonce(tx.SenderAddress) : 0) ??
                // this transaction is not a contract creation so it should have the recipient known and not null
                throw new InvalidDataException("Recipient has not been resolved properly before tx execution");

            TxExecutionContext executionContext =
                new(blCtx, tx.SenderAddress, effectiveGasPrice, tx.BlobVersionedHashes);

            CodeInfo codeInfo = tx.IsContractCreation ? new(tx.Data.AsArray())
                                    : VirtualMachine.GetCachedCodeInfo(WorldState, recipient, spec);

            byte[] inputData = tx.IsMessageCall ? tx.Data.AsArray() ?? Array.Empty<byte>() : Array.Empty<byte>();

            return new ExecutionEnvironment
            (
                txExecutionContext: executionContext,
                value: tx.Value,
                transferValue: tx.Value,
                caller: tx.SenderAddress,
                codeSource: recipient,
                executingAccount: recipient,
                inputData: inputData,
                codeInfo: codeInfo
            );
        }

        protected virtual bool ExecuteEVMCall(
            Transaction tx, BlockHeader header, IReleaseSpec spec, ITxTracer tracer, ExecutionOptions opts,
            in long gasAvailable, in ExecutionEnvironment env,
            out TransactionSubstate? substate, out long spentGas, out byte statusCode)
        {
            bool validate = !opts.HasFlag(ExecutionOptions.NoValidation);

            substate = null;
            spentGas = tx.GasLimit;
            statusCode = StatusCode.Failure;

            long unspentGas = gasAvailable;

            Snapshot snapshot = WorldState.TakeSnapshot();

            // Fixes eth_estimateGas.
            // If sender is SystemUser subtracting value will cause InsufficientBalanceException
            if (validate || !tx.IsSystem())
                WorldState.SubtractFromBalance(tx.SenderAddress, tx.Value, spec);

            try
            {
                if (tx.IsContractCreation)
                {
                    // if transaction is a contract creation then recipient address is the contract deployment address
                    PrepareAccountForContractDeployment(env.ExecutingAccount, spec);
                }

                ExecutionType executionType =
                    tx.IsContractCreation ? ExecutionType.CREATE : ExecutionType.TRANSACTION;

                using (EvmState state = new(unspentGas, env, executionType, true, snapshot, false))
                {
                    if (spec.UseTxAccessLists)
                    {
                        state.WarmUp(tx.AccessList); // eip-2930
                    }

                    if (spec.UseHotAndColdStorage)
                    {
                        state.WarmUp(tx.SenderAddress); // eip-2929
                        state.WarmUp(env.ExecutingAccount); // eip-2929
                    }

                    if (spec.AddCoinbaseToTxAccessList)
                    {
                        state.WarmUp(header.GasBeneficiary);
                    }

                    if (!tracer.IsTracingActions)
                    {
                        substate = VirtualMachine.Run<NotTracing>(state, WorldState, tracer);
                    }
                    else
                    {
                        substate = VirtualMachine.Run<IsTracing>(state, WorldState, tracer);
                    }

                    unspentGas = state.GasAvailable;

                    if (tracer.IsTracingAccess)
                    {
                        tracer.ReportAccess(state.AccessedAddresses, state.AccessedStorageCells);
                    }
                }

                if (substate.ShouldRevert || substate.IsError)
                {
                    if (Logger.IsTrace)
                        Logger.Trace("Restoring state from before transaction");
                    WorldState.Restore(snapshot);
                }
                else
                {
                    // tks: there is similar code fo contract creation from init and from CREATE
                    // this may lead to inconsistencies (however it is tested extensively in blockchain tests)
                    if (tx.IsContractCreation)
                    {
                        long codeDepositGasCost = CodeDepositHandler.CalculateCost(substate.Output.Length, spec);
                        if (unspentGas < codeDepositGasCost && spec.ChargeForTopLevelCreate)
                        {
                            throw new OutOfGasException();
                        }

                        if (CodeDepositHandler.CodeIsInvalid(spec, substate.Output))
                        {
                            throw new InvalidCodeException();
                        }

                        if (unspentGas >= codeDepositGasCost)
                        {
                            WorldState.InsertCode(env.ExecutingAccount, substate.Output, spec);
                            unspentGas -= codeDepositGasCost;
                        }
                    }

                    foreach (Address toBeDestroyed in substate.DestroyList)
                    {
                        if (Logger.IsTrace)
                            Logger.Trace($"Destroying account {toBeDestroyed}");

                        WorldState.ClearStorage(toBeDestroyed);
                        WorldState.DeleteAccount(toBeDestroyed);

                        if (tracer.IsTracingRefunds)
                            tracer.ReportRefund(RefundOf.Destroy(spec.IsEip3529Enabled));
                    }

                    statusCode = StatusCode.Success;
                }

                spentGas = Refund(tx, header, spec, opts, substate, unspentGas, env.TxExecutionContext.GasPrice);
            }
            catch (Exception ex) when (ex is EvmException || ex is OverflowException) // TODO: OverflowException? still needed? hope not
            {
                if (Logger.IsTrace)
                    Logger.Trace($"EVM EXCEPTION: {ex.GetType().Name}");
                WorldState.Restore(snapshot);
            }

            if (validate && !tx.IsSystem())
                header.GasUsed += spentGas;

            return true;
        }

        protected virtual bool PayFees(Transaction tx, BlockHeader header, IReleaseSpec spec, ITxTracer tracer, in TransactionSubstate substate, in long spentGas, in UInt256 premiumPerGas, in byte statusCode)
        {
            if (tx.IsSystem())
                return true;

            bool gasBeneficiaryNotDestroyed = substate?.DestroyList.Contains(header.GasBeneficiary) != true;
            if (statusCode == StatusCode.Failure || gasBeneficiaryNotDestroyed)
            {

                UInt256 fees = (UInt256)spentGas * premiumPerGas;
                UInt256 burntFees = !tx.IsFree() ? (UInt256)spentGas * header.BaseFeePerGas : 0;

                WorldState.AddToBalanceAndCreateIfNotExists(header.GasBeneficiary, fees, spec);

                if (spec.IsEip1559Enabled && spec.Eip1559FeeCollector is not null && !burntFees.IsZero)
                    WorldState.AddToBalanceAndCreateIfNotExists(spec.Eip1559FeeCollector, burntFees, spec); ;

                if (tracer.IsTracingFees)
                    tracer.ReportFees(fees, burntFees);
            }

            return true;
        }

        protected void PrepareAccountForContractDeployment(Address contractAddress, IReleaseSpec spec)
        {
            if (WorldState.AccountExists(contractAddress))
            {
                CodeInfo codeInfo = VirtualMachine.GetCachedCodeInfo(WorldState, contractAddress, spec);
                bool codeIsNotEmpty = codeInfo.MachineCode.Length != 0;
                bool accountNonceIsNotZero = WorldState.GetNonce(contractAddress) != 0;

                // TODO: verify what should happen if code info is a precompile
                // (but this would generally be a hash collision)
                if (codeIsNotEmpty || accountNonceIsNotZero)
                {
                    if (Logger.IsTrace)
                    {
                        Logger.Trace($"Contract collision at {contractAddress}");
                    }

                    throw new TransactionCollisionException();
                }

                // we clean any existing storage (in case of a previously called self destruct)
                WorldState.UpdateStorageRoot(contractAddress, Keccak.EmptyTreeHash);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected void TraceLogInvalidTx(Transaction transaction, string reason)
        {
            if (Logger.IsTrace) Logger.Trace($"Invalid tx {transaction.Hash} ({reason})");
        }

        protected virtual long Refund(Transaction tx, BlockHeader header, IReleaseSpec spec, ExecutionOptions opts,
            in TransactionSubstate substate, in long unspentGas, in UInt256 gasPrice)
        {
            long spentGas = tx.GasLimit;
            if (!substate.IsError)
            {
                spentGas -= unspentGas;
                long refund = substate.ShouldRevert
                    ? 0
                    : RefundHelper.CalculateClaimableRefund(spentGas,
                        substate.Refund + substate.DestroyList.Count * RefundOf.Destroy(spec.IsEip3529Enabled), spec);

                if (Logger.IsTrace)
                    Logger.Trace("Refunding unused gas of " + unspentGas + " and refund of " + refund);
                // If noValidation we didn't charge for gas, so do not refund
                if (!opts.HasFlag(ExecutionOptions.NoValidation))
                    WorldState.AddToBalance(tx.SenderAddress, (ulong)(unspentGas + refund) * gasPrice, spec);
                spentGas -= refund;
            }

            return spentGas;
        }
    }
}
