-- SPDX-License-Identifier: LGPL-3.0-only

-- This file is generated. Do not edit.
-- Extractor version: 0.1.0
-- Semantic IR SHA-256: f1352cd7ebc97d60f4f3befdcf6ff251df47714d14011d190b2f9e3819bc1937
-- Source closure SHA-256: 6c2cef3d82034479b493a99992a193cb68560ac8f84234b7cc3017a70e0e0b52
-- Compiler closure SHA-256: c40ab4f52dc087a0032f89e29ac615bc9935ceb993d5a04d6d2f310221dcd0b4
-- Scope: adapter-supplied OnlyOkTerminal ordinary exact-Commit simple-transfer fold.
-- C# execution, EVM frames, CREATE, BAL/parallel, roots, RLP, persistence and CLR behavior are excluded.

import ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel

namespace OrdinaryTransactionMachineExtractor.Generated

abbrev TerminalReceipt := ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel.Receipt
abbrev TerminalGasTotals := ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel.GasTotals
abbrev TerminalGas := ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel.Gas
abbrev TerminalState := ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel.State
abbrev TerminalBlock := ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel.BlockInput
abbrev TerminalTransaction := ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel.TransactionInput
abbrev TerminalStatus := ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel.Status
abbrev TerminalResult := ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel.TransactionResult
abbrev TerminalAddress := ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel.AddressOracle
abbrev TerminalBytes := ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel.BytesOracle
abbrev TerminalLog := ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel.LogOracle
abbrev TerminalHash := ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel.HashOracle
abbrev TerminalException := ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel.EvmExceptionOracle
abbrev TerminalError := ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel.ErrorOracle

def semanticIrSha256 : String := "f1352cd7ebc97d60f4f3befdcf6ff251df47714d14011d190b2f9e3819bc1937"
def sourceClosureSha256 : String := "6c2cef3d82034479b493a99992a193cb68560ac8f84234b7cc3017a70e0e0b52"
def compilerClosureSha256 : String := "c40ab4f52dc087a0032f89e29ac615bc9935ceb993d5a04d6d2f310221dcd0b4"
def sourcePaths : List String := [
  "src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs",
  "src/Nethermind/Nethermind.Evm/TransactionProcessing/ExecutionOptions.cs",
  "src/Nethermind/Nethermind.Evm/TransactionProcessing/SystemTransactionRoutingKernel.cs",
  "src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionSettlementKernel.cs",
  "src/Nethermind/Nethermind.Evm/TransactionProcessing/ITransactionProcessor.cs",
  "src/Nethermind/Nethermind.Evm/TransactionProcessing/ITransactionProcessorAdapter.cs",
  "src/Nethermind/Nethermind.Evm/TransactionProcessing/ExecuteTransactionProcessorAdapter.cs",
  "src/Nethermind/Nethermind.Evm/GasPolicy/IGasPolicy.cs",
  "src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs",
  "src/Nethermind/Nethermind.Evm/TransactionProcessing/GasConsumed.cs",
  "src/Nethermind/Nethermind.Evm/TransactionSubstate.cs",
  "src/Nethermind/Nethermind.Evm/Tracing/ITxTracer.cs",
  "src/Nethermind/Nethermind.Evm/State/IWorldState.cs",
  "src/Nethermind/Nethermind.Evm/BlockExecutionContext.cs",
  "src/Nethermind/Nethermind.Core/Transaction.cs",
  "src/Nethermind/Nethermind.Core/TransactionExtensions.cs",
  "src/Nethermind/Nethermind.Core/BlockHeader.cs",
  "src/Nethermind/Nethermind.Core/TransactionReceipt.cs",
  "src/Nethermind/Nethermind.Consensus/Processing/TransactionProcessorAdapterExtensions.cs",
  "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.BlockValidationTransactionsExecutor.cs",
  "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.ParallelBlockValidationTransactionsExecutor.cs",
  "src/Nethermind/Nethermind.Consensus/Processing/BlockAccessListManager.cs",
  "src/Nethermind/Nethermind.Consensus/Processing/IBlockAccessListManager.cs",
  "src/Nethermind/Nethermind.Init/Modules/BlockProcessingModule.cs",
  "src/Nethermind/Nethermind.Blockchain/Tracing/BlockReceiptsTracer.cs",
  "src/Nethermind/Nethermind.Core/Specs/IReleaseSpec.cs",
  "src/Nethermind/Nethermind.Specs/Forks/25_Amsterdam.cs",
  "src/Nethermind/Nethermind.Specs/ReleaseSpec.cs",
  "src/Nethermind/Nethermind.Core/Specs/ISpecProvider.cs",
  "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs",
  "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.std.cs",
  "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.BlockAccessListSystemContractHandler.cs",
  "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.SystemContractHandler.cs",
  "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.BlockProductionTransactionPicker.cs",
  "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.BlockProductionTransactionsExecutor.cs",
  "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.IBlockProductionTransactionPicker.cs",
  "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.IBlockProductionTransactionsExecutor.cs",
  "src/Nethermind/Nethermind.Consensus/Processing/BlockAccessListManager.StateChanges.cs",
  "src/Nethermind/Nethermind.Consensus/Processing/BlockAccessListManager.SystemContracts.cs",
  "src/Nethermind/Nethermind.Consensus/Processing/BlockAccessListManager.TxProcessorPool.cs",
  "src/Nethermind/Nethermind.Consensus/Processing/BlockAccessListManager.Validation.cs",
  "src/Nethermind/Nethermind.Evm/IVirtualMachine.cs",
  "src/Nethermind/Nethermind.Evm/State/IReadOnlyStateProvider.cs",
  "src/Nethermind/Nethermind.Evm/State/WorldStateExtensions.cs",
  "src/Nethermind/Nethermind.Evm/Tracing/IBlockTracer.cs",
  "src/Nethermind/Nethermind.Consensus/Processing/IBlockProcessor.cs",
  "src/Nethermind/Nethermind.Core/ContainerBuilderExtensions.cs",
  "src/Nethermind/Nethermind.Core/Container/IBlockValidationModule.cs",
  "src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessorAdapterFactory.cs",
  "src/Nethermind/Nethermind.Evm/TransactionProcessing/SystemTransactionProcessor.cs",
  "src/Nethermind/Nethermind.Evm/GasPolicy/IGasCost.cs",
  "src/Nethermind/Nethermind.Core/Transaction.std.cs",
  "src/Nethermind/Nethermind.Blockchain/BeaconBlockRoot/IBeaconBlockRootHandler.cs",
  "src/Nethermind/Nethermind.Blockchain/Blocks/IBlockhashStore.cs",
  "src/Nethermind/Nethermind.Consensus/ExecutionRequests/IExecutionRequestsProcessor.cs",
  "src/Nethermind/Nethermind.Consensus/Withdrawals/IWithdrawalProcessor.cs",
  "src/Nethermind/Nethermind.Core/Eip2930/IHasAccessList.cs",
  "src/Nethermind/Nethermind.Core/Specs/IForkAwareSpecProvider.cs",
  "src/Nethermind/Nethermind.Evm/Tracing/ITxTracerWrapper.cs",
  "src/Nethermind/Nethermind.Evm/TransactionExtensions.cs",
  "src/Nethermind/Nethermind.Evm/State/IReadOnlyStateProviderExtensions.cs",
  "src/Nethermind/Nethermind.Core/Specs/IReleaseSpecExtensions.cs",
  "src/Nethermind/Nethermind.Core/Specs/IReleaseSpecExtensions.std.cs",
  "src/Nethermind/Nethermind.Blockchain/Tracing/BlockReceiptGasAccountingKernel.cs",
  "src/Nethermind/Nethermind.Blockchain/Tracing/BlockTracer.cs",
  "src/Nethermind/Nethermind.Blockchain/Tracing/NullBlockTracer.cs",
  "src/Nethermind/Nethermind.Consensus/Processing/ProcessingOptions.cs",
  "src/Nethermind/Nethermind.Consensus/Processing/GasValidationResultSlot.cs",
  "src/Nethermind/Nethermind.Consensus/Processing/BlockAccessListValidationIndex.cs",
  "src/Nethermind/Nethermind.Consensus/Processing/TxProcessedEventArgs.cs",
  "src/Nethermind/Nethermind.Consensus/Processing/BalTxProcessorFactory.cs",
  "src/Nethermind/Nethermind.Consensus/Processing/IReadOnlyTxProcessingEnvFactory.cs",
  "src/Nethermind/Nethermind.Consensus/Rewards/BlockReward.cs",
  "src/Nethermind/Nethermind.Consensus/Producers/BlockToProduce.cs",
  "src/Nethermind/Nethermind.Consensus/Processing/PrewarmerEnvFactory.cs",
  "src/Nethermind/Nethermind.Consensus/Processing/PerTxTimingCollector.cs",
  "src/Nethermind/Nethermind.Consensus/Withdrawals/BlockProductionWithdrawalProcessor.cs",
  "src/Nethermind/Nethermind.Consensus/Processing/TxEventArgs.cs",
  "src/Nethermind/Nethermind.Consensus/ExecutionRequests/IExecutionRequestsProcessorFactory.cs",
  "src/Nethermind/Nethermind.Consensus/Rewards/IRewardCalculator.cs",
  "src/Nethermind/Nethermind.Consensus/Withdrawals/IWithdrawalProcessorFactory.cs",
  "src/Nethermind/Nethermind.Consensus/Processing/BlockCachePreWarmer.cs",
  "src/Nethermind/Nethermind.Consensus/Validators/IBlockValidator.cs",
  "src/Nethermind/Nethermind.Consensus/ExecutionRequests/ExecutionRequestsProcessorFactory.cs",
  "src/Nethermind/Nethermind.Consensus/Processing/BlockAccessListValidationIndex.LaneStore.cs",
  "src/Nethermind/Nethermind.Consensus/Processing/ExecutionFlags.std.cs",
  "src/Nethermind/Nethermind.Consensus/Processing/BlockchainProcessor.cs",
  "src/Nethermind/Nethermind.Consensus/Rewards/BlockRewardType.cs",
  "src/Nethermind/Nethermind.Consensus/Processing/CodeInfoRepositoryFactory.cs",
  "src/Nethermind/Nethermind.Consensus/Processing/AutoReadOnlyTxProcessingEnvFactory.cs",
  "src/Nethermind/Nethermind.Consensus/ExecutionRequests/ExecutionRequestsProcessor.cs",
  "src/Nethermind/Nethermind.Consensus/Processing/IBlockCachePreWarmer.cs",
  "src/Nethermind/Nethermind.Consensus/Validators/IHeaderValidator.cs",
  "src/Nethermind/Nethermind.Consensus/Validators/IWithdrawalValidator.cs",
  "src/Nethermind/Nethermind.Core/Specs/NoEip158Spec.cs",
  "src/Nethermind/Nethermind.Core/Specs/ReleaseSpecDecorator.cs",
  "src/Nethermind/Nethermind.Consensus/Processing/BlockRef.cs",
  "src/Nethermind/Nethermind.Consensus/Processing/BlockRemovedEventArgs.cs",
  "src/Nethermind/Nethermind.Consensus/Processing/DumpOptions.cs",
  "src/Nethermind/Nethermind.Consensus/Processing/IBlockchainProcessor.cs",
  "src/Nethermind/Nethermind.Consensus/Processing/IBlockPreprocessorStep.cs",
  "src/Nethermind/Nethermind.Consensus/Processing/IBranchProcessor.cs",
  "src/Nethermind/Nethermind.Consensus/Processing/IProcessingStats.cs",
  "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessingPauseGate.cs",
  "src/Nethermind/Nethermind.Consensus/Processing/IBlockProcessingPauseControl.cs",
  "src/Nethermind/Nethermind.Consensus/Processing/IBlockProcessingQueue.cs",
  "src/Nethermind/Nethermind.Consensus/Processing/ReadOnlyTxProcessingScope.cs",
  "src/Nethermind/Nethermind.Consensus/Processing/BlockHashEventArgs.cs",
  "src/Nethermind/Nethermind.Consensus/Processing/ProcessingStats.cs",
  "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessedEventArgs.cs",
  "src/Nethermind/Nethermind.Consensus/Processing/BlocksProcessingEventArgs.cs",
  "src/Nethermind/Nethermind.Consensus/Processing/BranchProcessingCompletedEventArgs.cs",
  "src/Nethermind/Nethermind.Consensus/Processing/BlockExtensions.cs",
  "src/Nethermind/Nethermind.Core/Address.cs",
  "src/Nethermind/Nethermind.Core/Crypto/Hash256.cs",
  "src/Nethermind/Nethermind.Core/Bloom.cs",
  "src/Nethermind/Nethermind.Core/Crypto/Keccak.cs",
  "src/Nethermind/Nethermind.Core/TxType.cs",
  "src/Nethermind/Nethermind.Core/Specs/ForkActivation.cs",
  "src/Nethermind/Nethermind.Core/BlobCellMask.cs",
  "src/Nethermind/Nethermind.Core/LogEntry.cs",
  "src/Nethermind/Nethermind.Core/Eip2930/AccessList.cs",
  "src/Nethermind/Nethermind.Core/Specs/SpecGasCosts.cs",
  "src/Nethermind/Nethermind.Core/GasCapExtensions.cs",
  "src/Nethermind/Nethermind.Core/Specs/IEip1559Spec.cs",
  "src/Nethermind/Nethermind.Core/Specs/IReceiptSpec.cs",
  "src/Nethermind/Nethermind.Core/IIntrinsicGasMemo.cs",
  "src/Nethermind/Nethermind.Core/SealEngineType.cs",
  "src/Nethermind/Nethermind.Core/AuthorizationTuple.cs",
  "src/Nethermind/Nethermind.Core/BaseFeeCalculator.cs",
  "src/Nethermind/Nethermind.Core/Attributes/Todo.cs",
  "src/Nethermind/Nethermind.Core/Extensions/MemoryExtensions.cs",
  "src/Nethermind/Nethermind.Core/Extensions/Bytes.cs",
  "src/Nethermind/Nethermind.Core/Collections/IHash64bit.cs",
  "src/Nethermind/Nethermind.Core/GenericEqualityComparer.cs",
  "src/Nethermind/Nethermind.Core/Extensions/SpanExtensions.cs",
  "src/Nethermind/Nethermind.Core/Extensions/SpanExtensions.std.cs",
  "src/Nethermind/Nethermind.Core/MemorySizes.cs",
  "src/Nethermind/Nethermind.Core/Extensions/Bytes.std.cs",
  "src/Nethermind/Nethermind.Core/Extensions/Bytes.Vector.cs",
  "src/Nethermind/Nethermind.Core/Extensions/ByteArrayExtensions.cs",
  "src/Nethermind/Nethermind.Core/Extensions/HexConverter.cs",
  "src/Nethermind/Nethermind.Core/Crypto/KeccakCache.cs",
  "src/Nethermind/Nethermind.Core/Crypto/KeccakCache.std.cs",
  "src/Nethermind/Nethermind.Core/Crypto/KeccakHash.cs",
  "src/Nethermind/Nethermind.Core/Crypto/KeccakHash.std.cs",
  "src/Nethermind/Nethermind.Core/Crypto/Signature.cs",
  "src/Nethermind/Nethermind.Core/Block.cs",
  "src/Nethermind/Nethermind.Core/StorageCell.cs",
  "src/Nethermind/Nethermind.Core/ValueAddress.cs",
  "src/Nethermind/Nethermind.Core/Address.std.cs",
  "src/Nethermind/Nethermind.Core/ILogEntry.cs",
  "src/Nethermind/Nethermind.Core/TxTypeExtensions.cs",
  "src/Nethermind/Nethermind.Core/Eip4844Constants.cs",
  "src/Nethermind/Nethermind.Core/GasCostOf.cs",
  "src/Nethermind/Nethermind.Core/RefundOf.cs",
  "src/Nethermind/Nethermind.Core/Container/KeyedMapperRegistrationSource.cs",
  "src/Nethermind/Nethermind.Core/JsonConverters/AddressConverter.cs",
  "src/Nethermind/Nethermind.Core/JsonConverters/AddressAsKeyConverter.cs",
  "src/Nethermind/Nethermind.Core/BloomConverter.cs",
  "src/Nethermind/Nethermind.Core/BlockBody.cs",
  "src/Nethermind/Nethermind.Core/Withdrawal.cs",
  "src/Nethermind/Nethermind.Core/BlockAccessLists/ReadOnlyBlockAccessList.cs",
  "src/Nethermind/Nethermind.Core/BlockAccessLists/GeneratedBlockAccessList.cs",
  "src/Nethermind/Nethermind.Core/Collections/ArrayPoolList.cs",
  "src/Nethermind/Nethermind.Core/Collections/ArrayPoolListRef.cs",
  "src/Nethermind/Nethermind.Core/Collections/ThrowHelper.cs",
  "src/Nethermind/Nethermind.Core/Eip7702Constants.cs",
  "src/Nethermind/Nethermind.Core/Eip7928Constants.cs",
  "src/Nethermind/Nethermind.Core/Eip8037Constants.cs",
  "src/Nethermind/Nethermind.Core/Eip8038Constants.cs",
  "src/Nethermind/Nethermind.Core/Crypto/KeccakHash.avx512vl.cs",
  "src/Nethermind/Nethermind.Core/Crypto/KeccakHash.avx512x8.cs",
  "src/Nethermind/Nethermind.Core/JsonConverters/ByteArrayConverter.cs",
  "src/Nethermind/Nethermind.Core/Extensions/IntExtensions.cs",
  "src/Nethermind/Nethermind.Core/Collections/IOwnedReadOnlyList.cs",
  "src/Nethermind/Nethermind.Core/Collections/PooledArrayEnumerator.cs",
  "src/Nethermind/Nethermind.Core/Collections/SafeArrayPool.std.cs",
  "src/Nethermind/Nethermind.Core/Collections/ArrayPoolListCore.cs",
  "src/Nethermind/Nethermind.Core/Collections/CollectionExtensions.cs",
  "src/Nethermind/Nethermind.Core/Collections/DictionaryExtensions.cs",
  "src/Nethermind/Nethermind.Core/BlockAccessLists/ReadOnlyAccountChanges.cs",
  "src/Nethermind/Nethermind.Core/BlockAccessLists/ReadOnlyAccountChangesView.cs",
  "src/Nethermind/Nethermind.Core/BlockAccessLists/GeneratedAccountChanges.cs",
  "src/Nethermind/Nethermind.Core/BlockAccessLists/GeneratedAccountChangesView.cs",
  "src/Nethermind/Nethermind.Core/BlockAccessLists/BlockAccessListAtIndex.cs",
  "src/Nethermind/Nethermind.Core/BlockAccessLists/AccountChangesAtIndex.cs",
  "src/Nethermind/Nethermind.Core/BlockAccessLists/ReadOnlySlotChanges.cs",
  "src/Nethermind/Nethermind.Core/BlockAccessLists/StorageChange.cs",
  "src/Nethermind/Nethermind.Core/GenericComparer.cs",
  "src/Nethermind/Nethermind.Core/Exceptions/SafePublicMessageFormatException.cs",
  "src/Nethermind/Nethermind.Core/Exceptions/IExceptionWithSafePublicMessage.cs",
  "src/Nethermind/Nethermind.Core/IJournal.cs",
  "src/Nethermind/Nethermind.Core/Resettables/IResettable.cs",
  "src/Nethermind/Nethermind.Core/BlockAccessLists/BalanceChange.cs",
  "src/Nethermind/Nethermind.Core/BlockAccessLists/NonceChange.cs",
  "src/Nethermind/Nethermind.Core/BlockAccessLists/CodeChange.cs",
  "src/Nethermind/Nethermind.Core/BlockAccessLists/SlotChangeAtIndex.cs",
  "src/Nethermind/Nethermind.Core/BlockAccessLists/GeneratedSlotChanges.cs",
  "src/Nethermind/Nethermind.Core/BlockAccessLists/IIndexedChange.cs",
  "src/Nethermind/Nethermind.Core/BlockAccessLists/IndexKey.cs",
  "src/Nethermind/Nethermind.Core/BlockAccessLists/StorageChangesByIndexConverter.cs",
  "src/Nethermind/Nethermind.Core/Collections/SlicedReadOnlyList.cs",
  "src/Nethermind/Nethermind.Core/Resettables/IReturnable.cs",
  "src/Nethermind/Nethermind.Core/Collections/ConcurrentDictionaryExtensions.cs",
  "src/Nethermind/Nethermind.Core/Unit.cs",
  "src/Nethermind/Nethermind.Core/UInt256Comparer.cs",
  "src/Nethermind/Nethermind.Core/Extensions/EvmWordExtensions.cs",
  "src/Nethermind/Nethermind.Evm/CodeAnalysis/CodeInfo.cs",
  "src/Nethermind/Nethermind.Evm/CodeAnalysis/CodeInfoFactory.cs",
  "src/Nethermind/Nethermind.Evm/CodeAnalysis/JumpDestinationAnalyzer.cs",
  "src/Nethermind/Nethermind.Evm/CodeAnalysis/JumpDestinationAnalyzer.std.cs",
  "src/Nethermind/Nethermind.Evm/Tracing/State/IStateTracer.cs",
  "src/Nethermind/Nethermind.Evm/Tracing/State/IStorageTracer.cs",
  "src/Nethermind/Nethermind.Evm/Tracing/State/IWorldStateTracer.cs",
  "src/Nethermind/Nethermind.Evm/Tracing/State/NullStateTracer.cs",
  "src/Nethermind/Nethermind.Evm/State/Snapshot.cs",
  "src/Nethermind/Nethermind.Evm/State/IWorldStateScopeProvider.cs",
  "src/Nethermind/Nethermind.Evm/ExecutionEnvironment.cs",
  "src/Nethermind/Nethermind.Evm/StackAccessTracker.cs",
  "src/Nethermind/Nethermind.Evm/StatusCode.cs",
  "src/Nethermind/Nethermind.Evm/TxExecutionContext.cs",
  "src/Nethermind/Nethermind.Evm/ICodeInfoRepository.cs",
  "src/Nethermind/Nethermind.Evm/EvmException.cs",
  "src/Nethermind/Nethermind.Evm/BlobGasCalculator.cs",
  "src/Nethermind/Nethermind.Evm/Precompiles/IPrecompile.cs",
  "src/Nethermind/Nethermind.Evm/GasPolicy/AccountAccessKind.cs",
  "src/Nethermind/Nethermind.Evm/Instructions/StorageAccessType.cs",
  "src/Nethermind/Nethermind.Evm/EvmPooledMemory.cs",
  "src/Nethermind/Nethermind.Evm/EvmFrameMemory.cs",
  "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Create.cs",
  "src/Nethermind/Nethermind.Evm/GasPolicy/StateGasTransitionKernel.cs",
  "src/Nethermind/Nethermind.Evm/GasPolicy/StateGasTransitionAdapterKernel.cs",
  "src/Nethermind/Nethermind.Evm/GasPolicy/AccountAccessPricingKernel.cs",
  "src/Nethermind/Nethermind.Evm/GasPolicy/PrecompileGasPricingKernel.cs",
  "src/Nethermind/Nethermind.Evm/GasPolicy/StateGasChargeKernel.cs",
  "src/Nethermind/Nethermind.Evm/GasPolicy/TransactionGasInitializationKernel.cs",
  "src/Nethermind/Nethermind.Evm/GasPolicy/Eip8037BlockGasInclusionCheck.cs",
  "src/Nethermind/Nethermind.Evm/Metrics.cs",
  "src/Nethermind/Nethermind.Evm/Metrics.std.cs",
  "src/Nethermind/Nethermind.Evm/Tracing/NullTxTracer.cs",
  "src/Nethermind/Nethermind.Evm/Tracing/TxTracer.cs",
  "src/Nethermind/Nethermind.Evm/Tracing/TraceMemory.cs",
  "src/Nethermind/Nethermind.Evm/Tracing/TraceStack.cs",
  "src/Nethermind/Nethermind.Evm/Instruction.cs",
  "src/Nethermind/Nethermind.Evm/ExecutionType.cs",
  "src/Nethermind/Nethermind.Evm/TransferLog.cs",
  "src/Nethermind/Nethermind.Evm/CodeDepositHandler.cs",
  "src/Nethermind/Nethermind.Evm/IntrinsicGasCalculator.cs",
  "src/Nethermind/Nethermind.Evm/RefundHelper.cs",
  "src/Nethermind/Nethermind.Evm/ContractAddress.cs",
  "src/Nethermind/Nethermind.Evm/DispatchFlags.std.cs",
  "src/Nethermind/Nethermind.Evm/VirtualMachine.cs",
  "src/Nethermind/Nethermind.Evm/VirtualMachine.warmup.cs",
  "src/Nethermind/Nethermind.Evm/VmState.cs",
  "src/Nethermind/Nethermind.Evm/VmStateStack.cs",
  "src/Nethermind/Nethermind.Evm/VirtualMachine.CallResult.cs",
  "src/Nethermind/Nethermind.Evm/VirtualMachine.Dispatch.cs",
  "src/Nethermind/Nethermind.Evm/VirtualMachine.ExecutionHandlers.cs",
  "src/Nethermind/Nethermind.Evm/VirtualMachine.OpcodeHandlers.cs",
  "src/Nethermind/Nethermind.Evm/VirtualMachine.std.cs",
  "src/Nethermind/Nethermind.Evm/EvmStack.cs",
  "src/Nethermind/Nethermind.Evm/EvmStack.std.cs",
  "src/Nethermind/Nethermind.Evm/EvmObjectPool.std.cs",
  "src/Nethermind/Nethermind.Evm/IBlockhashProvider.cs",
  "src/Nethermind/Nethermind.Evm/Instructions/EvmCalculations.cs",
  "src/Nethermind/Nethermind.Evm/OutOfGasException.cs",
  "src/Nethermind/Nethermind.Evm/PrecompileExecutionFailureException.cs",
  "src/Nethermind/Nethermind.Evm/PoppedAddressCache.cs",
  "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Spec.cs",
  "src/Nethermind/Nethermind.Evm/State/LocalMetrics.cs",
  "src/Nethermind/Nethermind.Evm/Tracing/FeesTracer.cs",
  "src/Nethermind/Nethermind.Evm/EvmExceptionExtensions.cs",
  "src/Nethermind/Nethermind.Evm/ReadOnlyMemoryExtensions.cs",
  "src/Nethermind/Nethermind.Evm/ReleaseSpecExtensions.cs",
  "src/Nethermind/Nethermind.Evm/Eip8038Flag.cs",
  "src/Nethermind/Nethermind.Evm/EvmStackUnderflowException.cs",
  "src/Nethermind/Nethermind.Evm/ExecutionMetricsCounters.cs",
  "src/Nethermind/Nethermind.Evm/OpcodeResult.cs",
  "src/Nethermind/Nethermind.Evm/SpecFlags.std.cs",
  "src/Nethermind/Nethermind.Evm/StackPool.cs",
  "src/Nethermind/Nethermind.Evm/StackPool.std.cs",
  "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Bitwise.cs",
  "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Call.cs",
  "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Call.std.cs",
  "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.CodeCopy.cs",
  "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.ControlFlow.cs",
  "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Crypto.cs",
  "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Environment.cs",
  "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Math1Param.cs",
  "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Math2Param.cs",
  "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Math3Param.cs",
  "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Shifts.cs",
  "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Stack.cs",
  "src/Nethermind/Nethermind.Evm/Instructions/EvmInstructions.Storage.cs",
  "src/Nethermind/Nethermind.Evm/Instructions/ExtendedStackDecoderKernel.cs",
  "src/Nethermind/Nethermind.Evm/GasPolicy/SStorePricingKernel.cs",
  "src/Nethermind/Nethermind.Evm/Tracing/TracerExtensions.cs",
  "src/Nethermind/Nethermind.Evm/Tracing/CancellationTxTracer.cs",
  "src/Nethermind/Nethermind.Evm/Tracing/CancellationBlockTracer.cs",
  "src/Nethermind/Nethermind.Evm/Tracing/CompositeTxTracer.cs",
  "src/Nethermind/Nethermind.Init/Modules/MainProcessingContext.cs",
  "src/Nethermind/Nethermind.Specs/Forks/NamedReleaseSpec.cs",
  "src/Nethermind/Nethermind.Specs/Forks/Fork.cs",
  "src/Nethermind/Nethermind.Specs/Forks/00_Olympic.cs",
  "src/Nethermind/Nethermind.Specs/Forks/01_Frontier.cs",
  "src/Nethermind/Nethermind.Specs/Forks/02_Homestead.cs",
  "src/Nethermind/Nethermind.Specs/Forks/03_Dao.cs",
  "src/Nethermind/Nethermind.Specs/Forks/04_TangerineWhistle.cs",
  "src/Nethermind/Nethermind.Specs/Forks/05_SpuriousDragon.cs",
  "src/Nethermind/Nethermind.Specs/Forks/06_Byzantium.cs",
  "src/Nethermind/Nethermind.Specs/Forks/07_Constantinople.cs",
  "src/Nethermind/Nethermind.Specs/Forks/08_ConstantinopleFix.cs",
  "src/Nethermind/Nethermind.Specs/Forks/09_Istanbul.cs",
  "src/Nethermind/Nethermind.Specs/Forks/10_MuirGlacier.cs",
  "src/Nethermind/Nethermind.Specs/Forks/11_Berlin.cs",
  "src/Nethermind/Nethermind.Specs/Forks/12_London.cs",
  "src/Nethermind/Nethermind.Specs/Forks/13_ArrowGlacier.cs",
  "src/Nethermind/Nethermind.Specs/Forks/14_GrayGlacier.cs",
  "src/Nethermind/Nethermind.Specs/Forks/15_Paris.cs",
  "src/Nethermind/Nethermind.Specs/Forks/16_Shanghai.cs",
  "src/Nethermind/Nethermind.Specs/Forks/17_Cancun.cs",
  "src/Nethermind/Nethermind.Specs/Forks/18_Prague.cs",
  "src/Nethermind/Nethermind.Specs/Forks/19_Osaka.cs",
  "src/Nethermind/Nethermind.Specs/Forks/20_BPO1.cs",
  "src/Nethermind/Nethermind.Specs/Forks/21_BPO2.cs",
  "src/Nethermind/Nethermind.Specs/MainnetSpecProvider.cs",
  "src/Nethermind/Nethermind.Specs/ForkScheduleSpecProvider.cs",
  "src/Nethermind/Nethermind.Specs/ForkSchedule.cs",
  "src/Nethermind/Nethermind.Specs/Forks/26_Bogota.cs",
  "src/Nethermind/Nethermind.Specs/ForkSpec.cs",
  "src/Nethermind/Nethermind.Specs/ForkActivationKind.cs",
]
def dependencyPaths : List String := [
  "tools/Evm/Lean/OrdinaryStaticAdmissionExtractor/Generated/OrdinaryStaticAdmissionKernel.ir.json",
  "tools/Evm/Lean/OrdinaryStaticAdmissionExtractor/Generated/OrdinaryStaticAdmissionKernel.lean",
  "tools/Evm/Lean/OrdinaryStaticAdmissionExtractor/Generated/OrdinaryStaticAdmissionKernel.source-manifest.json",
  "tools/Evm/Lean/OrdinaryStatefulAdmissionPrefixExtractor/Generated/OrdinaryStatefulAdmissionPrefix.ir.json",
  "tools/Evm/Lean/OrdinaryStatefulAdmissionPrefixExtractor/Generated/OrdinaryStatefulAdmissionPrefix.lean",
  "tools/Evm/Lean/OrdinaryStatefulAdmissionPrefixExtractor/Generated/OrdinaryStatefulAdmissionPrefix.source-manifest.json",
  "tools/Evm/Lean/OrdinaryPostNonceDispatchExtractor/Generated/OrdinaryPostNonceDispatch.ir.json",
  "tools/Evm/Lean/OrdinaryPostNonceDispatchExtractor/Generated/OrdinaryPostNonceDispatch.lean",
  "tools/Evm/Lean/OrdinaryPostNonceDispatchExtractor/Generated/OrdinaryPostNonceDispatch.source-manifest.json",
  "tools/Evm/Lean/SimpleTransferCompletionExtractor/Generated/SimpleTransferCompletion.ir.json",
  "tools/Evm/Lean/SimpleTransferCompletionExtractor/Generated/SimpleTransferCompletion.lean",
  "tools/Evm/Lean/SimpleTransferCompletionExtractor/Generated/SimpleTransferCompletion.source-manifest.json",
  "tools/Evm/Lean/ReceiptTerminalFoldExtractor/Generated/ReceiptTerminalFoldKernel.ir.json",
  "tools/Evm/Lean/ReceiptTerminalFoldExtractor/Generated/ReceiptTerminalFoldKernel.lean",
  "tools/Evm/Lean/ReceiptTerminalFoldExtractor/Generated/ReceiptTerminalFoldKernel.source-manifest.json",
  "tools/Evm/Lean/ReceiptTerminalFoldExtractor/SOURCE_PINS.json",
  "tools/Evm/Lean/ReceiptTerminalFoldExtractor/COMPILER_REFERENCE_PINS.json",
]
def sourceAnchors : List String := [
  "di.blockProcessorExecutorParameter",
  "di.blockProcessorExecutorStore",
  "di.decoratorInnerParameter",
  "di.decoratorBalParameter",
  "di.directAdapterParameter",
  "di.executeProcessorParameter",
  "context.reset.executionGas",
  "context.reset.stateGas",
  "context.install",
  "process.toExecuteCore",
  "route.systemGuard",
  "route.ordinaryExecute",
  "adapter.commitExecute",
  "adapter.commitProcess",
  "admission.recoverBeforeIntrinsic",
  "admission.intrinsic",
  "admission.contextHandoff",
  "admission.staticValidation",
  "admission.senderValidation",
  "admission.buyGas",
  "admission.incrementNonce",
  "dispatch.prepareFastPath",
  "dispatch.precommit",
  "dispatch.availableGas",
  "dispatch.simpleTransfer",
  "dispatch.evmExcluded",
  "dispatch.fastPathCandidate",
  "dispatch.fastPathCandidateCall",
  "dispatch.fastPathEntryGuard",
  "dispatch.noExecutableCode",
  "dispatch.noExecutableCodeCall",
  "simple.stateCharge",
  "simple.recipientDeadCheck",
  "simple.payValue",
  "simple.recipientWrite",
  "simple.oogForfeit",
  "simple.refund",
  "simple.access",
  "simple.headerAndFees",
  "simple.finalize",
  "fees.counterGuard",
  "fees.payFees",
  "finalize.commit",
  "finalize.receiptSuccess",
  "finalize.receiptFailure",
  "finalize.resultReturn",
  "adapter.startTxTrace",
  "adapter.execute",
  "adapter.endTxTrace",
  "context.executeAdapter",
  "executor.adapterCall",
  "executor.invalidResultGuard",
  "executor.invalidResultThrow",
  "executor.processedEvent",
  "executor.validationMode",
  "executor.processCall",
  "executor.blockGasLimitGuard",
  "executor.gasLimitThrow",
  "context.directExecutor",
  "bal.directInnerGuard",
  "bal.directInnerCall",
  "context.decoratorBal",
  "context.decoratorInner",
  "context.balStore",
  "tracer.blockReset.index",
  "tracer.blockReset.currentTx",
  "tracer.blockReset.tracer",
  "tracer.blockReset.receipts",
  "tracer.blockReset.blockGas",
  "tracer.blockReset.receiptGas",
  "tracer.receiptAppend",
  "tracer.nestedForwardGuard",
  "tracer.delegateSuccess",
  "tracer.currentForwardGuard",
  "tracer.currentSuccess",
  "tracer.gasUpdate",
  "tracer.receiptIndex",
  "tracer.tx-end-delegate",
  "tracer.endTx.indexIncrement",
  "tracer.txStart.currentTx",
  "tracer.txStart.delegate",
  "tracer.txStart.tracer",
  "block.startTrace",
  "block.createContext",
  "block.setContext",
  "block.preCommit",
  "block.fold",
  "block.transactionsExecuted",
  "block.postCommit",
  "block.balPrepareCall",
  "block.processBlockCall",
  "di.processor",
  "di.worldState",
  "di.blockProcessor",
  "di.balManager",
  "di.validationModule",
  "di.directExecutor",
  "di.parallelDecorator",
  "di.adapterFactory",
  "di.adapter",
  "di.createExecuteAdapter",
  "bal.enabledMember",
  "bal.enabledSpec",
  "bal.enabledDerivation",
  "bal.parallelSelection",
  "route.ethereumProcessorType",
  "route.ethereumBaseType",
  "route.genericBaseType",
  "fork.amsterdam",
  "route.systemPredicate",
  "options.commit",
]
def semanticOperationIds : List String := [
  "di.blockProcessorExecutorParameter",
  "di.blockProcessorExecutorStore",
  "di.decoratorInnerParameter",
  "di.decoratorBalParameter",
  "di.directAdapterParameter",
  "di.executeProcessorParameter",
  "context.reset.executionGas",
  "context.reset.stateGas",
  "context.install",
  "process.toExecuteCore",
  "route.systemGuard",
  "route.ordinaryExecute",
  "adapter.commitExecute",
  "adapter.commitProcess",
  "admission.recoverBeforeIntrinsic",
  "admission.intrinsic",
  "admission.contextHandoff",
  "admission.staticValidation",
  "admission.senderValidation",
  "admission.buyGas",
  "admission.incrementNonce",
  "dispatch.prepareFastPath",
  "dispatch.precommit",
  "dispatch.availableGas",
  "dispatch.simpleTransfer",
  "dispatch.evmExcluded",
  "dispatch.fastPathCandidate",
  "dispatch.fastPathCandidateCall",
  "dispatch.fastPathEntryGuard",
  "dispatch.noExecutableCode",
  "dispatch.noExecutableCodeCall",
  "simple.stateCharge",
  "simple.recipientDeadCheck",
  "simple.payValue",
  "simple.recipientWrite",
  "simple.oogForfeit",
  "simple.refund",
  "simple.access",
  "simple.headerAndFees",
  "simple.finalize",
  "fees.counterGuard",
  "fees.payFees",
  "finalize.commit",
  "finalize.receiptSuccess",
  "finalize.receiptFailure",
  "finalize.resultReturn",
  "adapter.startTxTrace",
  "adapter.execute",
  "adapter.endTxTrace",
  "context.executeAdapter",
  "executor.adapterCall",
  "executor.invalidResultGuard",
  "executor.invalidResultThrow",
  "executor.processedEvent",
  "executor.validationMode",
  "executor.processCall",
  "executor.blockGasLimitGuard",
  "executor.gasLimitThrow",
  "context.directExecutor",
  "bal.directInnerGuard",
  "bal.directInnerCall",
  "context.decoratorBal",
  "context.decoratorInner",
  "context.balStore",
  "tracer.blockReset.index",
  "tracer.blockReset.currentTx",
  "tracer.blockReset.tracer",
  "tracer.blockReset.receipts",
  "tracer.blockReset.blockGas",
  "tracer.blockReset.receiptGas",
  "tracer.receiptAppend",
  "tracer.nestedForwardGuard",
  "tracer.delegateSuccess",
  "tracer.currentForwardGuard",
  "tracer.currentSuccess",
  "tracer.gasUpdate",
  "tracer.receiptIndex",
  "tracer.tx-end-delegate",
  "tracer.endTx.indexIncrement",
  "tracer.txStart.currentTx",
  "tracer.txStart.delegate",
  "tracer.txStart.tracer",
  "block.startTrace",
  "block.createContext",
  "block.setContext",
  "block.preCommit",
  "block.fold",
  "block.transactionsExecuted",
  "block.postCommit",
  "block.balPrepareCall",
  "block.processBlockCall",
  "di.processor",
  "di.worldState",
  "di.blockProcessor",
  "di.balManager",
  "di.validationModule",
  "di.directExecutor",
  "di.parallelDecorator",
  "di.adapterFactory",
  "di.adapter",
  "di.createExecuteAdapter",
  "bal.enabledMember",
  "bal.enabledSpec",
  "bal.enabledDerivation",
  "bal.parallelSelection",
  "route.ethereumProcessorType",
  "route.ethereumBaseType",
  "route.genericBaseType",
  "fork.amsterdam",
  "route.systemPredicate",
  "options.commit",
]
def fieldwiseSeamIds : List String := [
  "tx.sender",
  "tx.recipient",
  "tx.value",
  "tx.nonce",
  "tx.type",
  "tx.gasLimit",
  "tx.fees",
  "route.codeOverridable",
  "route.authorizationList",
  "route.forceSimpleTransferDisabled",
  "header.gasUsed",
  "gas.spent",
  "gas.operation",
  "gas.block",
  "gas.state",
  "gas.maxUsed",
  "gas.refund",
  "substate.output",
  "substate.logs",
  "substate.error",
  "substate.exception",
  "receipt.status",
  "receipt.gas",
  "receipt.index",
  "receipt.recipient",
  "receipt.logs",
  "tracer.nestedForward",
  "tracer.currentReceiptForward",
  "result.constructor",
  "result.exception",
  "result.substateError",
]
def sourceStages : List String := [
  "SetBlockExecutionContext",
  "Process -> ExecuteCore -> ordinary Execute",
  "static/stateful admission and IncrementNonce",
  "PrepareSimpleTransferFastPath",
  "exact fast-path eligibility: non-overridable code, null authorization list, force-disable false",
  "pre-execution Commit(spec, commitRoots:false)",
  "CalculateAvailableGas",
  "ExecuteSimpleTransfer",
  "Refund -> UpdateHeaderGasUsedAndPayFees -> FinalizeTransaction",
  "base BlockReceiptsTracer receipt terminal",
]
def excludedBranches : List String := [
  "ExecuteEvmTransaction",
  "BuildUp",
  "Restore",
  "SkipValidation-only system routing",
  "BAL/parallel",
]

structure Block where
  terminal : TerminalBlock
  headerGasUsed : Nat
  standardMainnet : Bool
  chainId : Nat
  forkNumber : Nat
  deriving DecidableEq, Repr

structure TracerSeed where
  receipts : List TerminalReceipt
  gasHistory : List TerminalGasTotals
  cumulativeReceiptGas : Nat
  headerGasUsed : Nat
  currentIndex : Nat
  deriving DecidableEq, Repr

structure MachineInput where
  block : Block
  optionsRaw : Nat
  isSystem : Bool
  parallel : Bool
  balEnabled : Bool
  isCreate : Bool
  isCodeOverridable : Bool
  hasAuthorizationList : Bool
  forceSimpleTransferDisabled : Bool
  recipientLive : Bool
  recipientHasCode : Bool
  recipientHasDelegation : Bool
  tracerSeed : TracerSeed
  deriving DecidableEq, Repr

structure EntryNormality where
  startNewTxTrace : Bool
  execute : Bool
  endTxTrace : Bool
  deriving DecidableEq, Repr

def EntryNormality.valid (entry : EntryNormality) : Bool :=
  entry.startNewTxTrace && entry.execute && entry.endTxTrace

structure NormalReturnPremises where
  setBlockExecutionContext : Bool
  process : Bool
  executeCore : Bool
  ordinaryExecute : Bool
  staticAdmission : Bool
  statefulAdmission : Bool
  nonceAndPreparation : Bool
  preExecutionCommit : Bool
  availableGas : Bool
  executeSimpleTransfer : Bool
  settlement : Bool
  transactionCommit : Bool
  finalizeTransaction : Bool
  executorReturn : Bool
  transactionsExecutedSubscriber : Bool
  postTransactionCommitState : Bool
  receiptTerminal : Bool
  entries : List EntryNormality
  deriving DecidableEq, Repr

def NormalReturnPremises.all (premises : NormalReturnPremises) : Bool :=
  premises.setBlockExecutionContext && premises.process && premises.executeCore &&
  premises.ordinaryExecute && premises.staticAdmission && premises.statefulAdmission &&
  premises.nonceAndPreparation && premises.preExecutionCommit && premises.availableGas &&
  premises.executeSimpleTransfer && premises.settlement && premises.transactionCommit &&
  premises.finalizeTransaction && premises.executorReturn &&
  premises.transactionsExecutedSubscriber && premises.postTransactionCommitState &&
  premises.receiptTerminal

structure SettledEntry where
  transaction : TerminalTransaction
  receipt : TerminalReceipt
  gasTotals : TerminalGasTotals
  recipient : TerminalAddress
  gas : TerminalGas
  output : TerminalBytes
  logs : List TerminalLog
  stateRoot : Option TerminalHash
  status : TerminalStatus
  result : TerminalResult
  nestedTracer : Bool
  currentTxTracerIsTracingReceipt : Bool
  wellFormed : Bool
  deriving DecidableEq, Repr

def NormalReturnPremises.validFor (premises : NormalReturnPremises)
    (entries : List SettledEntry) : Bool :=
  premises.all && (premises.entries.length == entries.length) &&
  (premises.entries.map EntryNormality.valid).all (fun value => value)

def resultIsOk : TerminalResult → Bool
  | .ok => true
  | .evmException _ _ => false

def statusIsSuccess : TerminalStatus → Bool
  | .success => true
  | .failure => false

def onlyOkTerminal (entry : SettledEntry) : Bool :=
  entry.wellFormed && resultIsOk entry.result && statusIsSuccess entry.status

def lastReceipt : List TerminalReceipt → Option TerminalReceipt
  | [] => none
  | [last] => some last
  | _ :: tail => lastReceipt tail

def lastGasTotals : List TerminalGasTotals → Option TerminalGasTotals
  | [] => none
  | [last] => some last
  | _ :: tail => lastGasTotals tail

def receiptObservationMatches (actual expected : TerminalReceipt) : Bool :=
  actual.logs == expected.logs && actual.txType == expected.txType &&
  actual.gasUsedTotal == expected.gasUsedTotal && actual.statusCode == expected.statusCode &&
  actual.recipient == expected.recipient && actual.blockHash == expected.blockHash &&
  actual.blockNumber == expected.blockNumber && actual.index == expected.index &&
  actual.gasUsed == expected.gasUsed &&
  actual.effectiveGasPrice == expected.effectiveGasPrice && actual.sender == expected.sender &&
  actual.contractAddress == expected.contractAddress && actual.txHash == expected.txHash &&
  actual.postTransactionState == expected.postTransactionState &&
  actual.blockGasUsed == expected.blockGasUsed && actual.executionGasUsed == expected.executionGasUsed &&
  actual.storageGasUsed == expected.storageGasUsed && actual.error == expected.error

def gasTotalsObservationMatches (actual expected : TerminalGasTotals) : Bool :=
  actual.executionGas == expected.executionGas && actual.stateGas == expected.stateGas

def freshSequentialTracer (_seed : TracerSeed) (headerGasUsed : Nat) : TerminalState :=
  { receipts := []
    gasHistory := []
    cumulativeReceiptGas := 0
    headerGasUsed := headerGasUsed
    parallel := false
    currentIndex := 0 }

def startNewBlockTrace (input : MachineInput) : TerminalState :=
  freshSequentialTracer input.tracerSeed input.block.headerGasUsed

def freshTracerInvariant (input : MachineInput) (state : TerminalState) : Prop :=
  state.parallel = false ∧ state.currentIndex = 0 ∧ state.receipts = [] ∧
  state.gasHistory = [] ∧ state.cumulativeReceiptGas = 0 ∧
  state.headerGasUsed = input.block.headerGasUsed

def routeValid (input : MachineInput) : Bool :=
  input.optionsRaw == 1 && input.block.standardMainnet && input.block.chainId == 1 &&
  input.block.forkNumber == 25 && !input.isSystem && !input.parallel && !input.balEnabled &&
  !input.isCreate && !input.isCodeOverridable && !input.hasAuthorizationList &&
  !input.forceSimpleTransferDisabled && input.recipientLive && !input.recipientHasCode &&
  !input.recipientHasDelegation

inductive Event where
  | blockStart
  | preTransactionCommit
  | foldStart
  | startNewTxTrace
  | execute
  | transactionCommit
  | gasMutation
  | receiptAppend
  | nestedTracerForward
  | currentTracerForward
  | endTxTrace
  | currentIndexIncrement
  | foldReturn
  | transactionsExecuted
  | postTransactionCommit
  deriving DecidableEq, Repr

def mapTerminalEvent : ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel.Event → Event
  | .gasMutation => .gasMutation
  | .receiptAppend => .receiptAppend
  | .nestedTracerForward => .nestedTracerForward
  | .currentTracerForward => .currentTracerForward

def terminalForwardingEvents (entry : SettledEntry) : List Event :=
  (ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel.forwardingEvents
    entry.nestedTracer entry.currentTxTracerIsTracingReceipt).map mapTerminalEvent

inductive RejectReason where
  | route
  | normalReturn
  | malformedSettled
  | invalidResult
  | exceptionResult
  deriving DecidableEq, Repr

inductive Outcome where
  | rejected (reason : RejectReason) (state : TerminalState) (events : List Event)
      (processed : List TerminalResult)
  | completed (state : TerminalState) (events : List Event)
      (results : List TerminalResult)
  deriving DecidableEq, Repr

def foldEntries (block : Block) (state : TerminalState) (events : List Event)
    (processed : List TerminalResult) : List SettledEntry → Outcome
  | [] => .completed state events processed
  | entry :: tail =>
      if !entry.wellFormed then
        .rejected .malformedSettled state events processed
      else if !resultIsOk entry.result then
        .rejected (match entry.result with | .ok => .invalidResult | .evmException _ _ => .exceptionResult)
          state events processed
      else if !statusIsSuccess entry.status then
        .rejected .malformedSettled state events processed
      else
        let terminal :=
          ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel.markAsSuccess
            state block.terminal entry.transaction entry.recipient entry.gas entry.output
            entry.logs entry.stateRoot entry.nestedTracer entry.currentTxTracerIsTracingReceipt
        let nextState := { terminal.state with currentIndex := state.currentIndex + 1 }
        match lastReceipt terminal.state.receipts, lastGasTotals terminal.state.gasHistory with
        | some receipt, some totals =>
            if !receiptObservationMatches receipt entry.receipt ||
                !gasTotalsObservationMatches totals entry.gasTotals then
              .rejected .malformedSettled state events processed
            else
              let nextEvents := events ++ [Event.startNewTxTrace, Event.execute, Event.transactionCommit] ++
                terminal.events.map mapTerminalEvent ++ [Event.endTxTrace, Event.currentIndexIncrement]
              foldEntries block nextState nextEvents (processed ++ [entry.result]) tail
        | _, _ => .rejected .malformedSettled state events processed

def run (input : MachineInput) (premises : NormalReturnPremises)
    (entries : List SettledEntry) : Outcome :=
  let initial := startNewBlockTrace input
  let initialEvents := [Event.blockStart, Event.preTransactionCommit, Event.foldStart]
  if !routeValid input then
    .rejected .route initial initialEvents []
  else if !premises.validFor entries then
    .rejected .normalReturn initial initialEvents []
  else
    match foldEntries input.block initial initialEvents [] entries with
    | .rejected reason state events processed =>
        .rejected reason state (events ++ [Event.foldReturn]) processed
    | .completed state events results =>
        .completed state (events ++ [Event.foldReturn, Event.transactionsExecuted,
          Event.postTransactionCommit]) results

def receiptIndices (state : TerminalState) : List Nat :=
  state.receipts.map (fun receipt => receipt.index)

def receiptGasTotals (state : TerminalState) : List Nat :=
  state.receipts.map (fun receipt => receipt.gasUsedTotal)

def sequentialIndexInvariant (state : TerminalState) : Prop :=
  receiptIndices state = List.range state.receipts.length

def gasHistoryLengthsMatch (state : TerminalState) : Prop :=
  state.receipts.length = state.gasHistory.length

def onlyOkSuccess (input : MachineInput) (premises : NormalReturnPremises)
    (entries : List SettledEntry) : Prop :=
  routeValid input = true ∧ premises.validFor entries = true ∧
  entries.all onlyOkTerminal = true

end OrdinaryTransactionMachineExtractor.Generated

