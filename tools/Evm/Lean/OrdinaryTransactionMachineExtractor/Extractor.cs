// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;

namespace Nethermind.Evm.Lean.OrdinaryTransactionMachineExtractor;

/// <summary>
/// Binds the deliberately small ordinary-mainnet success route to typed Roslyn evidence.
/// </summary>
/// <remarks>
/// This extractor is an admission boundary, not a C# interpreter. It refuses source drift,
/// incomplete metadata, unresolved operations, unsupported CFG shapes, and ambiguous route
/// selectors before an artifact can be emitted. Effectful production calls are represented in the
/// generated model by explicit observations and normal-return premises.
/// </remarks>
internal static class Extractor
{
    internal const string MachinePinsPath =
        "tools/Evm/Lean/OrdinaryTransactionMachineExtractor/MACHINE_SOURCE_PINS.json";

    internal const string IrFileName = "OrdinaryTransactionMachine.ir.json";
    internal const string ManifestFileName = "OrdinaryTransactionMachine.source-manifest.json";
    internal const string DefaultGeneratedDirectory =
        "tools/Evm/Lean/OrdinaryTransactionMachineExtractor/Generated";
    internal const string DefaultLeanPath =
        "tools/Evm/Lean/OrdinaryTransactionMachineExtractor/Generated/OrdinaryTransactionMachine.lean";

    private const int SchemaVersion = 1;
    private const string ExtractorVersion = "0.1.0";
    private const string Kernel =
        "ordinary standard-mainnet exact-Commit simple-transfer success machine";
    private const string AcceptanceState = "bounded-source-extraction-and-refinement-only";
    private const string MachinePinsSha256 =
        "a39efd211fd169dc84587256bc43c5b6f7df3ef7c3a49fdeecfaa3cc489ed159";
    private const string ReceiptCompilerInventoryPath =
        "tools/Evm/Lean/ReceiptTerminalFoldExtractor/COMPILER_REFERENCE_PINS.json";
    private const string ReceiptCompilerInventorySha256 =
        "adb89ed740f688689cb18edc26d4dc22824eeef2907ed1da91fff420bcff342b";
    private const int ReceiptCompilerInventoryCount = 329;
    private const string ReceiptCompilerInventoryAggregateSha256 =
        "6185996666fdad205d2bf17895ed7b570948deea9ba9ea3d80b28bafb9717ca7";
    private const string ReceiptSourcePinsPath =
        "tools/Evm/Lean/ReceiptTerminalFoldExtractor/SOURCE_PINS.json";
    private const string ReceiptSourcePinsSha256 =
        "3e6c23b10b2c8de17346944d8e187ced0e5ad61703f856dec2bc42cbd55a8fcf";
    private const string ReceiptSourceManifestPath =
        "tools/Evm/Lean/ReceiptTerminalFoldExtractor/Generated/ReceiptTerminalFoldKernel.source-manifest.json";
    private const string ReceiptSourceManifestSha256 =
        "0d9c46495da46d06c112adf10cca8b3b1bac00a8a55d40d65ab29a98f8e9c512";
    private const int ReceiptSourceManifestSchemaVersion = 7;
    private const string ReceiptSourceManifestExtractorVersion = "1.7.0";
    private const string ReceiptSourceManifestCompilerVersion = "5.6.0.0";
    private const string ReceiptSourceManifestLanguageVersion = "14.0";
    private const string ReceiptSourceManifestKernel =
        "standard-mainnet sequential BlockReceiptsTracer terminal receipt fold";
    private const string ReceiptKernelPath =
        "tools/Evm/Lean/ReceiptTerminalFoldExtractor/Generated/ReceiptTerminalFoldKernel.lean";
    private const string ReceiptIrPath =
        "tools/Evm/Lean/ReceiptTerminalFoldExtractor/Generated/ReceiptTerminalFoldKernel.ir.json";
    private const string ReceiptIrSha256 =
        "bc9d568fc89b0ac3d4af6c197a9acac331a60b735985d03e319e5ad545291a97";
    private const string ReceiptRefinementPath =
        "tools/Evm/Lean/ReceiptTerminalFoldExtractor/Refinement/ReceiptTerminalFold.lean";
    private const string ReceiptAccountingSourcePath =
        "src/Nethermind/Nethermind.Blockchain/Tracing/BlockReceiptGasAccountingKernel.cs";
    private const string ReceiptAccountingSourceSha256 =
        "0882c798e6ffb2243735a9ec0cec5d442b20d51277feeba95e5b68ecfaf89e32";
    private const string ReceiptAccountingIrPath =
        "tools/Evm/Lean/Extractor/Generated/BlockReceiptGasAccountingKernel.ir.json";
    private const string ReceiptAccountingLeanPath =
        "tools/Evm/Lean/Eip803x/Generated/BlockReceiptGasAccountingKernel.lean";
    private const string ReceiptAccountingRefinementPath =
        "tools/Evm/Lean/Eip803x/Refinement/BlockReceiptGasAccounting.lean";
    private const string ReceiptAccountingManifestPath =
        "tools/Evm/Lean/Extractor/Generated/BlockReceiptGasAccountingKernel.source-manifest.json";
    private const string ReceiptKernelSha256 =
        "055177970967f1d6de0b3f54ca5c9b89a7e46358d5b95d793872a60c359e7f55";
    private const string ReceiptRefinementSha256 =
        "bd4a3556a98ec40875960ff2180523d16221b05cd6cf9a0b6ce1fc46a8cb3e93";
    private const string ReceiptAccountingKernelSha256 =
        "5d7851084945d5263dd87bcda4ae9e60ce7b779473a7c37617a96cf0e91026f2";
    private const string ReceiptAccountingRefinementSha256 =
        "6d8a1c1de3f99c43f24576a6358e083fcce43cfe23037bd54cfececfbda5dfe5";
    private const string ReceiptAccountingIrSha256 =
        "5278c354453a3adab99df5a68b4475d9feab798eba1e4df42ee80bf34a259c41";
    private const string ReceiptAccountingManifestSha256 =
        "1bc07122e230b88e69f5444f526d6c3654f4788c4479ea114185982471bdc2aa";
    private const string ReceiptTracerSourcePath =
        "src/Nethermind/Nethermind.Blockchain/Tracing/BlockReceiptsTracer.cs";
    private const string ExecuteAdapterSourcePath =
        "src/Nethermind/Nethermind.Evm/TransactionProcessing/ExecuteTransactionProcessorAdapter.cs";
    private const string ProcessorInterfaceSourcePath =
        "src/Nethermind/Nethermind.Evm/TransactionProcessing/ITransactionProcessor.cs";
    private const string AdapterExtensionsSourcePath =
        "src/Nethermind/Nethermind.Consensus/Processing/TransactionProcessorAdapterExtensions.cs";
    private const string VirtualMachineInterfaceSourcePath =
        "src/Nethermind/Nethermind.Evm/IVirtualMachine.cs";
    private const string ReadOnlyStateSourcePath =
        "src/Nethermind/Nethermind.Evm/State/IReadOnlyStateProvider.cs";
    private const string WorldStateExtensionsSourcePath =
        "src/Nethermind/Nethermind.Evm/State/WorldStateExtensions.cs";
    private const string BlockTracerSourcePath =
        "src/Nethermind/Nethermind.Evm/Tracing/IBlockTracer.cs";
    private const string BlockProcessorInterfaceSourcePath =
        "src/Nethermind/Nethermind.Consensus/Processing/IBlockProcessor.cs";
    private const string ContainerBuilderExtensionsSourcePath =
        "src/Nethermind/Nethermind.Core/ContainerBuilderExtensions.cs";
    private const string BlockValidationModuleSourcePath =
        "src/Nethermind/Nethermind.Core/Container/IBlockValidationModule.cs";
    private const string TransactionAdapterFactorySourcePath =
        "src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessorAdapterFactory.cs";
    private const string ReceiptSourceSha256 =
        "d4504f54b50dd43e2ab5bc7172ce2cf9e48453fcede990e5e667e743146262ff";

    private const string TransactionProcessorType =
        "Nethermind.Evm.TransactionProcessing.TransactionProcessorBase`1";
    private const string EthereumProcessorType =
        "Nethermind.Evm.TransactionProcessing.EthereumTransactionProcessor";
    private const string EthereumProcessorBaseType =
        "Nethermind.Evm.TransactionProcessing.EthereumTransactionProcessorBase";
    private const string ExecuteAdapterType =
        "Nethermind.Evm.TransactionProcessing.ExecuteTransactionProcessorAdapter";
    private const string ProcessorExtensionsType =
        "Nethermind.Evm.TransactionProcessing.ITransactionProcessorExtensions";
    private const string AdapterExtensionsType =
        "Nethermind.Consensus.Processing.TransactionProcessorAdapterExtensions";
    private const string BlockProcessorType =
        "Nethermind.Consensus.Processing.BlockProcessor";
    private const string DirectExecutorType =
        "Nethermind.Consensus.Processing.BlockProcessor+BlockValidationTransactionsExecutor";
    private const string ParallelExecutorType =
        "Nethermind.Consensus.Processing.BlockProcessor+ParallelBlockValidationTransactionsExecutor";
    private const string BalManagerType =
        "Nethermind.Consensus.Processing.BlockAccessListManager";
    private const string ReceiptTracerType =
        "Nethermind.Blockchain.Tracing.BlockReceiptsTracer";
    private const string BlockProcessingModuleType =
        "Nethermind.Init.Modules.BlockProcessingModule";
    private const string StandardValidationModuleType =
        "Nethermind.Init.Modules.BlockProcessingModule+StandardBlockValidationModule";
    private const string RoutingKernelType =
        "Nethermind.Evm.TransactionProcessing.SystemTransactionRoutingKernel";
    private const string ExecutionOptionsType =
        "Nethermind.Evm.TransactionProcessing.ExecutionOptions";
    private const string AmsterdamType =
        "Nethermind.Specs.Forks.Amsterdam";

    private static readonly SourceSpec[] SourceSpecs =
    [
        new("src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs", "processor entry, admission, simple transfer, fees, finalization"),
        new("src/Nethermind/Nethermind.Evm/TransactionProcessing/ExecutionOptions.cs", "execution option values"),
        new("src/Nethermind/Nethermind.Evm/TransactionProcessing/SystemTransactionRoutingKernel.cs", "ordinary/system route guard"),
        new("src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionSettlementKernel.cs", "settlement identity"),
        new("src/Nethermind/Nethermind.Evm/TransactionProcessing/ITransactionProcessor.cs", "processor interface"),
        new("src/Nethermind/Nethermind.Evm/TransactionProcessing/ITransactionProcessorAdapter.cs", "transaction adapter interface"),
        new("src/Nethermind/Nethermind.Evm/TransactionProcessing/ExecuteTransactionProcessorAdapter.cs", "standard adapter forwarding"),
        new("src/Nethermind/Nethermind.Evm/GasPolicy/IGasPolicy.cs", "gas policy interface"),
        new("src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs", "Ethereum gas policy"),
        new("src/Nethermind/Nethermind.Evm/TransactionProcessing/GasConsumed.cs", "gas observation"),
        new("src/Nethermind/Nethermind.Evm/TransactionSubstate.cs", "substate observation"),
        new("src/Nethermind/Nethermind.Evm/Tracing/ITxTracer.cs", "transaction tracer contract"),
        new("src/Nethermind/Nethermind.Evm/State/IWorldState.cs", "world-state balance/commit contract"),
        new("src/Nethermind/Nethermind.Evm/BlockExecutionContext.cs", "block context"),
        new("src/Nethermind/Nethermind.Core/Transaction.cs", "transaction fields and predicates"),
        new("src/Nethermind/Nethermind.Core/TransactionExtensions.cs", "price and transaction predicates"),
        new("src/Nethermind/Nethermind.Core/BlockHeader.cs", "header fields"),
        new("src/Nethermind/Nethermind.Core/TransactionReceipt.cs", "receipt fields"),
        new("src/Nethermind/Nethermind.Consensus/Processing/TransactionProcessorAdapterExtensions.cs", "Start/Execute/End adapter order"),
        new("src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.BlockValidationTransactionsExecutor.cs", "sequential direct-inner caller"),
        new("src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.ParallelBlockValidationTransactionsExecutor.cs", "BAL decorator guard"),
        new("src/Nethermind/Nethermind.Consensus/Processing/BlockAccessListManager.cs", "BAL manager identity"),
        new("src/Nethermind/Nethermind.Consensus/Processing/IBlockAccessListManager.cs", "BAL manager contract"),
        new("src/Nethermind/Nethermind.Init/Modules/BlockProcessingModule.cs", "standard DI route"),
        new("src/Nethermind/Nethermind.Blockchain/Tracing/BlockReceiptsTracer.cs", "exact base receipt tracer"),
        new("src/Nethermind/Nethermind.Core/Specs/IReleaseSpec.cs", "release-spec capability surface"),
        new("src/Nethermind/Nethermind.Specs/Forks/25_Amsterdam.cs", "Amsterdam fork identity"),
        new("src/Nethermind/Nethermind.Specs/ReleaseSpec.cs", "release-spec implementation"),
        new("src/Nethermind/Nethermind.Core/Specs/ISpecProvider.cs", "spec-provider contract"),
        new("src/Nethermind/Nethermind.Specs/ChainSpecStyle/ChainSpecBasedSpecProvider.cs", "chainspec fork derivation"),
        new("src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", "block route, TransactionsExecuted callback and post-transaction CommitState"),
        new("src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.std.cs", "standard BlockProcessor partial implementations needed for source compilation"),
        new("src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.BlockAccessListSystemContractHandler.cs", "standard BlockProcessor BAL system-contract partial needed for source compilation"),
        new("src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.SystemContractHandler.cs", "standard BlockProcessor system-contract partial needed for source compilation"),
        new("src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.BlockProductionTransactionPicker.cs", "standard BlockProcessor production-picker partial closure"),
        new("src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.BlockProductionTransactionsExecutor.cs", "standard BlockProcessor production-executor partial closure"),
        new("src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.IBlockProductionTransactionPicker.cs", "standard BlockProcessor production-picker contract partial closure"),
        new("src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.IBlockProductionTransactionsExecutor.cs", "standard BlockProcessor production-executor contract partial closure"),
        new("src/Nethermind/Nethermind.Consensus/Processing/BlockAccessListManager.StateChanges.cs", "BAL manager state-change partial needed for source compilation"),
        new("src/Nethermind/Nethermind.Consensus/Processing/BlockAccessListManager.SystemContracts.cs", "BAL manager system-contract partial needed for source compilation"),
        new("src/Nethermind/Nethermind.Consensus/Processing/BlockAccessListManager.TxProcessorPool.cs", "BAL manager processor-pool partial needed for source compilation"),
        new("src/Nethermind/Nethermind.Consensus/Processing/BlockAccessListManager.Validation.cs", "BAL manager validation partial needed for source compilation"),
        new(VirtualMachineInterfaceSourcePath, "VM block-context target identity"),
        new(ReadOnlyStateSourcePath, "recipient-account predicate target identity"),
        new(WorldStateExtensionsSourcePath, "world-state balance extension target identity"),
        new(BlockTracerSourcePath, "block-tracer forwarding target identities"),
        new(BlockProcessorInterfaceSourcePath, "block transaction-executor target identities"),
        new(ContainerBuilderExtensionsSourcePath, "standard DI fluent target identities"),
        new(BlockValidationModuleSourcePath, "standard block-validation module contract identity"),
        new(TransactionAdapterFactorySourcePath, "transaction adapter factory type identity"),
    ];

    private static readonly SourceMemberIdentity[] SourceMemberLedger =
    [
        Method(SourceSpecs[0].Path, TransactionProcessorType, "SetBlockExecutionContext", "publicvoidSetBlockExecutionContext(inBlockExecutionContextblockExecutionContext)", ["BlockExecutionContext"], ["in"]),
        Method(SourceSpecs[0].Path, TransactionProcessorType, "SetBlockExecutionContext", "publicvoidSetBlockExecutionContext(BlockHeaderheader)", ["BlockHeader"]),
        Method(SourceSpecs[0].Path, TransactionProcessorType, "Process", "publicTransactionResultProcess(Transactiontransaction,ITxTracertxTracer,ExecutionOptionsoptions)", ["Transaction", "ITxTracer", "ExecutionOptions"]),
        Method(SourceSpecs[0].Path, TransactionProcessorType, "ExecuteCore", "privateTransactionResultExecuteCore(Transactiontx,ITxTracertracer,ExecutionOptionsopts)", ["Transaction", "ITxTracer", "ExecutionOptions"]),
        Method(SourceSpecs[0].Path, TransactionProcessorType, "Execute", "protectedvirtualTransactionResultExecute(Transactiontx,ITxTracertracer,ExecutionOptionsopts)", ["Transaction", "ITxTracer", "ExecutionOptions"]),
        Method(SourceSpecs[0].Path, TransactionProcessorType, "Execute", "privateTransactionResultExecute(Transactiontx,ITxTracertracer,ExecutionOptionsopts,BlockHeaderheader,IReleaseSpecspec,inIntrinsicGas<TGasPolicy>intrinsicGas)", ["Transaction", "ITxTracer", "ExecutionOptions", "BlockHeader", "IReleaseSpec", "IntrinsicGas<TGasPolicy>"], ["none", "none", "none", "none", "none", "in"]),
        Method(SourceSpecs[0].Path, TransactionProcessorType, "IsSimpleTransferFastPathCandidate", "privatestaticboolIsSimpleTransferFastPathCandidate(Transactiontx,boolisCodeOverridable)", ["Transaction", "bool"]),
        Method(SourceSpecs[0].Path, TransactionProcessorType, "HasNoExecutableCode", "privatestaticboolHasNoExecutableCode(CodeInfocodeInfo,Address?delegationAddress)", ["CodeInfo", "Address?"]),
        Method(SourceSpecs[0].Path, TransactionProcessorType, "PrepareSimpleTransferFastPath", "privateAddress?PrepareSimpleTransferFastPath(Transactiontx,IReleaseSpecspec,outCodeInfo?preloadedCodeInfo,outAddress?preloadedDelegationAddress)", ["Transaction", "IReleaseSpec", "CodeInfo?", "Address?"], ["none", "none", "out", "out"]),
        Method(SourceSpecs[0].Path, TransactionProcessorType, "ExecuteSimpleTransfer", "privateTransactionResultExecuteSimpleTransfer(Transactiontx,BlockHeaderheader,IReleaseSpecspec,ITxTracertracer,ExecutionOptionsopts,boolrestore,boolcommit,booldeleteCallerAccount,Addressrecipient,inIntrinsicGas<TGasPolicy>intrinsicGas,TGasPolicygasAvailable,inUInt256opcodeGasPrice,inUInt256premiumPerGas,inUInt256senderReservedGasPayment,inUInt256blobBaseFee)", ["Transaction", "BlockHeader", "IReleaseSpec", "ITxTracer", "ExecutionOptions", "bool", "bool", "bool", "Address", "IntrinsicGas<TGasPolicy>", "TGasPolicy", "UInt256", "UInt256", "UInt256", "UInt256"], ["none", "none", "none", "none", "none", "none", "none", "none", "none", "in", "none", "in", "in", "in", "in"]),
        Method(SourceSpecs[0].Path, TransactionProcessorType, "UpdateHeaderGasUsedAndPayFees", "privatevoidUpdateHeaderGasUsedAndPayFees(Transactiontx,BlockHeaderheader,IReleaseSpecspec,ITxTracertracer,ExecutionOptionsopts,inTransactionSubstatesubstate,inGasConsumedspentGas,inUInt256premiumPerGas,inUInt256effectiveGasPrice,inUInt256blobBaseFee,intstatusCode)", ["Transaction", "BlockHeader", "IReleaseSpec", "ITxTracer", "ExecutionOptions", "TransactionSubstate", "GasConsumed", "UInt256", "UInt256", "UInt256", "int"], ["none", "none", "none", "none", "none", "in", "in", "in", "in", "in", "none"]),
        Method(SourceSpecs[0].Path, TransactionProcessorType, "FinalizeTransaction", "privateTransactionResultFinalizeTransaction(Transactiontx,IReleaseSpecspec,ITxTracertracer,ExecutionOptionsopts,boolrestore,boolcommit,booldeleteCallerAccount,inUInt256senderReservedGasPayment,AddressexecutingAccount,inTransactionSubstatesubstate,GasConsumedspentGas,intstatusCode)", ["Transaction", "IReleaseSpec", "ITxTracer", "ExecutionOptions", "bool", "bool", "bool", "UInt256", "Address", "TransactionSubstate", "GasConsumed", "int"], ["none", "none", "none", "none", "none", "none", "none", "in", "none", "in", "none", "none"]),
        Method(SourceSpecs[0].Path, TransactionProcessorType, "RecoverSenderBeforeIntrinsicGas", "privatevoidRecoverSenderBeforeIntrinsicGas(Transactiontx,IReleaseSpecspec)", ["Transaction", "IReleaseSpec"]),
        Method(SourceSpecs[0].Path, TransactionProcessorType, "CalculateIntrinsicGas", "protectedvirtualIntrinsicGas<TGasPolicy>CalculateIntrinsicGas(Transactiontx,IReleaseSpecspec,ulongblockGasLimit)", ["Transaction", "IReleaseSpec", "ulong"]),
        Method(SourceSpecs[0].Path, TransactionProcessorType, "ValidateStatic", "protectedvirtualTransactionResultValidateStatic(Transactiontx,BlockHeaderheader,IReleaseSpecspec,ExecutionOptionsopts,inIntrinsicGas<TGasPolicy>intrinsicGas)", ["Transaction", "BlockHeader", "IReleaseSpec", "ExecutionOptions", "IntrinsicGas<TGasPolicy>"], ["none", "none", "none", "none", "in"]),
        Method(SourceSpecs[0].Path, TransactionProcessorType, "ValidateSender", "protectedvirtualTransactionResultValidateSender(Transactiontx,BlockHeaderheader,IReleaseSpecspec,ITxTracertracer,ExecutionOptionsopts)", ["Transaction", "BlockHeader", "IReleaseSpec", "ITxTracer", "ExecutionOptions"]),
        Method(SourceSpecs[0].Path, TransactionProcessorType, "BuyGas", "protectedvirtualTransactionResultBuyGas(Transactiontx,IReleaseSpecspec,ITxTracertracer,ExecutionOptionsopts,inUInt256effectiveGasPrice,outUInt256premiumPerGas,outUInt256senderReservedGasPayment,outUInt256blobBaseFee)", ["Transaction", "IReleaseSpec", "ITxTracer", "ExecutionOptions", "UInt256", "UInt256", "UInt256", "UInt256"], ["none", "none", "none", "none", "in", "out", "out", "out"]),
        Method(SourceSpecs[0].Path, TransactionProcessorType, "IncrementNonce", "protectedvirtualTransactionResultIncrementNonce(Transactiontx,BlockHeaderheader,IReleaseSpecspec,ITxTracertracer,ExecutionOptionsopts)", ["Transaction", "BlockHeader", "IReleaseSpec", "ITxTracer", "ExecutionOptions"]),
        Method(SourceSpecs[0].Path, TransactionProcessorType, "CalculateAvailableGas", "protectedvirtualTransactionResultCalculateAvailableGas(Transactiontx,IReleaseSpecspec,inIntrinsicGas<TGasPolicy>intrinsicGas,outTGasPolicygasAvailable)", ["Transaction", "IReleaseSpec", "IntrinsicGas<TGasPolicy>", "TGasPolicy"], ["none", "none", "in", "out"]),
        Method(SourceSpecs[0].Path, TransactionProcessorType, "ExecuteEvmTransaction", "privateTransactionResultExecuteEvmTransaction(Transactiontx,BlockHeaderheader,IReleaseSpecspec,ITxTracertracer,ExecutionOptionsopts,boolrestore,boolcommit,booldeleteCallerAccount,inIntrinsicGas<TGasPolicy>intrinsicGas,TGasPolicygasAvailable,inUInt256opcodeGasPrice,inUInt256premiumPerGas,inUInt256senderReservedGasPayment,inUInt256blobBaseFee,CodeInfo?preloadedCodeInfo,Address?preloadedDelegationAddress)", ["Transaction", "BlockHeader", "IReleaseSpec", "ITxTracer", "ExecutionOptions", "bool", "bool", "bool", "IntrinsicGas<TGasPolicy>", "TGasPolicy", "UInt256", "UInt256", "UInt256", "UInt256", "CodeInfo?", "Address?"], ["none", "none", "none", "none", "none", "none", "none", "none", "in", "none", "in", "in", "in", "in", "none", "none"]),
        Method(SourceSpecs[0].Path, TransactionProcessorType, "PayValue", "protectedvirtualvoidPayValue(Transactiontx,IReleaseSpecspec,ExecutionOptionsopts)", ["Transaction", "IReleaseSpec", "ExecutionOptions"]),
        Method(SourceSpecs[0].Path, TransactionProcessorType, "Refund", "protectedvirtualGasConsumedRefund(Transactiontx,BlockHeaderheader,IReleaseSpecspec,ExecutionOptionsopts,inTransactionSubstatesubstate,inTGasPolicyunspentGas,inUInt256gasPrice,ulongcodeInsertRefunds,inTGasPolicyfloorGas,inTGasPolicyintrinsicGasStandard,longpostIntrinsicStateReservoir,booltopLevelCreateStateGasCharged=false)", ["Transaction", "BlockHeader", "IReleaseSpec", "ExecutionOptions", "TransactionSubstate", "TGasPolicy", "UInt256", "ulong", "TGasPolicy", "TGasPolicy", "long", "bool"], ["none", "none", "none", "none", "in", "in", "in", "none", "in", "in", "none", "none"]),
        Method(SourceSpecs[0].Path, TransactionProcessorType, "ReportSimpleTransferAccess", "privatevoidReportSimpleTransferAccess(Transactiontx,IReleaseSpecspec,ITxTracertracer,Addressrecipient)", ["Transaction", "IReleaseSpec", "ITxTracer", "Address"]),
        Method(SourceSpecs[0].Path, TransactionProcessorType, "PayFees", "protectedvirtualvoidPayFees(Transactiontx,BlockHeaderheader,IReleaseSpecspec,ITxTracertracer,inTransactionSubstatesubstate,ulongspentGas,inUInt256premiumPerGas,inUInt256effectiveGasPrice,inUInt256blobBaseFee,intstatusCode)", ["Transaction", "BlockHeader", "IReleaseSpec", "ITxTracer", "TransactionSubstate", "ulong", "UInt256", "UInt256", "UInt256", "int"], ["none", "none", "none", "none", "in", "none", "in", "in", "in", "none"]),
        Method(ExecuteAdapterSourcePath, ExecuteAdapterType, "Execute", "publicTransactionResultExecute(Transactiontransaction,ITxTracertxTracer)", ["Transaction", "ITxTracer"]),
        Method(ExecuteAdapterSourcePath, ExecuteAdapterType, "SetBlockExecutionContext", "publicvoidSetBlockExecutionContext(inBlockExecutionContextblockExecutionContext)", ["BlockExecutionContext"], ["in"]),
        Method(ProcessorInterfaceSourcePath, ProcessorExtensionsType, "Execute", "publicTransactionResultExecute(Transactiontransaction,ITxTracertxTracer)", ["Transaction", "ITxTracer"]),
        Method(AdapterExtensionsSourcePath, AdapterExtensionsType, "ProcessTransaction", "publicstaticTransactionResultProcessTransaction(thisITransactionProcessorAdaptertransactionProcessor,TransactioncurrentTx,BlockReceiptsTracerreceiptsTracer,ProcessingOptionsprocessingOptions,IWorldStatestateProvider)", ["ITransactionProcessorAdapter", "Transaction", "BlockReceiptsTracer", "ProcessingOptions", "IWorldState"]),
        Method(SourceSpecs[19].Path, DirectExecutorType, "ProcessTransactions", "publicTxReceipt[]ProcessTransactions(Blockblock,ProcessingOptionsprocessingOptions,BlockReceiptsTracerreceiptsTracer,CancellationTokentoken)", ["Block", "ProcessingOptions", "BlockReceiptsTracer", "CancellationToken"]),
        Method(SourceSpecs[19].Path, DirectExecutorType, "ProcessTransaction", "protectedvirtualvoidProcessTransaction(Blockblock,TransactioncurrentTx,intindex,BlockReceiptsTracerreceiptsTracer,ProcessingOptionsprocessingOptions)", ["Block", "Transaction", "int", "BlockReceiptsTracer", "ProcessingOptions"]),
        Method(SourceSpecs[19].Path, DirectExecutorType, "SetBlockExecutionContext", "publicvoidSetBlockExecutionContext(inBlockExecutionContextblockExecutionContext)", ["BlockExecutionContext"], ["in"]),
        Method(SourceSpecs[19].Path, DirectExecutorType, "ThrowInvalidTransactionException", "internalstaticvoidThrowInvalidTransactionException(TransactionResultresult,BlockHeaderheader,TransactioncurrentTx,intindex)", ["TransactionResult", "BlockHeader", "Transaction", "int"]),
        Method(SourceSpecs[19].Path, DirectExecutorType + "+ITransactionProcessedEventHandler", "OnTransactionProcessed", "voidOnTransactionProcessed(TxProcessedEventArgstxProcessedEventArgs)", ["TxProcessedEventArgs"]),
        Method(SourceSpecs[20].Path, ParallelExecutorType, "ProcessTransactions", "publicTxReceipt[]ProcessTransactions(Blockblock,ProcessingOptionsprocessingOptions,BlockReceiptsTracerreceiptsTracer,CancellationTokentoken)", ["Block", "ProcessingOptions", "BlockReceiptsTracer", "CancellationToken"]),
        Method(SourceSpecs[20].Path, ParallelExecutorType, "SetBlockExecutionContext", "publicvoidSetBlockExecutionContext(inBlockExecutionContextblockExecutionContext)", ["BlockExecutionContext"], ["in"]),
        Method(SourceSpecs[24].Path, ReceiptTracerType, "StartNewBlockTrace", "publicvoidStartNewBlockTrace(Blockblock)", ["Block"]),
        Method(SourceSpecs[24].Path, ReceiptTracerType, "StartNewTxTrace", "publicITxTracerStartNewTxTrace(Transaction?tx)", ["Transaction?"]),
        Method(SourceSpecs[24].Path, ReceiptTracerType, "EndTxTrace", "publicvoidEndTxTrace()", []),
        Method(SourceSpecs[24].Path, ReceiptTracerType, "MarkAsSuccess", "publicvoidMarkAsSuccess(Addressrecipient,inGasConsumedgasSpent,byte[]output,LogEntry[]logs,Hash256?stateRoot=null)", ["Address", "GasConsumed", "byte[]", "LogEntry[]", "Hash256?"], ["none", "in", "none", "none", "none"]),
        Method(SourceSpecs[24].Path, ReceiptTracerType, "BuildReceipt", "protectedvirtualTxReceiptBuildReceipt(Addressrecipient,inGasConsumedgasConsumed,bytestatusCode,LogEntry[]logEntries,Hash256?stateRoot)", ["Address", "GasConsumed", "byte", "LogEntry[]", "Hash256?"], ["none", "in", "none", "none", "none"]),
        Method(SourceSpecs[24].Path, ReceiptTracerType, "UpdateCumulativeGasTracking", "protectedulongUpdateCumulativeGasTracking(inGasConsumedgasConsumed)", ["GasConsumed"], ["in"]),
        Method(SourceSpecs[30].Path, BlockProcessorType, "ProcessBlock", "protectedvirtualTxReceipt[]ProcessBlock(Blockblock,IBlockTracerblockTracer,ProcessingOptionsoptions,IReleaseSpecspec,CancellationTokentoken)", ["Block", "IBlockTracer", "ProcessingOptions", "IReleaseSpec", "CancellationToken"]),
        Method(SourceSpecs[30].Path, BlockProcessorType, "ProcessOne", "public(BlockBlock,TxReceipt[]Receipts)ProcessOne(BlocksuggestedBlock,ProcessingOptionsoptions,IBlockTracerblockTracer,IReleaseSpecspec,CancellationTokentoken)", ["Block", "ProcessingOptions", "IBlockTracer", "IReleaseSpec", "CancellationToken"]),
        Method(SourceSpecs[30].Path, BlockProcessorType, "CreateBlockExecutionContext", "protectedvirtualBlockExecutionContextCreateBlockExecutionContext(BlockHeaderheader,IReleaseSpecspec)", ["BlockHeader", "IReleaseSpec"]),
        Method(SourceSpecs[30].Path, BlockProcessorType, "CommitState", "privatevoidCommitState(IReleaseSpecspec)", ["IReleaseSpec"]),
        Method(SourceSpecs[21].Path, BalManagerType, "PrepareForProcessing", "publicvoidPrepareForProcessing(BlocksuggestedBlock,IReleaseSpecspec,ProcessingOptionsoptions)", ["Block", "IReleaseSpec", "ProcessingOptions"]),
        Method(SourceSpecs[21].Path, BalManagerType, "SetBlockExecutionContext", "publicvoidSetBlockExecutionContext(inBlockExecutionContextblockExecutionContext)", ["BlockExecutionContext"], ["in"]),
        Method(SourceSpecs[23].Path, BlockProcessingModuleType, "Load", "protectedoverridevoidLoad(ContainerBuilderbuilder)", ["ContainerBuilder"]),
        Method(SourceSpecs[23].Path, StandardValidationModuleType, "Load", "protectedoverridevoidLoad(ContainerBuilderbuilder)", ["ContainerBuilder"]),
        Method(SourceSpecs[23].Path, BlockProcessingModuleType, "CreateExecuteAdapter", "privatestaticITransactionProcessorAdapterCreateExecuteAdapter(ITransactionProcessortransactionProcessor)", ["ITransactionProcessor"]),
        Method(SourceSpecs[2].Path, RoutingKernelType, "UseSystemProcessor", "internalstaticboolUseSystemProcessor(boolisSystemTransaction,ExecutionOptionsoptions)", ["bool", "ExecutionOptions"]),
        Method(SourceSpecs[2].Path, RoutingKernelType, "ParticipatesInNormalBlockCounters", "internalstaticboolParticipatesInNormalBlockCounters(ExecutionOptionsoptions,boolparallel)", ["ExecutionOptions", "bool"]),
        Method(VirtualMachineInterfaceSourcePath, "Nethermind.Evm.IVirtualMachine`1", "SetBlockExecutionContext", "voidSetBlockExecutionContext(inBlockExecutionContextblockExecutionContext)", ["BlockExecutionContext"], ["in"]),
        Method(SourceSpecs[7].Path, "Nethermind.Evm.GasPolicy.IGasPolicy`1", "ClearExecutionGas", "staticabstractvoidClearExecutionGas(refTSelfgas)", ["TSelf"], ["ref"]),
        Method(SourceSpecs[7].Path, "Nethermind.Evm.GasPolicy.IGasPolicy`1", "TryConsumeStateGas", "staticvirtualboolTryConsumeStateGas(refTSelfgas,longstateGasCost)", ["TSelf", "long"], ["ref", "none"]),
        Method(ReadOnlyStateSourcePath, "Nethermind.Evm.State.IReadOnlyStateProvider", "IsDeadAccount", "boolIsDeadAccount(Addressaddress)", ["Address"]),
        Method(SourceSpecs[12].Path, "Nethermind.Evm.State.IWorldState", "Commit", "voidCommit(IReleaseSpecreleaseSpec,IWorldStateTracertracer,boolisGenesis=false,boolcommitRoots=true)", ["IReleaseSpec", "IWorldStateTracer", "bool", "bool"]),
        Method(WorldStateExtensionsSourcePath, "Nethermind.Evm.State.WorldStateExtensions", "AddToBalanceAndCreateIfNotExists", "publicstaticboolAddToBalanceAndCreateIfNotExists(thisIWorldStateworldState,Addressaddress,inUInt256balanceChange,IReleaseSpecspec)", ["IWorldState", "Address", "UInt256", "IReleaseSpec"], ["none", "none", "in", "none"]),
        Method(SourceSpecs[11].Path, "Nethermind.Evm.Tracing.ITxTracer", "MarkAsSuccess", "voidMarkAsSuccess(Addressrecipient,inGasConsumedgasSpent,byte[]output,LogEntry[]logs,Hash256?stateRoot=null)", ["Address", "GasConsumed", "byte[]", "LogEntry[]", "Hash256?"], ["none", "in", "none", "none", "none"]),
        Method(SourceSpecs[11].Path, "Nethermind.Evm.Tracing.ITxTracer", "MarkAsFailed", "voidMarkAsFailed(Addressrecipient,inGasConsumedgasSpent,byte[]output,string?error,Hash256?stateRoot=null)", ["Address", "GasConsumed", "byte[]", "string?", "Hash256?"], ["none", "in", "none", "none", "none"]),
        Method(ProcessorInterfaceSourcePath, "Nethermind.Evm.TransactionProcessing.ITransactionProcessor", "Process", "TransactionResultProcess(Transactiontransaction,ITxTracertxTracer,ExecutionOptionsoptions)", ["Transaction", "ITxTracer", "ExecutionOptions"]),
        Method(ProcessorInterfaceSourcePath, "Nethermind.Evm.TransactionProcessing.ITransactionProcessor", "SetBlockExecutionContext", "voidSetBlockExecutionContext(BlockHeaderblockHeader)", ["BlockHeader"]),
        Method(ProcessorInterfaceSourcePath, "Nethermind.Evm.TransactionProcessing.ITransactionProcessor", "SetBlockExecutionContext", "voidSetBlockExecutionContext(inBlockExecutionContextblockExecutionContext)", ["BlockExecutionContext"], ["in"]),
        Method(SourceSpecs[5].Path, "Nethermind.Evm.TransactionProcessing.ITransactionProcessorAdapter", "Execute", "TransactionResultExecute(Transactiontransaction,ITxTracertxTracer)", ["Transaction", "ITxTracer"]),
        Method(SourceSpecs[5].Path, "Nethermind.Evm.TransactionProcessing.ITransactionProcessorAdapter", "SetBlockExecutionContext", "voidSetBlockExecutionContext(inBlockExecutionContextblockExecutionContext)", ["BlockExecutionContext"], ["in"]),
        Method(BlockTracerSourcePath, "Nethermind.Evm.Tracing.IBlockTracer", "StartNewTxTrace", "ITxTracerStartNewTxTrace(Transaction?tx)", ["Transaction?"]),
        Method(BlockTracerSourcePath, "Nethermind.Evm.Tracing.IBlockTracer", "EndTxTrace", "voidEndTxTrace()", []),
        Method(BlockProcessorInterfaceSourcePath, "Nethermind.Consensus.Processing.IBlockProcessor+IBlockTransactionsExecutor", "ProcessTransactions", "TxReceipt[]ProcessTransactions(Blockblock,ProcessingOptionsprocessingOptions,BlockReceiptsTracerreceiptsTracer,CancellationTokentoken=default)", ["Block", "ProcessingOptions", "BlockReceiptsTracer", "CancellationToken"]),
        Method(BlockProcessorInterfaceSourcePath, "Nethermind.Consensus.Processing.IBlockProcessor+IBlockTransactionsExecutor", "SetBlockExecutionContext", "voidSetBlockExecutionContext(inBlockExecutionContextblockExecutionContext)", ["BlockExecutionContext"], ["in"]),
        Method(SourceSpecs[22].Path, "Nethermind.Consensus.Processing.IBlockAccessListManager", "PrepareForProcessing", "voidPrepareForProcessing(BlocksuggestedBlock,IReleaseSpecspec,ProcessingOptionsoptions)", ["Block", "IReleaseSpec", "ProcessingOptions"]),
        Method(SourceSpecs[22].Path, "Nethermind.Consensus.Processing.IBlockAccessListManager", "SetBlockExecutionContext", "voidSetBlockExecutionContext(inBlockExecutionContextblockExecutionContext)", ["BlockExecutionContext"], ["in"]),
        Method(ContainerBuilderExtensionsSourcePath, "Nethermind.Core.ContainerBuilderExtensions", "AddScoped", "publicstaticContainerBuilderAddScoped<T,TImpl>(thisContainerBuilderbuilder)", ["ContainerBuilder"], arity: 2),
        Method(ContainerBuilderExtensionsSourcePath, "Nethermind.Core.ContainerBuilderExtensions", "AddScoped", "publicstaticContainerBuilderAddScoped<T>(thisContainerBuilderbuilder,Tinstance)", ["ContainerBuilder", "T"], arity: 1),
        Method(ContainerBuilderExtensionsSourcePath, "Nethermind.Core.ContainerBuilderExtensions", "AddScoped", "publicstaticContainerBuilderAddScoped<T>(thisContainerBuilderbuilder,Func<IComponentContext,T>factoryMethod)", ["ContainerBuilder", "Func<IComponentContext,T>"], arity: 1),
        Method(ContainerBuilderExtensionsSourcePath, "Nethermind.Core.ContainerBuilderExtensions", "AddScoped", "publicstaticContainerBuilderAddScoped<T,TArg0,TArg1>(thisContainerBuilderbuilder,Func<TArg0,TArg1,T>factoryMethod)", ["ContainerBuilder", "Func<TArg0,TArg1,T>"], arity: 3),
        Method(ContainerBuilderExtensionsSourcePath, "Nethermind.Core.ContainerBuilderExtensions", "AddSingleton", "publicstaticContainerBuilderAddSingleton<T,TImpl>(thisContainerBuilderbuilder)", ["ContainerBuilder"], arity: 2),
        Method(ContainerBuilderExtensionsSourcePath, "Nethermind.Core.ContainerBuilderExtensions", "AddDecorator", "publicstaticContainerBuilderAddDecorator<T,TDecorator>(thisContainerBuilderbuilder)", ["ContainerBuilder"], arity: 2),
    ];

    private static readonly InvocationTargetIdentity[] InvocationTargetLedger =
    [
        Target("context.install", "Nethermind.Evm.IVirtualMachine`1", "SetBlockExecutionContext", 1, receiver: "Nethermind.Evm.IVirtualMachine`1", refKinds: ["in"]),
        Target("process.toExecuteCore", TransactionProcessorType, "ExecuteCore", 3, receiver: TransactionProcessorType),
        Target("route.systemGuard", RoutingKernelType, "UseSystemProcessor", 2),
        Target("route.ordinaryExecute", TransactionProcessorType, "Execute", 3, receiver: TransactionProcessorType),
        Target("adapter.commitExecute", ProcessorExtensionsType, "Execute", 2, receiver: "Nethermind.Evm.TransactionProcessing.ITransactionProcessor"),
        Target("adapter.commitProcess", "Nethermind.Evm.TransactionProcessing.ITransactionProcessor", "Process", 3, receiver: "Nethermind.Evm.TransactionProcessing.ITransactionProcessor"),
        Target("admission.recoverBeforeIntrinsic", TransactionProcessorType, "RecoverSenderBeforeIntrinsicGas", 2, receiver: TransactionProcessorType),
        Target("admission.intrinsic", TransactionProcessorType, "CalculateIntrinsicGas", 3, receiver: TransactionProcessorType),
        Target("admission.contextHandoff", TransactionProcessorType, "Execute", 6, receiver: TransactionProcessorType, refKinds: ["none", "none", "none", "none", "none", "in"]),
        Target("admission.staticValidation", TransactionProcessorType, "ValidateStatic", 5, receiver: TransactionProcessorType, refKinds: ["none", "none", "none", "none", "in"]),
        Target("admission.senderValidation", TransactionProcessorType, "ValidateSender", 5, receiver: TransactionProcessorType),
        Target("admission.buyGas", TransactionProcessorType, "BuyGas", 8, receiver: TransactionProcessorType, refKinds: ["none", "none", "none", "none", "in", "out", "out", "out"]),
        Target("admission.incrementNonce", TransactionProcessorType, "IncrementNonce", 5, receiver: TransactionProcessorType),
        Target("dispatch.prepareFastPath", TransactionProcessorType, "PrepareSimpleTransferFastPath", 4, receiver: TransactionProcessorType, refKinds: ["none", "none", "out", "out"]),
        Target("dispatch.precommit", "Nethermind.Evm.State.IWorldState", "Commit", 4, receiver: "Nethermind.Evm.State.IWorldState"),
        Target("dispatch.availableGas", TransactionProcessorType, "CalculateAvailableGas", 4, receiver: TransactionProcessorType, refKinds: ["none", "none", "in", "out"]),
        Target("dispatch.simpleTransfer", TransactionProcessorType, "ExecuteSimpleTransfer", 15, receiver: TransactionProcessorType, refKinds: ["none", "none", "none", "none", "none", "none", "none", "none", "none", "in", "none", "in", "in", "in", "in"]),
        Target("dispatch.evmExcluded", TransactionProcessorType, "ExecuteEvmTransaction", 16, receiver: TransactionProcessorType, refKinds: ["none", "none", "none", "none", "none", "none", "none", "none", "in", "none", "in", "in", "in", "in", "none", "none"]),
        Target("dispatch.fastPathCandidateCall", TransactionProcessorType, "IsSimpleTransferFastPathCandidate", 2),
        Target("dispatch.noExecutableCodeCall", TransactionProcessorType, "HasNoExecutableCode", 2),
        Target("simple.stateCharge", "Nethermind.Evm.GasPolicy.IGasPolicy`1", "TryConsumeStateGas", 2, refKinds: ["ref", "none"]),
        Target("simple.recipientDeadCheck", "Nethermind.Evm.State.IReadOnlyStateProvider", "IsDeadAccount", 1, receiver: "Nethermind.Evm.State.IWorldState"),
        Target("simple.payValue", TransactionProcessorType, "PayValue", 3, receiver: TransactionProcessorType),
        Target("simple.recipientWrite", "Nethermind.Evm.State.WorldStateExtensions", "AddToBalanceAndCreateIfNotExists", 4, receiver: "Nethermind.Evm.State.IWorldState", refKinds: ["none", "none", "in", "none"]),
        Target("simple.oogForfeit", "Nethermind.Evm.GasPolicy.IGasPolicy`1", "ClearExecutionGas", 1, refKinds: ["ref"]),
        Target("simple.refund", TransactionProcessorType, "Refund", 12, receiver: TransactionProcessorType, refKinds: ["none", "none", "none", "none", "in", "in", "in", "none", "in", "in", "none", "none"]),
        Target("simple.access", TransactionProcessorType, "ReportSimpleTransferAccess", 4, receiver: TransactionProcessorType),
        Target("simple.headerAndFees", TransactionProcessorType, "UpdateHeaderGasUsedAndPayFees", 11, receiver: TransactionProcessorType, refKinds: ["none", "none", "none", "none", "none", "in", "in", "in", "in", "in", "none"]),
        Target("simple.finalize", TransactionProcessorType, "FinalizeTransaction", 12, receiver: TransactionProcessorType, refKinds: ["none", "none", "none", "none", "none", "none", "none", "in", "none", "in", "none", "none"]),
        Target("fees.counterGuard", RoutingKernelType, "ParticipatesInNormalBlockCounters", 2),
        Target("fees.payFees", TransactionProcessorType, "PayFees", 10, receiver: TransactionProcessorType, refKinds: ["none", "none", "none", "none", "in", "none", "in", "in", "in", "none"]),
        Target("finalize.commit", "Nethermind.Evm.State.IWorldState", "Commit", 4, receiver: "Nethermind.Evm.State.IWorldState"),
        Target("finalize.receiptSuccess", "Nethermind.Evm.Tracing.ITxTracer", "MarkAsSuccess", 5, receiver: "Nethermind.Evm.Tracing.ITxTracer", refKinds: ["none", "in", "none", "none", "none"]),
        Target("finalize.receiptFailure", "Nethermind.Evm.Tracing.ITxTracer", "MarkAsFailed", 5, receiver: "Nethermind.Evm.Tracing.ITxTracer", refKinds: ["none", "in", "none", "none", "none"]),
        Target("adapter.startTxTrace", ReceiptTracerType, "StartNewTxTrace", 1, receiver: ReceiptTracerType),
        Target("adapter.execute", "Nethermind.Evm.TransactionProcessing.ITransactionProcessorAdapter", "Execute", 2, receiver: "Nethermind.Evm.TransactionProcessing.ITransactionProcessorAdapter"),
        Target("adapter.endTxTrace", ReceiptTracerType, "EndTxTrace", 0, receiver: ReceiptTracerType),
        Target("context.executeAdapter", "Nethermind.Evm.TransactionProcessing.ITransactionProcessor", "SetBlockExecutionContext", 1, receiver: "Nethermind.Evm.TransactionProcessing.ITransactionProcessor", refKinds: ["in"]),
        Target("executor.adapterCall", AdapterExtensionsType, "ProcessTransaction", 5, receiver: "Nethermind.Evm.TransactionProcessing.ITransactionProcessorAdapter"),
        Target("executor.invalidResultThrow", DirectExecutorType, "ThrowInvalidTransactionException", 4),
        Target("executor.processedEvent", DirectExecutorType + "+ITransactionProcessedEventHandler", "OnTransactionProcessed", 1, receiver: DirectExecutorType + "+ITransactionProcessedEventHandler"),
        Target("executor.processCall", DirectExecutorType, "ProcessTransaction", 5, receiver: DirectExecutorType),
        Target("executor.gasLimitThrow", DirectExecutorType, "ThrowInvalidBlockForGasLimit", 1,
            kind: LedgerMemberKind.LocalFunction, canonicalSyntax: "staticvoidThrowInvalidBlockForGasLimit(Blockblock)"),
        Target("context.directExecutor", "Nethermind.Evm.TransactionProcessing.ITransactionProcessorAdapter", "SetBlockExecutionContext", 1, receiver: "Nethermind.Evm.TransactionProcessing.ITransactionProcessorAdapter", refKinds: ["in"]),
        Target("bal.directInnerCall", "Nethermind.Consensus.Processing.IBlockProcessor+IBlockTransactionsExecutor", "ProcessTransactions", 4, receiver: "Nethermind.Consensus.Processing.IBlockProcessor+IBlockTransactionsExecutor"),
        Target("context.decoratorBal", "Nethermind.Consensus.Processing.IBlockAccessListManager", "SetBlockExecutionContext", 1, receiver: "Nethermind.Consensus.Processing.IBlockAccessListManager", refKinds: ["in"]),
        Target("context.decoratorInner", "Nethermind.Consensus.Processing.IBlockProcessor+IBlockTransactionsExecutor", "SetBlockExecutionContext", 1, receiver: "Nethermind.Consensus.Processing.IBlockProcessor+IBlockTransactionsExecutor", refKinds: ["in"]),
        Target("tracer.blockReset.receipts", "System.Collections.Generic.List`1", "Clear", 0, receiver: "System.Collections.Generic.List`1"),
        Target("tracer.blockReset.blockGas", "System.Collections.Generic.List`1", "Clear", 0, receiver: "System.Collections.Generic.List`1"),
        Target("tracer.receiptAppend", ReceiptTracerType, "BuildReceipt", 5, receiver: ReceiptTracerType, refKinds: ["none", "in", "none", "none", "none"]),
        Target("tracer.delegateSuccess", "Nethermind.Evm.Tracing.ITxTracer", "MarkAsSuccess", 5, receiver: "Nethermind.Evm.Tracing.ITxTracer", refKinds: ["none", "in", "none", "none", "none"]),
        Target("tracer.currentSuccess", "Nethermind.Evm.Tracing.ITxTracer", "MarkAsSuccess", 5, receiver: "Nethermind.Evm.Tracing.ITxTracer", refKinds: ["none", "in", "none", "none", "none"]),
        Target("tracer.gasUpdate", ReceiptTracerType, "UpdateCumulativeGasTracking", 1, receiver: ReceiptTracerType, refKinds: ["in"]),
        Target("tracer.tx-end-delegate", "Nethermind.Evm.Tracing.IBlockTracer", "EndTxTrace", 0, receiver: "Nethermind.Evm.Tracing.IBlockTracer"),
        Target("tracer.txStart.delegate", "Nethermind.Evm.Tracing.IBlockTracer", "StartNewTxTrace", 1, receiver: "Nethermind.Evm.Tracing.IBlockTracer"),
        Target("block.startTrace", ReceiptTracerType, "StartNewBlockTrace", 1, receiver: ReceiptTracerType),
        Target("block.createContext", BlockProcessorType, "CreateBlockExecutionContext", 2, receiver: BlockProcessorType),
        Target("block.setContext", "Nethermind.Consensus.Processing.IBlockProcessor+IBlockTransactionsExecutor", "SetBlockExecutionContext", 1, receiver: "Nethermind.Consensus.Processing.IBlockProcessor+IBlockTransactionsExecutor", refKinds: ["in"]),
        Target("block.preCommit", BlockProcessorType, "CommitState", 1, receiver: BlockProcessorType),
        Target("block.fold", "Nethermind.Consensus.Processing.IBlockProcessor+IBlockTransactionsExecutor", "ProcessTransactions", 4, receiver: "Nethermind.Consensus.Processing.IBlockProcessor+IBlockTransactionsExecutor"),
        Target("block.transactionsExecuted", "System.Action", "Invoke", 0, receiver: "System.Action",
            kind: LedgerMemberKind.DelegateInvoke),
        Target("block.postCommit", BlockProcessorType, "CommitState", 1, receiver: BlockProcessorType),
        Target("block.balPrepareCall", "Nethermind.Consensus.Processing.IBlockAccessListManager", "PrepareForProcessing", 3, receiver: "Nethermind.Consensus.Processing.IBlockAccessListManager"),
        Target("block.processBlockCall", BlockProcessorType, "ProcessBlock", 5, receiver: BlockProcessorType),
        Target("di.processor", "Nethermind.Core.ContainerBuilderExtensions", "AddScoped", 1,
            receiver: "Autofac.ContainerBuilder", arity: 2,
            typeArgumentMetadataNames: ["Nethermind.Evm.TransactionProcessing.ITransactionProcessor", EthereumProcessorType]),
        Target("di.worldState", "Nethermind.Core.ContainerBuilderExtensions", "AddScoped", 1,
            receiver: "Autofac.ContainerBuilder", arity: 2,
            typeArgumentMetadataNames: ["Nethermind.Evm.State.IWorldState", "Nethermind.State.WorldState"]),
        Target("di.blockProcessor", "Nethermind.Core.ContainerBuilderExtensions", "AddScoped", 1,
            receiver: "Autofac.ContainerBuilder", arity: 2,
            typeArgumentMetadataNames: ["Nethermind.Consensus.Processing.IBlockProcessor", BlockProcessorType]),
        Target("di.balManager", "Nethermind.Core.ContainerBuilderExtensions", "AddScoped", 1,
            receiver: "Autofac.ContainerBuilder", arity: 2,
            typeArgumentMetadataNames: ["Nethermind.Consensus.Processing.IBlockAccessListManager", BalManagerType]),
        Target("di.validationModule", "Nethermind.Core.ContainerBuilderExtensions", "AddSingleton", 1,
            receiver: "Autofac.ContainerBuilder", arity: 2,
            typeArgumentMetadataNames: ["Nethermind.Core.Container.IBlockValidationModule", StandardValidationModuleType]),
        Target("di.directExecutor", "Nethermind.Core.ContainerBuilderExtensions", "AddScoped", 1,
            receiver: "Autofac.ContainerBuilder", arity: 2,
            typeArgumentMetadataNames: ["Nethermind.Consensus.Processing.IBlockProcessor+IBlockTransactionsExecutor", DirectExecutorType]),
        Target("di.parallelDecorator", "Nethermind.Core.ContainerBuilderExtensions", "AddDecorator", 1,
            receiver: "Autofac.ContainerBuilder", arity: 2,
            typeArgumentMetadataNames: ["Nethermind.Consensus.Processing.IBlockProcessor+IBlockTransactionsExecutor", ParallelExecutorType]),
        Target("di.adapterFactory", "Nethermind.Core.ContainerBuilderExtensions", "AddScoped", 2,
            receiver: "Autofac.ContainerBuilder", arity: 1,
            parameterTypeMetadataNames: ["Autofac.ContainerBuilder", "System.Func`2"],
            typeArgumentMetadataNames: ["Nethermind.Evm.TransactionProcessing.TransactionProcessorAdapterFactory"]),
        Target("di.adapter", "Nethermind.Core.ContainerBuilderExtensions", "AddScoped", 2,
            receiver: "Autofac.ContainerBuilder", arity: 3,
            typeArgumentMetadataNames: ["Nethermind.Evm.TransactionProcessing.ITransactionProcessorAdapter",
                "Nethermind.Evm.TransactionProcessing.ITransactionProcessor",
                "Nethermind.Evm.TransactionProcessing.TransactionProcessorAdapterFactory"]),
    ];

    private static readonly SourceOwnerIdentity[] SourceOwnerLedger =
    [
        new(SourceSpecs[0].Path, TransactionProcessorType),
        new(SourceSpecs[0].Path, EthereumProcessorType),
        new(SourceSpecs[0].Path, EthereumProcessorBaseType),
        new(ExecuteAdapterSourcePath, ExecuteAdapterType),
        new(ProcessorInterfaceSourcePath, ProcessorExtensionsType),
        new(ProcessorInterfaceSourcePath, "Nethermind.Evm.TransactionProcessing.ITransactionProcessor"),
        new(SourceSpecs[5].Path, "Nethermind.Evm.TransactionProcessing.ITransactionProcessorAdapter"),
        new(AdapterExtensionsSourcePath, AdapterExtensionsType),
        new(SourceSpecs[19].Path, DirectExecutorType),
        new(SourceSpecs[19].Path, DirectExecutorType + "+ITransactionProcessedEventHandler"),
        new(SourceSpecs[20].Path, ParallelExecutorType),
        new(SourceSpecs[21].Path, BalManagerType),
        new(SourceSpecs[22].Path, "Nethermind.Consensus.Processing.IBlockAccessListManager"),
        new(SourceSpecs[23].Path, BlockProcessingModuleType),
        new(SourceSpecs[23].Path, StandardValidationModuleType),
        new(SourceSpecs[24].Path, ReceiptTracerType),
        new(SourceSpecs[26].Path, AmsterdamType),
        new(SourceSpecs[30].Path, BlockProcessorType),
        new(VirtualMachineInterfaceSourcePath, "Nethermind.Evm.IVirtualMachine`1"),
        new(SourceSpecs[7].Path, "Nethermind.Evm.GasPolicy.IGasPolicy`1"),
        new(SourceSpecs[8].Path, "Nethermind.Evm.GasPolicy.EthereumGasPolicy"),
        new(ReadOnlyStateSourcePath, "Nethermind.Evm.State.IReadOnlyStateProvider"),
        new(SourceSpecs[12].Path, "Nethermind.Evm.State.IWorldState"),
        new(WorldStateExtensionsSourcePath, "Nethermind.Evm.State.WorldStateExtensions"),
        new(SourceSpecs[11].Path, "Nethermind.Evm.Tracing.ITxTracer"),
        new(BlockTracerSourcePath, "Nethermind.Evm.Tracing.IBlockTracer"),
        new(BlockProcessorInterfaceSourcePath, "Nethermind.Consensus.Processing.IBlockProcessor"),
        new(BlockProcessorInterfaceSourcePath, "Nethermind.Consensus.Processing.IBlockProcessor+IBlockTransactionsExecutor"),
        new(ContainerBuilderExtensionsSourcePath, "Nethermind.Core.ContainerBuilderExtensions"),
        new(SourceSpecs[1].Path, ExecutionOptionsType),
        new(SourceSpecs[17].Path, "Nethermind.Core.TxReceipt"),
        new(BlockValidationModuleSourcePath, "Nethermind.Core.Container.IBlockValidationModule"),
        new(TransactionAdapterFactorySourcePath, "Nethermind.Evm.TransactionProcessing.TransactionProcessorAdapterFactory"),
    ];

    private static readonly HashSet<string> CompilerOwnedTargetTypes = new(StringComparer.Ordinal)
    {
        "Autofac.ContainerBuilder",
        "System.Action",
        "System.Collections.Generic.List`1",
        "System.Func`2",
        "Nethermind.State.WorldState",
    };

    private static readonly DeclarationIdentity[] DeclarationLedger =
    [
        new("route.ethereumProcessorType", SourceSpecs[0].Path, EthereumProcessorType, LedgerMemberKind.Type,
            "publicsealedclassEthereumTransactionProcessor", 1),
        new("route.ethereumBaseType", SourceSpecs[0].Path, EthereumProcessorBaseType, LedgerMemberKind.Type,
            "publicabstractclassEthereumTransactionProcessorBase", 1),
        new("route.genericBaseType", SourceSpecs[0].Path, TransactionProcessorType, LedgerMemberKind.Type,
            "publicabstractclassTransactionProcessorBase<TGasPolicy>", 1),
        new("fork.amsterdam", SourceSpecs[26].Path, AmsterdamType, LedgerMemberKind.Type,
            "publicclassAmsterdam", 1),
        new("options.commit", SourceSpecs[1].Path, ExecutionOptionsType, LedgerMemberKind.EnumMember,
            "Commit=1", 1),
        new("di.blockProcessorExecutorParameter", SourceSpecs[30].Path, BlockProcessorType, LedgerMemberKind.PrimaryConstructorParameter,
            "IBlockTransactionsExecutorblockTransactionsExecutor", 1, "Nethermind.Consensus.Processing.IBlockProcessor+IBlockTransactionsExecutor"),
        new("di.decoratorInnerParameter", SourceSpecs[20].Path, ParallelExecutorType, LedgerMemberKind.PrimaryConstructorParameter,
            "IBlockProcessor.IBlockTransactionsExecutorinner", 1, "Nethermind.Consensus.Processing.IBlockProcessor+IBlockTransactionsExecutor"),
        new("di.decoratorBalParameter", SourceSpecs[20].Path, ParallelExecutorType, LedgerMemberKind.PrimaryConstructorParameter,
            "IBlockAccessListManagerbalManager", 1, "Nethermind.Consensus.Processing.IBlockAccessListManager"),
        new("di.directAdapterParameter", SourceSpecs[19].Path, DirectExecutorType, LedgerMemberKind.PrimaryConstructorParameter,
            "ITransactionProcessorAdaptertransactionProcessor", 1, "Nethermind.Evm.TransactionProcessing.ITransactionProcessorAdapter"),
        new("di.executeProcessorParameter", ExecuteAdapterSourcePath, ExecuteAdapterType, LedgerMemberKind.PrimaryConstructorParameter,
            "ITransactionProcessortransactionProcessor", 1, "Nethermind.Evm.TransactionProcessing.ITransactionProcessor"),
        new("di.blockProcessorExecutorStore", SourceSpecs[30].Path, BlockProcessorType, LedgerMemberKind.Field,
            "_blockTransactionsExecutor=blockTransactionsExecutor", 1),
        new("di.createExecuteAdapter", SourceSpecs[23].Path, BlockProcessingModuleType, LedgerMemberKind.Constructor,
            "newExecuteTransactionProcessorAdapter(transactionProcessor)", 1, ExecuteAdapterType,
            ["Nethermind.Evm.TransactionProcessing.ITransactionProcessor"], ["none"]),
    ];

    private static readonly AssignmentTargetIdentity[] AssignmentTargetLedger =
    [
        new("context.reset.executionGas", TransactionProcessorType, "_blockCumulativeExecutionGas", SymbolKind.Field),
        new("context.reset.stateGas", TransactionProcessorType, "_blockCumulativeStateGas", SymbolKind.Field),
        new("context.balStore", BalManagerType, "_blockExecutionContext", SymbolKind.Field),
        new("tracer.blockReset.index", ReceiptTracerType, "_currentIndex", SymbolKind.Property),
        new("tracer.blockReset.currentTx", ReceiptTracerType, "CurrentTx", SymbolKind.Field),
        new("tracer.blockReset.tracer", ReceiptTracerType, "_currentTxTracer", SymbolKind.Field),
        new("tracer.blockReset.receiptGas", ReceiptTracerType, "_cumulativeReceiptGas", SymbolKind.Field),
        new("tracer.receiptIndex", "Nethermind.Core.TxReceipt", "Index", SymbolKind.Property),
        new("tracer.endTx.indexIncrement", ReceiptTracerType, "_currentIndex", SymbolKind.Property),
        new("tracer.txStart.currentTx", ReceiptTracerType, "CurrentTx", SymbolKind.Field),
        new("tracer.txStart.tracer", ReceiptTracerType, "_currentTxTracer", SymbolKind.Field),
        new("bal.enabledSpec", BalManagerType, "_blockAccessListsEnabled", SymbolKind.Field),
        new("bal.enabledDerivation", BalManagerType, "Enabled", SymbolKind.Property),
    ];

    private static readonly MemberTargetIdentity[] MemberTargetLedger =
    [
        new("bal.enabledMember", "Nethermind.Consensus.Processing.IBlockAccessListManager", "Enabled", SymbolKind.Property,
            "Nethermind.Consensus.Processing.IBlockAccessListManager"),
    ];

    private static readonly DependencySpec[] DependencySpecs =
    [
        new("tools/Evm/Lean/OrdinaryStaticAdmissionExtractor/Generated/OrdinaryStaticAdmissionKernel.ir.json", "settled static-admission identity"),
        new("tools/Evm/Lean/OrdinaryStaticAdmissionExtractor/Generated/OrdinaryStaticAdmissionKernel.lean", "settled static-admission generated kernel identity"),
        new("tools/Evm/Lean/OrdinaryStaticAdmissionExtractor/Generated/OrdinaryStaticAdmissionKernel.source-manifest.json", "settled static-admission manifest identity"),
        new("tools/Evm/Lean/OrdinaryStatefulAdmissionPrefixExtractor/Generated/OrdinaryStatefulAdmissionPrefix.ir.json", "settled stateful-admission identity"),
        new("tools/Evm/Lean/OrdinaryStatefulAdmissionPrefixExtractor/Generated/OrdinaryStatefulAdmissionPrefix.lean", "settled stateful-admission generated kernel identity"),
        new("tools/Evm/Lean/OrdinaryStatefulAdmissionPrefixExtractor/Generated/OrdinaryStatefulAdmissionPrefix.source-manifest.json", "settled stateful-admission manifest identity"),
        new("tools/Evm/Lean/OrdinaryPostNonceDispatchExtractor/Generated/OrdinaryPostNonceDispatch.ir.json", "settled post-nonce handoff identity"),
        new("tools/Evm/Lean/OrdinaryPostNonceDispatchExtractor/Generated/OrdinaryPostNonceDispatch.lean", "settled post-nonce generated kernel identity"),
        new("tools/Evm/Lean/OrdinaryPostNonceDispatchExtractor/Generated/OrdinaryPostNonceDispatch.source-manifest.json", "settled post-nonce manifest identity"),
        new("tools/Evm/Lean/SimpleTransferCompletionExtractor/Generated/SimpleTransferCompletion.ir.json", "settled simple-transfer identity"),
        new("tools/Evm/Lean/SimpleTransferCompletionExtractor/Generated/SimpleTransferCompletion.lean", "settled simple-transfer generated kernel identity"),
        new("tools/Evm/Lean/SimpleTransferCompletionExtractor/Generated/SimpleTransferCompletion.source-manifest.json", "settled simple-transfer manifest identity"),
        new("tools/Evm/Lean/ReceiptTerminalFoldExtractor/Generated/ReceiptTerminalFoldKernel.ir.json", "settled receipt-terminal identity"),
        new("tools/Evm/Lean/ReceiptTerminalFoldExtractor/Generated/ReceiptTerminalFoldKernel.lean", "settled receipt-terminal generated kernel identity"),
        new("tools/Evm/Lean/ReceiptTerminalFoldExtractor/Generated/ReceiptTerminalFoldKernel.source-manifest.json", "settled receipt-terminal manifest identity"),
        new(ReceiptSourcePinsPath, "settled receipt-terminal source pins"),
        new(ReceiptCompilerInventoryPath, "settled compiler/reference inventory"),
    ];

    private static readonly string[] FieldwiseSeamIds =
    [
        "tx.sender", "tx.recipient", "tx.value", "tx.nonce", "tx.type", "tx.gasLimit", "tx.fees",
        "route.codeOverridable", "route.authorizationList", "route.forceSimpleTransferDisabled",
        "header.gasUsed", "gas.spent", "gas.operation", "gas.block", "gas.state", "gas.maxUsed",
        "gas.refund", "substate.output", "substate.logs", "substate.error", "substate.exception",
        "receipt.status", "receipt.gas", "receipt.index", "receipt.recipient", "receipt.logs",
        "tracer.nestedForward", "tracer.currentReceiptForward",
        "result.constructor", "result.exception", "result.substateError",
    ];

    private static readonly CSharpParseOptions ParseOptions = CSharpParseOptions.Default
        .WithLanguageVersion(LanguageVersion.CSharp14)
        .WithDocumentationMode(DocumentationMode.Parse)
        .WithKind(SourceCodeKind.Regular);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    internal static ExtractionResult Extract(string repoRoot, string outputDirectory, string? leanOutputPath = null) =>
        ExtractCore(Path.GetFullPath(repoRoot), Path.GetFullPath(outputDirectory), leanOutputPath, requirePinnedSources: true, writeArtifacts: true);

    /// <summary>Test-only source rebinding that still requires typed Roslyn and CFG evidence.</summary>
    internal static ExtractionResult ExtractWithoutReviewedAdmissionForTest(
        string repoRoot,
        string outputDirectory,
        string? leanOutputPath = null,
        string? referenceRoot = null) =>
        ExtractCore(Path.GetFullPath(repoRoot), Path.GetFullPath(outputDirectory), leanOutputPath,
            requirePinnedSources: false, writeArtifacts: true, referenceRoot: referenceRoot,
            sourceOverrides: null, validateReceiptTerminalClosure: false);

    /// <summary>Test-only extraction with compile-valid source replacements.</summary>
    /// <remarks>
    /// This hook relaxes only ordinary source-byte pins and the known stale settled source-manifest
    /// pair so a mutation reaches the local Roslyn/CFG admission code. Settled ordinary
    /// dependencies, compiler/reference bytes, receipt artifacts, and all other closure checks
    /// remain active. Production extraction and
    /// checked-artifact validation always retain both gates. The replacement map is restricted to
    /// the declared source closure and no output is published until the complete test extraction
    /// succeeds.
    /// </remarks>
    internal static ExtractionResult ExtractForTest(
        string repoRoot,
        string outputDirectory,
        IReadOnlyDictionary<string, byte[]> sourceOverrides,
        string? leanOutputPath = null,
        string? referenceRoot = null) =>
        ExtractCore(Path.GetFullPath(repoRoot), Path.GetFullPath(outputDirectory), leanOutputPath,
            requirePinnedSources: false, writeArtifacts: true, referenceRoot: referenceRoot,
            sourceOverrides: sourceOverrides, validateReceiptTerminalClosure: false);

    internal static void ValidateExistingArtifacts(string repoRoot, string outputDirectory, string? leanOutputPath = null)
    {
        string root = Path.GetFullPath(repoRoot);
        string output = Path.GetFullPath(outputDirectory);
        string lean = Path.GetFullPath(leanOutputPath ?? Path.Combine(root, DefaultLeanPath));
        _ = ReadPins(root);
        SourceFile[] sources = ReadSources(root, requirePinnedSources: true);
        _ = ReadDependencies(root, requirePinnedSources: true);
        ReceiptTerminalSourceClosureIdentity receiptTerminalSourceClosure =
            ReadReceiptTerminalSourceClosure(root);
        CompilerClosure closure = BuildCompilerClosure(root);
        ValidateCompiledSources(sources, closure);

        string irPath = Path.Combine(output, IrFileName);
        string manifestPath = Path.Combine(output, ManifestFileName);
        if (!File.Exists(irPath) || !File.Exists(manifestPath) || !File.Exists(lean))
        {
            throw new ExtractionException("Generated machine artifacts are missing; run --extract in the serialized build lane.");
        }

        IrDocument ir = Deserialize<IrDocument>(File.ReadAllBytes(irPath), "ordinary machine IR");
        Manifest manifest = Deserialize<Manifest>(File.ReadAllBytes(manifestPath), "ordinary machine manifest");
        if (ir.AcceptanceState == "static-draft" || manifest.AcceptanceState == "static-draft")
        {
            throw new ExtractionException("Static ordinary-machine artifacts require --extract re-emission in the serialized build lane.");
        }

        ValidateIr(ir);
        ValidateReceiptTerminalSourceClosure(ir.Machine.ReceiptTerminalSourceClosure);
        if (!ReceiptTerminalSourceClosureMatches(ir.Machine.ReceiptTerminalSourceClosure, receiptTerminalSourceClosure))
        {
            throw new ExtractionException("The machine IR receipt-terminal source closure does not match the settled closure.");
        }
        ValidateManifest(manifest);
        ValidateReceiptTerminalSourceClosure(manifest.ReceiptTerminalSourceClosure);
        if (!ReceiptTerminalSourceClosureMatches(manifest.ReceiptTerminalSourceClosure, receiptTerminalSourceClosure))
        {
            throw new ExtractionException("The machine manifest receipt-terminal source closure does not match the settled closure.");
        }
        if (!string.Equals(manifest.SemanticIrSha256, manifest.Ir.Sha256, StringComparison.Ordinal))
        {
            throw new ExtractionException("The machine manifest semantic IR hash does not match the IR artifact.");
        }
        string irHash = Sha256(File.ReadAllBytes(irPath));
        if (!string.Equals(irHash, manifest.Ir.Sha256, StringComparison.Ordinal))
        {
            throw new ExtractionException("The machine manifest does not match the checked-in IR bytes.");
        }

        string leanHash = Sha256(File.ReadAllBytes(lean));
        if (!string.Equals(leanHash, manifest.Lean.Sha256, StringComparison.Ordinal))
        {
            throw new ExtractionException("The machine manifest does not match the checked-in Lean bytes.");
        }

        ValidateSourceIdentities(root, manifest.Sources);
        ValidateDependencyIdentities(root, manifest.Dependencies);
        ValidateCompilerIdentities(root, manifest.CompilerReferences);
        // Validate the serialized IR closure independently as well. Comparing only the manifest
        // would let a mutated IR MVID, dependency list, or source identity hide behind an
        // unchanged manifest and its aggregate hash.
        ValidateSourceIdentities(root, ir.Sources);
        ValidateDependencyIdentities(root, ir.Dependencies);
        ValidateCompilerIdentities(root, ir.CompilerReferences);
    }

    internal static CompilerReferenceIdentity[] LoadCompilerReferencesForTest(string repoRoot) =>
        BuildCompilerClosure(Path.GetFullPath(repoRoot)).Identities;

    internal static void ValidateCompilerReferencesForTest(CompilerReferenceIdentity[] references) =>
        ValidateCompilerIdentities(references);

    internal static void ValidateCompilerReferencesForTest(string repoRoot, CompilerReferenceIdentity[] references) =>
        ValidateCompilerIdentities(Path.GetFullPath(repoRoot), references);

    private static ExtractionResult ExtractCore(
        string root,
        string outputDirectory,
        string? leanOutputPath,
        bool requirePinnedSources,
        bool writeArtifacts,
        string? referenceRoot = null,
        IReadOnlyDictionary<string, byte[]>? sourceOverrides = null,
        bool validateReceiptTerminalClosure = true)
    {
        ReadPins(root);
        SourceFile[] sources = ReadSources(root, requirePinnedSources, sourceOverrides);
        // Source overrides are test-only; settled ordinary dependencies remain byte-pinned even
        // when a mutation is being routed past the source-pin gate.
        DependencyIdentity[] dependencies = ReadDependencies(root, requirePinnedSources: true,
            validateReceiptTerminalClosure: validateReceiptTerminalClosure);
        CompilerClosure closure = BuildCompilerClosure(Path.GetFullPath(referenceRoot ?? root));
        ValidateCompiledSources(sources, closure);

        Dictionary<string, SemanticUnit> units = BuildSemanticUnits(sources, closure);
        ReceiptTerminalSourceClosureIdentity receiptTerminalSourceClosure =
            ReadReceiptTerminalSourceClosure(root, validateReceiptTerminalClosure);
        List<TypedBinding> bindings = [];
        MachineShape machine = BindMachine(root, units, bindings, receiptTerminalSourceClosure);
        SourceIdentity[] sourceIdentities = sources
            .Select(static source => new SourceIdentity(source.RelativePath, source.Role, source.Sha256, source.SyntaxSha256))
            .ToArray();
        string sourceClosure = CombinedSourceHash(sourceIdentities);
        IrDocument document = new(
            SchemaVersion,
            ExtractorVersion,
            Kernel,
            AcceptanceState,
            sourceClosure,
            closure.AggregateSha256,
            sourceIdentities,
            dependencies,
            closure.Identities,
            machine.Options,
            machine.Route,
            machine,
            [
                "ExecutionOptions is the source signed-Int32 flag projection; the admitted target is raw Commit = 1.",
                "EthereumGasPolicy gas and GasConsumed fields are retained as typed observations; no CLR arithmetic is silently replaced by Nat arithmetic.",
                "ReceiptTerminalFold's fixed-width accounting and receipt append are consumed only through its pinned generated kernel identity.",
            ],
            [
                "Roslyn binds source symbols, IOperation types, data-flow sets, CFG reachability, and route order. It does not execute C# or prove CLR/JIT behavior.",
                "Effectful state, gas-policy, settlement, fee, and tracer calls are normal-return adapter observations. Their implementations are not inferred from an oracle value.",
                "The EVM frame branch is deliberately excluded; a simple-transfer handoff is admitted only under the exact live-recipient/no-code/no-delegation, non-overridable, null-authorization-list and force-enabled guard.",
                "The direct-inner caller is standard only under explicit BAL-disabled and sequential premises; only the decorator context forwarding and direct-inner guard are composed, not its BAL-enabled implementation.",
            ],
            [
                "Source-to-generated fieldwise handoffs for transaction, header, gas, substate, receipt, and result projections.",
                "Construction of the adapter-supplied settled list from Block.Transactions and production execution remains an external composition obligation.",
                "Normal return from SetBlockExecutionContext, Process, adapter Start/Execute/End, ExecuteSimpleTransfer, CommitState, and ReceiptTerminal callbacks.",
                "Amsterdam chainspec ReleaseSpec equivalence and UInt256/ulong arithmetic remain external proof obligations.",
                "The settled ReceiptTerminal source manifest must be re-emitted whenever its source pins change; a stale pin/manifest pair remains rejected.",
            ]);
        ValidateIr(document);
        byte[] irBytes = Serialize(document);
        string irSha = Sha256(irBytes);
        byte[] leanBytes = LeanEmitter.Emit(document, irSha);
        string leanPath = Path.GetFullPath(leanOutputPath ?? Path.Combine(root, DefaultLeanPath));
        EnsureWithin(leanOutputPath is null ? root : outputDirectory, leanPath);
        string irPath = Path.Combine(outputDirectory, IrFileName);
        string manifestPath = Path.Combine(outputDirectory, ManifestFileName);
        EnsureWithin(outputDirectory, irPath);
        EnsureWithin(outputDirectory, manifestPath);

        Manifest manifest = new(
            SchemaVersion,
            ExtractorVersion,
            typeof(CSharpCompilation).Assembly.GetName().Version?.ToString() ?? "unknown",
            LanguageVersion.CSharp14.ToDisplayString(),
            Kernel,
            AcceptanceState,
            sourceClosure,
            closure.AggregateSha256,
            sourceIdentities,
            dependencies,
            closure.Identities,
            receiptTerminalSourceClosure,
            bindings.ToArray(),
            machine.ControlFlows,
            new ArtifactIdentity(Normalize(Path.GetRelativePath(outputDirectory, irPath)), irSha),
            new ArtifactIdentity(Normalize(DefaultLeanPath), Sha256(leanBytes)),
            CombinedSourceHash(sourceIdentities),
            irSha,
            document.OpenObligations);
        ValidateManifest(manifest);
        byte[] manifestBytes = Serialize(manifest);

        if (writeArtifacts)
        {
            AtomicWrite(irPath, irBytes);
            AtomicWrite(leanPath, leanBytes);
            AtomicWrite(manifestPath, manifestBytes);
        }

        return new ExtractionResult(
            irPath,
            manifestPath,
            leanPath,
            sources.Length,
            bindings.Count,
            machine.ControlFlows.Length,
            irSha,
            Sha256(manifestBytes),
            Sha256(leanBytes));
    }

    private static PinsDocument ReadPins(string root)
    {
        string path = Path.GetFullPath(Path.Combine(root, MachinePinsPath));
        EnsureWithin(root, path);
        if (!File.Exists(path)) throw new ExtractionException($"Missing machine source pins: {MachinePinsPath}.");
        byte[] bytes = File.ReadAllBytes(path);
        if (!string.Equals(Sha256(bytes), MachinePinsSha256, StringComparison.Ordinal))
        {
            throw new ExtractionException($"Machine source pins changed; expected {MachinePinsSha256}.");
        }

        PinsDocument pins = Deserialize<PinsDocument>(bytes, "machine source pins");
        if (pins.SchemaVersion != SchemaVersion || pins.Status != "ordinary-machine-static-draft" ||
            string.IsNullOrWhiteSpace(pins.Authority) || pins.Sources is null || pins.Dependencies is null ||
            pins.CompilerReferenceInventory is null || pins.Sources.Length != SourceSpecs.Length ||
            pins.Dependencies.Length != DependencySpecs.Length || pins.Sources.Any(static source => source is null) ||
            pins.Dependencies.Any(static dependency => dependency is null))
        {
            throw new ExtractionException("The ordinary machine source-pin schema or path count changed.");
        }

        string[] sourcePaths = pins.Sources.Select(static source => source.Path).ToArray();
        string[] expectedSourcePaths = SourceSpecs.Select(static source => source.Path).ToArray();
        if (!sourcePaths.SequenceEqual(expectedSourcePaths, StringComparer.Ordinal))
        {
            throw new ExtractionException("The ordinary machine source-pin path/order set changed.");
        }

        string[] dependencyPaths = pins.Dependencies.Select(static dependency => dependency.Path).ToArray();
        string[] expectedDependencyPaths = DependencySpecs.Select(static dependency => dependency.Path).ToArray();
        if (!dependencyPaths.SequenceEqual(expectedDependencyPaths, StringComparer.Ordinal))
        {
            throw new ExtractionException("The ordinary machine dependency path/order set changed.");
        }

        if (!string.Equals(Normalize(pins.CompilerReferenceInventory.Path), ReceiptCompilerInventoryPath, StringComparison.Ordinal) ||
            !string.Equals(pins.CompilerReferenceInventory.Sha256, ReceiptCompilerInventorySha256, StringComparison.Ordinal) ||
            pins.CompilerReferenceInventory.Count != ReceiptCompilerInventoryCount ||
            !string.Equals(pins.CompilerReferenceInventory.AggregateSha256, ReceiptCompilerInventoryAggregateSha256, StringComparison.Ordinal))
        {
            throw new ExtractionException("The authoritative compiler/reference closure identity changed.");
        }

        return pins;
    }

    private static SourceFile[] ReadSources(
        string root,
        bool requirePinnedSources,
        IReadOnlyDictionary<string, byte[]>? sourceOverrides = null)
    {
        PinsDocument pins = ReadPins(root);
        if (sourceOverrides is not null && sourceOverrides.Keys.Any(path =>
                !SourceSpecs.Any(spec => spec.Path == path)))
        {
            throw new ExtractionException("A source override is outside the ordinary machine source closure.");
        }

        SourceFile[] result = new SourceFile[SourceSpecs.Length];
        for (int index = 0; index < SourceSpecs.Length; index++)
        {
            SourceSpec expected = SourceSpecs[index];
            PinnedSource pin = pins.Sources[index];
            if (!string.Equals(pin.Role, expected.Role, StringComparison.Ordinal) || !IsSha256(pin.Sha256))
            {
                throw new ExtractionException($"Source pin metadata changed: {expected.Path}.");
            }

            string path = Path.GetFullPath(Path.Combine(root, expected.Path));
            EnsureWithin(root, path);
            if (!File.Exists(path)) throw new ExtractionException($"Missing source closure member: {expected.Path}.");
            byte[] bytes;
            if (sourceOverrides is not null && sourceOverrides.TryGetValue(expected.Path, out byte[]? overrideBytes))
            {
                bytes = overrideBytes ?? throw new ExtractionException(
                    $"The source override for '{expected.Path}' is null.");
            }
            else
            {
                bytes = File.ReadAllBytes(path);
            }
            string sha = Sha256(bytes);
            if (requirePinnedSources && !string.Equals(sha, pin.Sha256, StringComparison.Ordinal))
            {
                throw new ExtractionException($"Pinned source changed: {expected.Path}; expected {pin.Sha256}, got {sha}.");
            }

            SourceText text = SourceText.From(bytes, Encoding.UTF8, canBeEmbedded: false, checksumAlgorithm: SourceHashAlgorithm.Sha256);
            SyntaxTree tree = CSharpSyntaxTree.ParseText(text, ParseOptions, expected.Path);
            CompilationUnitSyntax syntaxRoot = tree.GetCompilationUnitRoot();
            if (syntaxRoot.ContainsDiagnostics && syntaxRoot.GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
            {
                throw new ExtractionException($"The source closure has a parse error: {expected.Path}.");
            }

            result[index] = new SourceFile(
                expected.Path,
                expected.Role,
                path,
                bytes,
                sha,
                Sha256(Encoding.UTF8.GetBytes(CanonicalTokens(syntaxRoot))),
                tree,
                syntaxRoot);
        }

        return result;
    }

    private static DependencyIdentity[] ReadDependencies(
        string root,
        bool requirePinnedSources,
        bool validateReceiptTerminalClosure = true)
    {
        PinsDocument pins = ReadPins(root);
        DependencyIdentity[] identities = new DependencyIdentity[DependencySpecs.Length];
        for (int index = 0; index < DependencySpecs.Length; index++)
        {
            DependencySpec expected = DependencySpecs[index];
            PinnedDependency pin = pins.Dependencies[index];
            if (pin.Path != expected.Path || pin.Role != expected.Role || !IsSha256(pin.Sha256))
            {
                throw new ExtractionException($"Dependency pin metadata changed: {expected.Path}.");
            }
            string path = Path.GetFullPath(Path.Combine(root, expected.Path));
            EnsureWithin(root, path);
            if (!File.Exists(path)) throw new ExtractionException($"Missing settled dependency: {expected.Path}.");
            string actual = Sha256(File.ReadAllBytes(path));
            if (requirePinnedSources && !string.Equals(actual, pin.Sha256, StringComparison.Ordinal))
            {
                throw new ExtractionException($"Settled dependency changed: {expected.Path}; expected {pin.Sha256}, got {actual}.");
            }

            identities[index] = new DependencyIdentity(expected.Path, expected.Role, actual);
        }

        if (validateReceiptTerminalClosure)
        {
            ValidateReceiptTerminalSourcePins(root);
        }
        return identities;
    }

    private static ReceiptTerminalSourceClosureIdentity ReadReceiptTerminalSourceClosure(
        string root,
        bool requireSettledManifestMatch = true)
    {
        byte[] sourcePinsBytes = ReadPinnedReceiptBytes(root, ReceiptSourcePinsPath, ReceiptSourcePinsSha256);
        try
        {
            using JsonDocument sourcePinsDocument = ParseReceiptJson(sourcePinsBytes, ReceiptSourcePinsPath);
            JsonElement sourcePins = sourcePinsDocument.RootElement;
            if (!sourcePins.TryGetProperty("schemaVersion", out JsonElement schema) ||
                schema.ValueKind != JsonValueKind.Number || schema.GetInt32() != 2 ||
                !sourcePins.TryGetProperty("root", out JsonElement sourceRoot) ||
                sourceRoot.ValueKind != JsonValueKind.String ||
                sourceRoot.GetString() !=
                    "Nethermind.Blockchain.Tracing.BlockReceiptsTracer (exact base; no BuildReceipt override)")
            {
                throw new ExtractionException("The settled receipt-terminal source-pin root changed.");
            }

            SourcePin[] sources = ReadReceiptSourcePins(sourcePins, "sources");
            SourcePin[] bindingSources = ReadReceiptSourcePins(sourcePins, "bindingSources");
            ValidateReceiptSourcePinPaths(sources, bindingSources);
            ValidateReceiptSourceFiles(root, sources);
            ValidateReceiptSourceFiles(root, bindingSources);
            SourcePin tracerPin = sources.SingleOrDefault(pin => pin.Path == ReceiptTracerSourcePath)
                ?? throw new ExtractionException("The settled receipt-terminal source pins lack the exact base tracer.");
            if (!string.Equals(tracerPin.Sha256, ReceiptTracerSourceSha256, StringComparison.Ordinal))
            {
                throw new ExtractionException("The settled receipt-terminal source pins no longer bind the exact base tracer source.");
            }

            byte[] manifestBytes = ReadPinnedReceiptBytes(root, ReceiptSourceManifestPath, ReceiptSourceManifestSha256);
            using JsonDocument manifestDocument = ParseReceiptJson(manifestBytes, ReceiptSourceManifestPath);
            JsonElement manifest = manifestDocument.RootElement;
            ValidateReceiptManifestHeader(manifest);
            SourcePin[] manifestSources = ReadReceiptManifestPins(manifest, "sources");
            SourcePin[] manifestBindingSources = ReadReceiptManifestPins(manifest, "bindingSources");
            if (requireSettledManifestMatch &&
                (!ReceiptPinsMatch(sources, manifestSources) ||
                    !ReceiptPinsMatch(bindingSources, manifestBindingSources)))
            {
                throw new ExtractionException("The settled receipt source manifest does not match its pinned source lists.");
            }

            ValidateReceiptManifestCompilerReferences(root, manifest);
            ValidateReceiptManifestArtifacts(root, manifest);
            return new(
                ReceiptSourcePinsPath,
                ReceiptSourcePinsSha256,
                ReceiptSourceManifestPath,
                ReceiptSourceManifestSha256,
                ReceiptCompilerInventoryPath,
                ReceiptCompilerInventorySha256,
                sources,
                bindingSources);
        }
        catch (ExtractionException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new ExtractionException($"The settled receipt-terminal source pins are malformed: {exception.Message}");
        }
    }

    private static void ValidateReceiptTerminalSourcePins(string root) =>
        _ = ReadReceiptTerminalSourceClosure(root);

    private static byte[] ReadPinnedReceiptBytes(string root, string relativePath, string expectedSha256)
    {
        string path = Path.GetFullPath(Path.Combine(root, relativePath));
        EnsureWithin(root, path);
        if (!File.Exists(path))
        {
            throw new ExtractionException($"The transitive receipt source closure is missing '{relativePath}'.");
        }

        byte[] bytes = File.ReadAllBytes(path);
        if (!string.Equals(Sha256(bytes), expectedSha256, StringComparison.Ordinal))
        {
            throw new ExtractionException(
                $"The transitive receipt source closure bytes changed for '{relativePath}': expected {expectedSha256}.");
        }

        return bytes;
    }

    private static JsonDocument ParseReceiptJson(byte[] bytes, string relativePath)
    {
        try
        {
            return JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            });
        }
        catch (JsonException exception)
        {
            throw new ExtractionException(
                $"The transitive receipt source artifact '{relativePath}' is invalid: {exception.Message}");
        }
    }

    private static SourcePin[] ReadReceiptSourcePins(JsonElement document, string propertyName)
    {
        if (!document.TryGetProperty(propertyName, out JsonElement values) ||
            values.ValueKind != JsonValueKind.Array)
        {
            throw new ExtractionException($"The settled receipt source pin document lacks '{propertyName}'.");
        }

        List<SourcePin> pins = [];
        foreach (JsonElement value in values.EnumerateArray())
        {
            if (!value.TryGetProperty("path", out JsonElement path) ||
                !value.TryGetProperty("role", out JsonElement role) ||
                !value.TryGetProperty("sha256", out JsonElement sha256) ||
                path.ValueKind != JsonValueKind.String || role.ValueKind != JsonValueKind.String ||
                sha256.ValueKind != JsonValueKind.String)
            {
                throw new ExtractionException($"The settled receipt source pin entry in '{propertyName}' is malformed.");
            }

            pins.Add(new(path.GetString()!, role.GetString()!, sha256.GetString()!));
        }

        if (pins.Count == 0 || pins.Any(static pin => string.IsNullOrWhiteSpace(pin.Path) ||
                string.IsNullOrWhiteSpace(pin.Role) || !IsSha256(pin.Sha256)))
        {
            throw new ExtractionException($"The settled receipt source pin list '{propertyName}' is empty or malformed.");
        }

        return pins.ToArray();
    }

    private static SourcePin[] ReadReceiptManifestPins(JsonElement document, string propertyName)
    {
        if (!document.TryGetProperty(propertyName, out JsonElement values) ||
            values.ValueKind != JsonValueKind.Array)
        {
            throw new ExtractionException($"The settled receipt source manifest lacks '{propertyName}'.");
        }

        List<SourcePin> pins = [];
        foreach (JsonElement value in values.EnumerateArray())
        {
            if (!value.TryGetProperty("path", out JsonElement path) ||
                !value.TryGetProperty("sha256", out JsonElement sha256) ||
                path.ValueKind != JsonValueKind.String || sha256.ValueKind != JsonValueKind.String)
            {
                throw new ExtractionException($"The settled receipt source manifest entry in '{propertyName}' is malformed.");
            }

            pins.Add(new(path.GetString()!, string.Empty, sha256.GetString()!));
        }

        if (pins.Count == 0 || pins.Any(static pin => string.IsNullOrWhiteSpace(pin.Path) || !IsSha256(pin.Sha256)))
        {
            throw new ExtractionException($"The settled receipt source manifest list '{propertyName}' is empty or malformed.");
        }

        return pins.ToArray();
    }

    private static void ValidateReceiptSourcePinPaths(params SourcePin[][] groups)
    {
        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        foreach (SourcePin[] group in groups)
        {
            foreach (SourcePin pin in group)
            {
                string normalized = Normalize(pin.Path);
                if (pin.Path != normalized || normalized.Contains("../", StringComparison.Ordinal) ||
                    normalized.Contains("/..", StringComparison.Ordinal) ||
                    Path.IsPathRooted(pin.Path) || !paths.Add(pin.Path))
                {
                    throw new ExtractionException($"The settled receipt source pin path is non-canonical or ambiguous: {pin.Path}.");
                }
            }
        }
    }

    private static void ValidateReceiptSourceFiles(string root, IEnumerable<SourcePin> pins)
    {
        foreach (SourcePin pin in pins)
        {
            string path = Path.GetFullPath(Path.Combine(root, pin.Path));
            EnsureWithin(root, path);
            if (!File.Exists(path))
            {
                throw new ExtractionException($"The transitive receipt source closure member is missing: {pin.Path}.");
            }

            string actual = Sha256(File.ReadAllBytes(path));
            if (!string.Equals(actual, pin.Sha256, StringComparison.Ordinal))
            {
                throw new ExtractionException(
                    $"The transitive receipt source closure member changed for '{pin.Path}': expected {pin.Sha256}, got {actual}.");
            }
        }
    }

    private static void ValidateReceiptManifestHeader(JsonElement manifest)
    {
        if (!manifest.TryGetProperty("schemaVersion", out JsonElement schema) ||
            schema.ValueKind != JsonValueKind.Number || schema.GetInt32() != ReceiptSourceManifestSchemaVersion ||
            !manifest.TryGetProperty("extractorVersion", out JsonElement extractorVersion) ||
            extractorVersion.GetString() != ReceiptSourceManifestExtractorVersion ||
            !manifest.TryGetProperty("compilerVersion", out JsonElement compilerVersion) ||
            compilerVersion.GetString() != ReceiptSourceManifestCompilerVersion ||
            !manifest.TryGetProperty("languageVersion", out JsonElement languageVersion) ||
            languageVersion.GetString() != ReceiptSourceManifestLanguageVersion ||
            !manifest.TryGetProperty("kernel", out JsonElement kernel) ||
            kernel.GetString() != ReceiptSourceManifestKernel)
        {
            throw new ExtractionException("The settled receipt source manifest header changed.");
        }
    }

    private static void ValidateReceiptManifestCompilerReferences(string root, JsonElement manifest)
    {
        byte[] inventoryBytes = ReadPinnedReceiptBytes(root, ReceiptCompilerInventoryPath, ReceiptCompilerInventorySha256);
        using JsonDocument inventoryDocument = ParseReceiptJson(inventoryBytes, ReceiptCompilerInventoryPath);
        JsonElement inventory = inventoryDocument.RootElement;
        if (!inventory.TryGetProperty("schemaVersion", out JsonElement schema) ||
            schema.ValueKind != JsonValueKind.Number || schema.GetInt32() != 1 ||
            !inventory.TryGetProperty("count", out JsonElement count) ||
            count.ValueKind != JsonValueKind.Number || count.GetInt32() != ReceiptCompilerInventoryCount ||
            !inventory.TryGetProperty("aggregateSha256", out JsonElement aggregate) ||
            aggregate.GetString() != ReceiptCompilerInventoryAggregateSha256 ||
            !inventory.TryGetProperty("references", out JsonElement inventoryReferences) ||
            inventoryReferences.ValueKind != JsonValueKind.Array ||
            inventoryReferences.GetArrayLength() != ReceiptCompilerInventoryCount)
        {
            throw new ExtractionException("The settled receipt compiler inventory header changed.");
        }

        if (!manifest.TryGetProperty("compilerReferences", out JsonElement manifestReferences) ||
            manifestReferences.ValueKind != JsonValueKind.Array ||
            manifestReferences.GetArrayLength() != ReceiptCompilerInventoryCount)
        {
            throw new ExtractionException("The settled receipt source manifest compiler closure is incomplete.");
        }

        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> assemblies = new(StringComparer.OrdinalIgnoreCase);
        JsonElement[] expected = inventoryReferences.EnumerateArray().ToArray();
        JsonElement[] actual = manifestReferences.EnumerateArray().ToArray();
        for (int index = 0; index < expected.Length; index++)
        {
            JsonElement expectedReference = expected[index];
            JsonElement actualReference = actual[index];
            string expectedPath = RequiredString(expectedReference, "path", "compiler inventory");
            string expectedAssembly = RequiredString(expectedReference, "assemblyName", "compiler inventory");
            string expectedSha = RequiredString(expectedReference, "sha256", "compiler inventory");
            string actualPath = RequiredString(actualReference, "path", "receipt source manifest compiler closure");
            string actualAssembly = RequiredString(actualReference, "assemblyName", "receipt source manifest compiler closure");
            string actualSha = RequiredString(actualReference, "sha256", "receipt source manifest compiler closure");
            if (actualPath != expectedPath || actualAssembly != expectedAssembly || actualSha != expectedSha ||
                !IsSha256(actualSha) || !paths.Add(actualPath) || !assemblies.Add(actualAssembly))
            {
                throw new ExtractionException("The settled receipt source manifest compiler closure changed or is ambiguous.");
            }
        }
    }

    private static void ValidateReceiptManifestArtifacts(string root, JsonElement manifest)
    {
        ValidateReceiptArtifact(root, ReceiptIrPath, ReceiptIrSha256, "receipt terminal IR");
        ValidateReceiptArtifact(root, ReceiptSourceManifestPath, ReceiptSourceManifestSha256, "receipt terminal source manifest");
        ValidateReceiptArtifact(root, ReceiptKernelPath, ReceiptKernelSha256, "receipt terminal generated Lean");
        ValidateReceiptArtifact(root, ReceiptRefinementPath, ReceiptRefinementSha256, "receipt terminal refinement");

        if (!manifest.TryGetProperty("ir", out JsonElement receiptIr) ||
            !ManifestArtifactMatches(receiptIr, "ReceiptTerminalFoldKernel.ir.json", ReceiptIrSha256) ||
            !manifest.TryGetProperty("lean", out JsonElement receiptLean) ||
            !ManifestArtifactMatches(receiptLean, ReceiptKernelPath, ReceiptKernelSha256))
        {
            throw new ExtractionException("The settled receipt source manifest terminal artifact identities changed.");
        }

        if (!manifest.TryGetProperty("accountingKernel", out JsonElement accountingKernel) ||
            accountingKernel.ValueKind != JsonValueKind.Object ||
            RequiredString(accountingKernel, "root", "accounting kernel") !=
                "Nethermind.Blockchain.Tracing.BlockReceiptGasAccountingKernel" ||
            RequiredString(accountingKernel, "sourcePath", "accounting kernel") != ReceiptAccountingSourcePath ||
            RequiredString(accountingKernel, "sourceSha256", "accounting kernel") != ReceiptAccountingSourceSha256 ||
            RequiredString(accountingKernel, "irPath", "accounting kernel") != ReceiptAccountingIrPath ||
            RequiredString(accountingKernel, "irSha256", "accounting kernel") != ReceiptAccountingIrSha256 ||
            RequiredString(accountingKernel, "generatedLeanPath", "accounting kernel") != ReceiptAccountingLeanPath ||
            RequiredString(accountingKernel, "generatedLeanSha256", "accounting kernel") != ReceiptAccountingKernelSha256 ||
            RequiredString(accountingKernel, "refinementPath", "accounting kernel") != ReceiptAccountingRefinementPath ||
            RequiredString(accountingKernel, "refinementSha256", "accounting kernel") != ReceiptAccountingRefinementSha256 ||
            RequiredString(accountingKernel, "manifestPath", "accounting kernel") != ReceiptAccountingManifestPath ||
            RequiredString(accountingKernel, "manifestSha256", "accounting kernel") != ReceiptAccountingManifestSha256)
        {
            throw new ExtractionException("The settled receipt accounting-kernel closure changed.");
        }

        ValidateReceiptArtifact(root, ReceiptAccountingSourcePath, ReceiptAccountingSourceSha256, "receipt accounting source");
        ValidateReceiptArtifact(root, ReceiptAccountingIrPath, ReceiptAccountingIrSha256, "receipt accounting IR");
        ValidateReceiptArtifact(root, ReceiptAccountingLeanPath, ReceiptAccountingKernelSha256, "receipt accounting generated Lean");
        ValidateReceiptArtifact(root, ReceiptAccountingRefinementPath, ReceiptAccountingRefinementSha256, "receipt accounting refinement");
        ValidateReceiptArtifact(root, ReceiptAccountingManifestPath, ReceiptAccountingManifestSha256, "receipt accounting source manifest");
    }

    private static void ValidateReceiptArtifact(string root, string relativePath, string expectedSha256, string description)
    {
        string path = Path.GetFullPath(Path.Combine(root, relativePath));
        EnsureWithin(root, path);
        if (!File.Exists(path))
        {
            throw new ExtractionException($"The settled {description} is missing: {relativePath}.");
        }

        string actual = Sha256(File.ReadAllBytes(path));
        if (!string.Equals(actual, expectedSha256, StringComparison.Ordinal))
        {
            throw new ExtractionException(
                $"The settled {description} changed: expected {expectedSha256}, got {actual}.");
        }
    }

    private static bool ManifestArtifactMatches(JsonElement artifact, string expectedPath, string expectedSha256) =>
        artifact.ValueKind == JsonValueKind.Object &&
        RequiredString(artifact, "path", "receipt artifact") == expectedPath &&
        RequiredString(artifact, "sha256", "receipt artifact") == expectedSha256;

    private static string RequiredString(JsonElement value, string propertyName, string description)
    {
        if (!value.TryGetProperty(propertyName, out JsonElement property) ||
            property.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new ExtractionException($"The {description} lacks a non-empty '{propertyName}'.");
        }

        return property.GetString()!;
    }

    private static bool ReceiptPinsMatch(SourcePin[] expected, SourcePin[] actual) =>
        expected.Length == actual.Length && expected.Zip(actual).All(static pair =>
            string.Equals(pair.First.Path, pair.Second.Path, StringComparison.Ordinal) &&
            string.Equals(pair.First.Sha256, pair.Second.Sha256, StringComparison.Ordinal));

    private static void ValidateReceiptTerminalSourceClosure(ReceiptTerminalSourceClosureIdentity closure)
    {
        if (closure is null || closure.SourcePinsPath != ReceiptSourcePinsPath ||
            closure.SourcePinsSha256 != ReceiptSourcePinsSha256 ||
            closure.SourceManifestPath != ReceiptSourceManifestPath ||
            closure.SourceManifestSha256 != ReceiptSourceManifestSha256 ||
            closure.CompilerReferenceInventoryPath != ReceiptCompilerInventoryPath ||
            closure.CompilerReferenceInventorySha256 != ReceiptCompilerInventorySha256 ||
            closure.Sources is null || closure.BindingSources is null ||
            closure.Sources.Length == 0 || closure.BindingSources.Length == 0)
        {
            throw new ExtractionException("The machine IR receipt-terminal source closure identity changed.");
        }

        if (closure.Sources.Any(static pin => pin is null || string.IsNullOrWhiteSpace(pin.Role) || !IsSha256(pin.Sha256)) ||
            closure.BindingSources.Any(static pin => pin is null || string.IsNullOrWhiteSpace(pin.Role) || !IsSha256(pin.Sha256)))
        {
            throw new ExtractionException("The machine IR receipt-terminal source closure is incomplete.");
        }

        ValidateReceiptSourcePinPaths(closure.Sources, closure.BindingSources);
    }

    private static bool ReceiptTerminalSourceClosureMatches(
        ReceiptTerminalSourceClosureIdentity actual,
        ReceiptTerminalSourceClosureIdentity expected) =>
        actual is not null && expected is not null &&
        actual.SourcePinsPath == expected.SourcePinsPath &&
        actual.SourcePinsSha256 == expected.SourcePinsSha256 &&
        actual.SourceManifestPath == expected.SourceManifestPath &&
        actual.SourceManifestSha256 == expected.SourceManifestSha256 &&
        actual.CompilerReferenceInventoryPath == expected.CompilerReferenceInventoryPath &&
        actual.CompilerReferenceInventorySha256 == expected.CompilerReferenceInventorySha256 &&
        ReceiptPinsMatch(expected.Sources, actual.Sources) &&
        ReceiptPinsMatch(expected.BindingSources, actual.BindingSources) &&
        actual.Sources.Zip(expected.Sources).All(static pair => pair.First.Role == pair.Second.Role) &&
        actual.BindingSources.Zip(expected.BindingSources).All(static pair => pair.First.Role == pair.Second.Role);

    private static CompilerClosure BuildCompilerClosure(string referenceRoot)
    {
        string root = Path.GetFullPath(referenceRoot);
        string inventoryPath = Path.GetFullPath(Path.Combine(root, ReceiptCompilerInventoryPath));
        EnsureWithin(root, inventoryPath);
        if (!File.Exists(inventoryPath))
        {
            throw new ExtractionException($"Missing authoritative compiler/reference inventory: {ReceiptCompilerInventoryPath}.");
        }

        byte[] bytes = File.ReadAllBytes(inventoryPath);
        string inventorySha = Sha256(bytes);
        if (!string.Equals(inventorySha, ReceiptCompilerInventorySha256, StringComparison.Ordinal))
        {
            throw new ExtractionException($"Compiler/reference inventory changed; expected {ReceiptCompilerInventorySha256}, got {inventorySha}.");
        }

        CompilerReferenceInventory inventory = Deserialize<CompilerReferenceInventory>(bytes, "compiler/reference inventory");
        if (inventory.SchemaVersion != 1 || inventory.Count != ReceiptCompilerInventoryCount ||
            inventory.References is null || inventory.References.Length != inventory.Count ||
            !string.Equals(inventory.AggregateSha256, ReceiptCompilerInventoryAggregateSha256, StringComparison.Ordinal))
        {
            throw new ExtractionException("The compiler/reference inventory header or count changed.");
        }

        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> assemblyNames = new(StringComparer.OrdinalIgnoreCase);
        List<MetadataReference> references = new(inventory.References.Length);
        List<CompilerReferenceIdentity> identities = new(inventory.References.Length);
        foreach (CompilerReferencePin pin in inventory.References)
        {
            if (pin is null || string.IsNullOrWhiteSpace(pin.Path) || !paths.Add(pin.Path) ||
                string.IsNullOrWhiteSpace(pin.AssemblyName) || !assemblyNames.Add(pin.AssemblyName) ||
                !IsSha256(pin.Sha256) || !string.Equals(pin.AssemblyName, Path.GetFileNameWithoutExtension(pin.Path), StringComparison.OrdinalIgnoreCase))
            {
                throw new ExtractionException("The compiler/reference inventory contains a duplicate or malformed identity.");
            }

            string normalized = Normalize(pin.Path);
            if (!string.Equals(pin.Path, normalized, StringComparison.Ordinal) ||
                normalized.Contains("../", StringComparison.Ordinal) || normalized.Contains("/..", StringComparison.Ordinal) ||
                !normalized.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                throw new ExtractionException($"The compiler/reference path is not canonical: {pin.Path}.");
            }

            string fullPath = ResolveReferencePath(root, normalized);
            if (!File.Exists(fullPath)) throw new ExtractionException($"Missing compiler/reference binary: {pin.Path}.");
            string actual = Sha256(File.ReadAllBytes(fullPath));
            if (!string.Equals(actual, pin.Sha256, StringComparison.Ordinal))
            {
                throw new ExtractionException($"Compiler/reference byte drift: {pin.Path}; expected {pin.Sha256}, got {actual}.");
            }

            (string assemblyName, string mvid, string[] dependencies) = ReadCompilerMetadata(fullPath, pin.Path);

            if (!string.Equals(assemblyName, pin.AssemblyName, StringComparison.Ordinal))
            {
                throw new ExtractionException($"Compiler/reference assembly mismatch for {pin.Path}: {assemblyName}.");
            }

            try
            {
                references.Add(MetadataReference.CreateFromFile(fullPath));
            }
            catch (Exception exception)
            {
                throw new ExtractionException($"Compiler/reference cannot be loaded for {pin.Path}: {exception.Message}");
            }

            identities.Add(new CompilerReferenceIdentity(pin.Path, pin.AssemblyName, pin.Sha256, mvid, true, dependencies));
        }

        CompilerReferenceIdentity[] ordered = identities
            .OrderBy(static identity => identity.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static identity => identity.Path, StringComparer.Ordinal)
            .ToArray();
        if (!identities.Select(static identity => identity.Path).SequenceEqual(ordered.Select(static identity => identity.Path), StringComparer.Ordinal))
        {
            throw new ExtractionException("The compiler/reference inventory is not in canonical path order.");
        }

        string aggregate = CompilerAggregateHash(identities);
        if (!string.Equals(aggregate, ReceiptCompilerInventoryAggregateSha256, StringComparison.Ordinal))
        {
            // The delegated inventory's aggregate is deliberately checked using its own canonical
            // fields. A different interpretation must not silently become this package's closure.
            throw new ExtractionException($"The compiler/reference inventory aggregate changed; expected {ReceiptCompilerInventoryAggregateSha256}, got {aggregate}.");
        }

        return new CompilerClosure(references.ToArray(), identities.ToArray(), inventorySha, aggregate);
    }

    private static string ResolveReferencePath(string root, string logicalPath)
    {
        if (logicalPath.StartsWith("platform/", StringComparison.Ordinal))
        {
            string platformDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location)
                ?? throw new ExtractionException("The runtime platform assembly directory is unavailable.");
            string fileName = logicalPath["platform/".Length..];
            if (fileName.Length == 0 || Path.GetFileName(fileName) != fileName)
            {
                throw new ExtractionException($"The platform compiler/reference path is not a file name: {logicalPath}.");
            }

            return Path.Combine(platformDirectory, fileName);
        }

        string path = Path.GetFullPath(Path.Combine(root, logicalPath));
        EnsureWithin(root, path);
        return path;
    }

    private static void ValidateCompiledSources(SourceFile[] sources, CompilerClosure closure)
    {
        if (sources.Length == 0 || closure.References.Length == 0)
        {
            throw new ExtractionException("The source/compiler closure is empty.");
        }

        foreach (SourceFile[] group in CompilationGroups(sources))
        {
            CSharpCompilation compilation = CreateCompilation(group, closure, "OrdinaryMachineAdmission");
            Diagnostic[] errors = compilation.GetDiagnostics()
                .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                .ToArray();
            if (errors.Length != 0)
            {
                string path = errors[0].Location.SourceTree?.FilePath ?? group[0].RelativePath;
                throw new ExtractionException($"The source/compiler closure has diagnostics in {path}: {errors[0].GetMessage()}.");
            }
        }
    }

    private static Dictionary<string, SemanticUnit> BuildSemanticUnits(SourceFile[] sources, CompilerClosure closure)
    {
        Dictionary<string, SemanticUnit> units = new(StringComparer.Ordinal);
        foreach (SourceFile[] group in CompilationGroups(sources))
        {
            CSharpCompilation compilation = CreateCompilation(group, closure, "OrdinaryMachineSemantics");
            foreach (SourceFile source in group)
            {
                SemanticModel model = compilation.GetSemanticModel(source.Tree, ignoreAccessibility: false);
                units.Add(source.RelativePath, new SemanticUnit(source, compilation, model));
            }
        }

        return units;
    }

    private static CSharpCompilation CreateCompilation(SourceFile[] sources, CompilerClosure closure, string namePrefix) =>
        CSharpCompilation.Create(
            $"{namePrefix}_{Path.GetFileNameWithoutExtension(sources[0].RelativePath)}",
            sources.Select(static source => source.Tree),
            closure.References,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Debug,
                allowUnsafe: true,
                nullableContextOptions: NullableContextOptions.Enable,
                concurrentBuild: false));

    private static IEnumerable<SourceFile[]> CompilationGroups(SourceFile[] sources)
    {
        SourceFile[] blockProcessorPartials = sources
            .Where(static source => IsBlockProcessorPartial(source.RelativePath))
            .ToArray();
        if (blockProcessorPartials.Length != 0) yield return blockProcessorPartials;

        SourceFile[] balManagerPartials = sources
            .Where(static source => IsBlockAccessListManagerPartial(source.RelativePath))
            .ToArray();
        if (balManagerPartials.Length != 0) yield return balManagerPartials;

        HashSet<string> groupedPaths = blockProcessorPartials
            .Concat(balManagerPartials)
            .Select(static source => source.RelativePath)
            .ToHashSet(StringComparer.Ordinal);
        foreach (SourceFile source in sources.Where(source => !groupedPaths.Contains(source.RelativePath))) yield return [source];
    }

    private static bool IsBlockProcessorPartial(string relativePath)
    {
        string fileName = Path.GetFileName(relativePath);
        return fileName.StartsWith("BlockProcessor", StringComparison.Ordinal)
            && !fileName.EndsWith(".zkevm.cs", StringComparison.Ordinal);
    }

    private static bool IsBlockAccessListManagerPartial(string relativePath)
    {
        string fileName = Path.GetFileName(relativePath);
        return fileName.Equals("BlockAccessListManager.cs", StringComparison.Ordinal)
            || fileName.StartsWith("BlockAccessListManager.", StringComparison.Ordinal);
    }

    private static MachineShape BindMachine(
        string root,
        Dictionary<string, SemanticUnit> units,
        List<TypedBinding> allBindings,
        ReceiptTerminalSourceClosureIdentity receiptTerminalSourceClosure)
    {
        ValidateIdentityLedgers(units);
        SemanticUnit processor = Unit(units, SourceSpecs[0].Path);
        SemanticUnit options = Unit(units, SourceSpecs[1].Path);
        SemanticUnit routing = Unit(units, SourceSpecs[2].Path);
        SemanticUnit executeAdapter = Unit(units, ExecuteAdapterSourcePath);
        SemanticUnit processorInterface = Unit(units, ProcessorInterfaceSourcePath);
        SemanticUnit adapterExtensions = Unit(units, AdapterExtensionsSourcePath);
        SemanticUnit executor = Unit(units, SourceSpecs[19].Path);
        SemanticUnit parallelExecutor = Unit(units, SourceSpecs[20].Path);
        SemanticUnit balManager = Unit(units, SourceSpecs[21].Path);
        SemanticUnit di = Unit(units, SourceSpecs[23].Path);
        SemanticUnit tracer = Unit(units, SourceSpecs[24].Path);
        SemanticUnit fork = Unit(units, SourceSpecs[26].Path);
        SemanticUnit blockProcessor = Unit(units, SourceSpecs[30].Path);

        MethodHandle setContext = BindMethod(processor, TransactionProcessorType, "SetBlockExecutionContext", 1, "BlockExecutionContext");
        MethodHandle process = BindMethod(processor, TransactionProcessorType, "Process", 3);
        MethodHandle executeCore = BindMethod(processor, TransactionProcessorType, "ExecuteCore", 3);
        MethodHandle executeContext = BindMethod(processor, TransactionProcessorType, "Execute", 3);
        MethodHandle executeMachine = BindMethod(processor, TransactionProcessorType, "Execute", 6);
        MethodHandle fastPathCandidate = BindMethod(processor, TransactionProcessorType, "IsSimpleTransferFastPathCandidate", 2);
        MethodHandle noExecutableCode = BindMethod(processor, TransactionProcessorType, "HasNoExecutableCode", 2);
        MethodHandle prepareFastPathMethod = BindMethod(processor, TransactionProcessorType, "PrepareSimpleTransferFastPath", 4);
        MethodHandle simpleTransfer = BindMethod(processor, TransactionProcessorType, "ExecuteSimpleTransfer", 15);
        MethodHandle updateFees = BindMethod(processor, TransactionProcessorType, "UpdateHeaderGasUsedAndPayFees", 11);
        MethodHandle finalize = BindMethod(processor, TransactionProcessorType, "FinalizeTransaction", 12);
        MethodHandle executeAdapterMethod = BindMethod(executeAdapter, ExecuteAdapterType, "Execute", 2);
        MethodHandle executeAdapterSetContext = BindMethod(executeAdapter, ExecuteAdapterType, "SetBlockExecutionContext", 1);
        MethodHandle commitExtension = BindMethod(processorInterface, ProcessorExtensionsType, "Execute", 2);
        MethodHandle startTxTrace = BindMethod(adapterExtensions, AdapterExtensionsType, "ProcessTransaction", 5);
        MethodHandle processTransactions = BindMethod(executor, DirectExecutorType, "ProcessTransactions", 4);
        MethodHandle processTransaction = BindMethod(executor, DirectExecutorType, "ProcessTransaction", 5);
        MethodHandle directExecutorSetContext = BindMethod(executor, DirectExecutorType, "SetBlockExecutionContext", 1);
        RequireExactDirectExecutorRoute(processTransactions, processTransaction);
        MethodHandle balProcessTransactions = BindMethod(parallelExecutor, ParallelExecutorType, "ProcessTransactions", 4);
        MethodHandle decoratedSetContext = BindMethod(parallelExecutor, ParallelExecutorType, "SetBlockExecutionContext", 1);
        MethodHandle startBlockTrace = BindMethod(tracer, ReceiptTracerType, "StartNewBlockTrace", 1);
        MethodHandle startTracerTx = BindMethod(tracer, ReceiptTracerType, "StartNewTxTrace", 1);
        MethodHandle endTracerTx = BindMethod(tracer, ReceiptTracerType, "EndTxTrace", 0);
        MethodHandle markSuccess = BindMethod(tracer, ReceiptTracerType, "MarkAsSuccess", 5);
        MethodHandle buildReceipt = BindMethod(tracer, ReceiptTracerType, "BuildReceipt", 5);
        MethodHandle processBlock = BindMethod(blockProcessor, BlockProcessorType, "ProcessBlock", 5);
        MethodHandle processOne = BindMethod(blockProcessor, BlockProcessorType, "ProcessOne", 5);
        MethodHandle balPrepare = BindMethod(balManager, BalManagerType, "PrepareForProcessing", 3);
        MethodHandle balSetContext = BindMethod(balManager, BalManagerType, "SetBlockExecutionContext", 1);
        MethodHandle diLoad = BindMethod(di, BlockProcessingModuleType, "Load", 1);
        MethodHandle validationDiLoad = BindMethod(di, StandardValidationModuleType, "Load", 1);
        MethodHandle createExecuteAdapter = BindMethod(di, BlockProcessingModuleType, "CreateExecuteAdapter", 1);

        List<Anchor> anchors = [];
        List<ControlFlowIdentity> controlFlows = [];

        Add(anchors, BindPrimaryConstructorParameter(
            blockProcessor, BlockProcessorType, "blockTransactionsExecutor", "IBlockTransactionsExecutor",
            "di.blockProcessorExecutorParameter"));
        Add(anchors, BindFieldInitializer(
            blockProcessor, BlockProcessorType, "_blockTransactionsExecutor",
            "_blockTransactionsExecutor=blockTransactionsExecutor", "di.blockProcessorExecutorStore"));
        Add(anchors, BindPrimaryConstructorParameter(
            parallelExecutor, ParallelExecutorType, "inner",
            "IBlockProcessor.IBlockTransactionsExecutor", "di.decoratorInnerParameter"));
        Add(anchors, BindPrimaryConstructorParameter(
            parallelExecutor, ParallelExecutorType, "balManager",
            "IBlockAccessListManager", "di.decoratorBalParameter"));
        Add(anchors, BindPrimaryConstructorParameter(
            executor, DirectExecutorType, "transactionProcessor",
            "ITransactionProcessorAdapter", "di.directAdapterParameter"));
        Add(anchors, BindPrimaryConstructorParameter(
            executeAdapter, ExecuteAdapterType, "transactionProcessor",
            "ITransactionProcessor", "di.executeProcessorParameter"));

        Anchor resetExecutionGas = Add(anchors, BindAssignment(setContext, "_blockCumulativeExecutionGas", "context.reset.executionGas"));
        Anchor resetStateGas = Add(anchors, BindAssignment(setContext, "_blockCumulativeStateGas", "context.reset.stateGas"));
        Anchor installContext = Add(anchors, BindInvocation(setContext, "SetBlockExecutionContext", "context.install", "blockExecutionContext"));
        RequireSourceOrder(resetExecutionGas, resetStateGas, "SetBlockExecutionContext must reset execution gas before state gas.");
        RequireSourceOrder(resetStateGas, installContext, "SetBlockExecutionContext must install the VM context after both accumulator resets.");
        RequireDominates(setContext, resetExecutionGas, installContext, "The execution-gas reset must dominate VM context installation.");
        RequireDominates(setContext, resetStateGas, installContext, "The state-gas reset must dominate VM context installation.");

        Anchor processToCore = Add(anchors, BindInvocation(process, "ExecuteCore", "process.toExecuteCore", "transaction"));
        Anchor systemGuard = Add(anchors, BindInvocation(executeCore, "UseSystemProcessor", "route.systemGuard", "tx.IsSystem"));
        Anchor ordinaryExecute = Add(anchors, BindInvocation(executeCore, "Execute", "route.ordinaryExecute", "tracer", occurrence: 1, expectedCount: 2));
        RequireSourceOrder(systemGuard, ordinaryExecute, "ExecuteCore must evaluate the system guard before ordinary dispatch.");
        RequireDominates(executeCore, systemGuard, ordinaryExecute, "The ordinary dispatch must be dominated by the source routing guard.");

        Anchor adapterExecuteCommit = Add(anchors, BindInvocation(executeAdapterMethod, "Execute", "adapter.commitExecute", "transactionProcessor"));
        RequireCanonicalBinding(
            adapterExecuteCommit,
            "transactionProcessor.Execute(transaction, txTracer)",
            "The standard execute adapter no longer delegates to ITransactionProcessor.Execute.");
        Anchor commitProcess = Add(anchors, BindInvocation(commitExtension, "Process", "adapter.commitProcess", "ExecutionOptions.Commit"));
        RequireCanonicalBinding(
            commitProcess,
            "transactionProcessor.Process(transaction, txTracer, ExecutionOptions.Commit)",
            "The ordinary adapter extension no longer selects exact ExecutionOptions.Commit.");

        Anchor recoverBeforeIntrinsic = Add(anchors, BindInvocation(executeContext, "RecoverSenderBeforeIntrinsicGas", "admission.recoverBeforeIntrinsic", "tx"));
        Anchor calculateIntrinsic = Add(anchors, BindInvocation(executeContext, "CalculateIntrinsicGas", "admission.intrinsic", "header.GasLimit"));
        Anchor executeWithIntrinsic = Add(anchors, BindInvocation(executeContext, "Execute", "admission.contextHandoff", "in intrinsicGas"));
        RequireSourceOrder(recoverBeforeIntrinsic, calculateIntrinsic, "Sender recovery must precede intrinsic-gas calculation.");
        RequireSourceOrder(calculateIntrinsic, executeWithIntrinsic, "The cached intrinsic value must feed the six-parameter Execute handoff.");

        Anchor staticValidation = Add(anchors, BindInvocation(executeMachine, "ValidateStatic", "admission.staticValidation", "intrinsicGas"));
        Anchor senderValidation = Add(anchors, BindInvocation(executeMachine, "ValidateSender", "admission.senderValidation", "tracer"));
        Anchor buyGas = Add(anchors, BindInvocation(executeMachine, "BuyGas", "admission.buyGas", "effectiveGasPrice"));
        Anchor incrementNonce = Add(anchors, BindInvocation(executeMachine, "IncrementNonce", "admission.incrementNonce", "opts"));
        Anchor prepareFastPath = Add(anchors, BindInvocation(executeMachine, "PrepareSimpleTransferFastPath", "dispatch.prepareFastPath", "spec"));
        Anchor precommit = Add(anchors, BindInvocation(executeMachine, "Commit", "dispatch.precommit", "commitRoots:false"));
        Anchor availableGas = Add(anchors, BindInvocation(executeMachine, "CalculateAvailableGas", "dispatch.availableGas", "out"));
        Anchor simpleHandoff = Add(anchors, BindInvocation(executeMachine, "ExecuteSimpleTransfer", "dispatch.simpleTransfer", "simpleTransferRecipient"));
        Anchor evmHandoff = Add(anchors, BindInvocation(executeMachine, "ExecuteEvmTransaction", "dispatch.evmExcluded", "preloadedCodeInfo"));
        RequireSourceOrder(staticValidation, senderValidation, "Static validation must precede sender validation.");
        RequireSourceOrder(senderValidation, buyGas, "Sender validation must precede gas reservation.");
        RequireSourceOrder(buyGas, incrementNonce, "Gas reservation must precede nonce increment.");
        RequireSourceOrder(incrementNonce, prepareFastPath, "Nonce increment must precede simple-transfer preparation.");
        RequireSourceOrder(prepareFastPath, precommit, "Fast-path preparation must precede the optional pre-execution commit.");
        RequireSourceOrder(precommit, availableGas, "The optional pre-execution commit must precede available-gas initialization.");
        RequireSourceOrder(availableGas, simpleHandoff, "Available-gas initialization must precede the simple-transfer handoff.");
        RequireSourceOrder(availableGas, evmHandoff, "Available-gas initialization must precede the excluded EVM handoff.");
        RequireDominates(executeMachine, incrementNonce, prepareFastPath, "The fast-path guard must be reached only after successful nonce admission.");
        RequireDominates(executeMachine, availableGas, simpleHandoff, "Simple-transfer dispatch must be dominated by available-gas initialization.");

        Anchor fastPathCandidateBody = Add(anchors, BindExpressionBody(fastPathCandidate, "dispatch.fastPathCandidate"));
        RequireCanonicalBinding(
            fastPathCandidateBody,
            "!isCodeOverridable && tx.AuthorizationList is null && !ForceSimpleTransferDisabled",
            "The simple-transfer candidate guards changed.");
        Anchor fastPathCandidateCall = Add(anchors, BindInvocation(prepareFastPathMethod, "IsSimpleTransferFastPathCandidate", "dispatch.fastPathCandidateCall", "_isCodeOverridable"));
        Anchor fastPathEntryGuard = Add(anchors, BindPrefixIf(prepareFastPathMethod, "recipient is null || !IsSimpleTransferFastPathCandidate", "dispatch.fastPathEntryGuard"));
        RequireCanonicalBinding(
            fastPathEntryGuard,
            "recipient is null || !IsSimpleTransferFastPathCandidate(tx, _isCodeOverridable)",
            "The simple-transfer preparation entry guard changed.");
        Anchor noExecutableCodeBody = Add(anchors, BindExpressionBody(noExecutableCode, "dispatch.noExecutableCode"));
        RequireCanonicalBinding(
            noExecutableCodeBody,
            "delegationAddress is null && codeInfo.IsEmpty",
            "The no-executable-code/delegation predicate changed.");
        Anchor noExecutableCodeCall = Add(anchors, BindInvocation(prepareFastPathMethod, "HasNoExecutableCode", "dispatch.noExecutableCodeCall", "preloadedDelegationAddress"));
        RequireSourceOrder(fastPathCandidateCall, noExecutableCodeCall, "Fast-path eligibility must be checked before code and delegation classification.");

        Anchor stateCharge = Add(anchors, BindInvocation(simpleTransfer, "TryConsumeStateGas", "simple.stateCharge", "gasAvailable"));
        Anchor recipientDeadCheck = Add(anchors, BindInvocation(simpleTransfer, "IsDeadAccount", "simple.recipientDeadCheck", "recipient"));
        Anchor payValue = Add(anchors, BindInvocation(simpleTransfer, "PayValue", "simple.payValue", "opts"));
        Anchor recipientWrite = Add(anchors, BindInvocation(simpleTransfer, "AddToBalanceAndCreateIfNotExists", "simple.recipientWrite", "recipient"));
        Anchor clearExecutionGas = Add(anchors, BindInvocation(simpleTransfer, "ClearExecutionGas", "simple.oogForfeit", "gasAvailable"));
        Anchor simpleRefund = Add(anchors, BindInvocation(simpleTransfer, "Refund", "simple.refund", "postIntrinsicStateReservoir"));
        Anchor simpleAccess = Add(anchors, BindInvocation(simpleTransfer, "ReportSimpleTransferAccess", "simple.access", "recipient"));
        Anchor updateHeaderFees = Add(anchors, BindInvocation(simpleTransfer, "UpdateHeaderGasUsedAndPayFees", "simple.headerAndFees", "statusCode"));
        Anchor finalizeTransaction = Add(anchors, BindInvocation(simpleTransfer, "FinalizeTransaction", "simple.finalize", "statusCode"));
        RequireSourceOrder(stateCharge, payValue, "The simple-transfer state-charge guard must precede value payment.");
        RequireSourceOrder(recipientDeadCheck, payValue, "Recipient liveness must be classified before value payment.");
        RequireSourceOrder(clearExecutionGas, simpleRefund, "The simple-transfer gas-forfeit branch must precede refund settlement.");
        RequireSourceOrder(payValue, recipientWrite, "PayValue must precede the recipient balance write on the simple-transfer path.");
        RequireSourceOrder(recipientWrite, simpleRefund, "The recipient write must precede refund settlement.");
        RequireSourceOrder(simpleAccess, updateHeaderFees, "Simple-transfer access reporting must precede header gas and fee accounting.");
        RequireSourceOrder(simpleRefund, updateHeaderFees, "Refund settlement must precede header gas and fee accounting.");
        RequireSourceOrder(updateHeaderFees, finalizeTransaction, "Header gas and fees must precede common finalization.");
        RequireDominates(simpleTransfer, simpleRefund, updateHeaderFees, "The common header/fee boundary must be dominated by simple-transfer settlement.");
        RequireDominates(simpleTransfer, updateHeaderFees, finalizeTransaction, "Common finalization must be dominated by header/fee accounting.");

        Anchor counterGuard = Add(anchors, BindInvocation(updateFees, "ParticipatesInNormalBlockCounters", "fees.counterGuard", "_parallel"));
        Anchor payFees = Add(anchors, BindInvocation(updateFees, "PayFees", "fees.payFees", "statusCode"));
        RequireSourceOrder(counterGuard, payFees, "The normal counter guard must precede fee payment.");

        Anchor finalizeCommit = Add(anchors, BindInvocation(finalize, "Commit", "finalize.commit", "commitRoots:!spec.IsEip658Enabled"));
        Anchor receiptSuccess = Add(anchors, BindInvocation(finalize, "MarkAsSuccess", "finalize.receiptSuccess", "executingAccount"));
        Anchor receiptFailure = Add(anchors, BindInvocation(finalize, "MarkAsFailed", "finalize.receiptFailure", "executingAccount"));
        Anchor resultReturn = Add(anchors, BindReturn(finalize, "finalize.resultReturn", "EvmException"));
        RequireSourceOrder(finalizeCommit, receiptFailure, "Finalization's commit branch must precede its receipt terminal branches.");
        RequireSourceOrder(receiptFailure, receiptSuccess, "Failure and success receipt branches must retain source order.");
        RequireSourceOrder(receiptSuccess, resultReturn, "The receipt terminal must precede the TransactionResult return.");

        Anchor adapterStart = Add(anchors, BindInvocation(startTxTrace, "StartNewTxTrace", "adapter.startTxTrace", "currentTx"));
        Anchor adapterExecute = Add(anchors, BindInvocation(startTxTrace, "Execute", "adapter.execute", ""));
        Anchor adapterEnd = Add(anchors, BindInvocation(startTxTrace, "EndTxTrace", "adapter.endTxTrace", ""));
        RequireCanonicalBinding(
            adapterExecute,
            "transactionProcessor.Execute(currentTx, receiptsTracer)",
            "TransactionProcessorAdapterExtensions no longer forwards the exact transaction and base receipts tracer.");
        RequireSourceOrder(adapterStart, adapterExecute, "TransactionProcessorAdapterExtensions must start tracing before Execute.");
        RequireSourceOrder(adapterExecute, adapterEnd, "TransactionProcessorAdapterExtensions must end tracing after Execute.");
        RequireDominates(startTxTrace, adapterStart, adapterExecute, "StartNewTxTrace must dominate Execute on the normal adapter path.");
        RequirePostDominates(startTxTrace, adapterEnd, adapterExecute, "EndTxTrace must postdominate Execute on the normal adapter path.");

        Anchor executeAdapterContext = Add(anchors, BindInvocation(executeAdapterSetContext, "SetBlockExecutionContext", "context.executeAdapter", "transactionProcessor"));
        RequireCanonicalBinding(
            executeAdapterContext,
            "transactionProcessor.SetBlockExecutionContext(in blockExecutionContext)",
            "The execute adapter no longer forwards the exact block execution context.");

        Anchor executorAdapterCall = Add(anchors, BindInvocation(processTransaction, "ProcessTransaction", "executor.adapterCall", "processingOptions"));
        RequireCanonicalBinding(
            executorAdapterCall,
            "transactionProcessor.ProcessTransaction(currentTx, receiptsTracer, processingOptions, _stateProvider)",
            "The direct executor no longer forwards the exact transaction, receipts tracer, options, and world state to the adapter extension.");
        Anchor executorResultGuard = Add(anchors, BindPrefixIf(processTransaction, "!result", "executor.invalidResultGuard"));
        Anchor executorInvalidThrow = Add(anchors, BindInvocation(processTransaction, "ThrowInvalidTransactionException", "executor.invalidResultThrow", "result"));
        Anchor executorProcessedEvent = Add(anchors, BindInvocation(processTransaction, "OnTransactionProcessed", "executor.processedEvent", "receiptsTracer.TxReceipts[index]"));
        RequireSourceOrder(executorAdapterCall, executorResultGuard, "The direct executor must inspect the adapter result after normal return.");
        RequireSoleInvocationInTrueArm(processTransaction, executorResultGuard, executorInvalidThrow, "The invalid-result guard must directly throw in its true arm.");
        RequireSourceOrder(executorResultGuard, executorInvalidThrow, "The invalid-result guard must precede its direct throw.");
        RequireDominates(processTransaction, executorResultGuard, executorInvalidThrow, "The invalid-result guard must dominate its direct throw.");
        RequireDominates(processTransaction, executorResultGuard, executorProcessedEvent, "The invalid-result guard must dominate the normal processed callback.");
        RequireSourceOrder(executorInvalidThrow, executorProcessedEvent, "The processed callback must follow the invalid-result throw guard.");
        RequirePostDominates(processTransaction, executorProcessedEvent, executorAdapterCall, "The processed callback must postdominate a successful direct adapter return.");

        Anchor transactionLoop = Add(anchors, BindPrefixIf(processTransactions, "shouldValidate", "executor.validationMode"));
        Anchor executorProcessCall = Add(anchors, BindInvocation(processTransactions, "ProcessTransaction", "executor.processCall", "receiptsTracer"));
        Anchor gasLimitGuard = Add(anchors, BindPrefixIf(processTransactions, "block.Header.GasUsed", "executor.blockGasLimitGuard"));
        Anchor gasLimitThrow = Add(anchors, BindInvocation(processTransactions, "ThrowInvalidBlockForGasLimit", "executor.gasLimitThrow", "block"));
        RequireSourceOrder(transactionLoop, executorProcessCall, "Sequential processing must evaluate the validation mode before processing transactions.");
        RequireSourceOrder(executorProcessCall, gasLimitGuard, "The block gas-limit guard must follow the transaction call.");
        RequireSoleInvocationInTrueArm(processTransactions, gasLimitGuard, gasLimitThrow, "The block-gas guard must directly throw in its true arm.");
        RequireSourceOrder(gasLimitGuard, gasLimitThrow, "The block-gas guard must precede its direct throw.");
        RequireDominates(processTransactions, gasLimitGuard, gasLimitThrow, "The block-gas guard must dominate its direct throw.");

        Anchor directExecutorContext = Add(anchors, BindInvocation(directExecutorSetContext, "SetBlockExecutionContext", "context.directExecutor", "transactionProcessor"));
        RequireCanonicalBinding(
            directExecutorContext,
            "transactionProcessor.SetBlockExecutionContext(in blockExecutionContext)",
            "The direct executor no longer forwards the exact block execution context.");

        Anchor balGuard = Add(anchors, BindPrefixIf(balProcessTransactions, "!balManager.Enabled", "bal.directInnerGuard"));
        Anchor directInnerCall = Add(anchors, BindInvocation(balProcessTransactions, "ProcessTransactions", "bal.directInnerCall", "inner"));
        RequireSoleReturnInTrueArm(balProcessTransactions, balGuard, directInnerCall, "BAL-disabled direct-inner dispatch must be the sole return in the true guard arm.");
        RequireSourceOrder(balGuard, directInnerCall, "BAL guard must dominate direct-inner dispatch.");
        RequireDominates(balProcessTransactions, balGuard, directInnerCall, "BAL guard must dominate direct-inner dispatch on the normal CFG.");
        RequireCanonicalBinding(
            directInnerCall,
            "inner.ProcessTransactions(block, processingOptions, receiptsTracer, token)",
            "BAL-disabled direct-inner dispatch changed its typed argument order.");

        Anchor decoratedContextBal = Add(anchors, BindInvocation(decoratedSetContext, "SetBlockExecutionContext", "context.decoratorBal", "balManager"));
        Anchor decoratedContextInner = Add(anchors, BindInvocation(decoratedSetContext, "SetBlockExecutionContext", "context.decoratorInner", "inner"));
        RequireCanonicalBinding(
            decoratedContextBal,
            "balManager.SetBlockExecutionContext(blockExecutionContext)",
            "The executor decorator no longer installs the BAL block context first.");
        RequireCanonicalBinding(
            decoratedContextInner,
            "inner.SetBlockExecutionContext(blockExecutionContext)",
            "The executor decorator no longer forwards the exact context to its inner executor.");
        RequireUnconditional(decoratedSetContext, decoratedContextBal, "BAL context installation must be unconditional.");
        RequireUnconditional(decoratedSetContext, decoratedContextInner, "Inner-executor context forwarding must be unconditional.");
        RequireSourceOrder(decoratedContextBal, decoratedContextInner, "BAL context installation must precede inner-executor context forwarding.");

        Anchor balContextStore = Add(anchors, BindAssignment(balSetContext, "_blockExecutionContext", "context.balStore"));
        RequireCanonicalBinding(
            balContextStore,
            "_blockExecutionContext = blockExecutionContext",
            "The BAL manager no longer retains the exact block execution context.");

        Anchor blockResetIndex = Add(anchors, BindAssignment(startBlockTrace, "_currentIndex", "tracer.blockReset.index"));
        Anchor blockResetCurrentTx = Add(anchors, BindAssignment(startBlockTrace, "CurrentTx", "tracer.blockReset.currentTx"));
        Anchor blockResetTracer = Add(anchors, BindAssignment(startBlockTrace, "_currentTxTracer", "tracer.blockReset.tracer"));
        Anchor blockClearReceipts = Add(anchors, BindInvocation(startBlockTrace, "Clear", "tracer.blockReset.receipts", "_txReceipts"));
        Anchor blockClearGasHistory = Add(anchors, BindInvocation(startBlockTrace, "Clear", "tracer.blockReset.blockGas", "_cumulativeBlockGasPerTx"));
        Anchor blockResetCumulative = Add(anchors, BindAssignment(startBlockTrace, "_cumulativeReceiptGas", "tracer.blockReset.receiptGas"));
        RequireCanonicalBinding(blockResetIndex, "_currentIndex = 0", "Block trace reset must set the receipt index to zero.");
        RequireCanonicalBinding(blockResetCurrentTx, "CurrentTx = null", "Block trace reset must clear the current transaction.");
        RequireCanonicalBinding(blockResetTracer, "_currentTxTracer = NullTxTracer.Instance", "Block trace reset must install the null transaction tracer.");
        RequireCanonicalBinding(blockResetCumulative, "_cumulativeReceiptGas = 0", "Block trace reset must clear cumulative receipt gas.");
        RequireUnconditional(startBlockTrace, blockResetIndex, "Block trace index reset must be unconditional.");
        RequireUnconditional(startBlockTrace, blockResetCurrentTx, "Block trace current transaction reset must be unconditional.");
        RequireUnconditional(startBlockTrace, blockResetTracer, "Block trace null transaction tracer reset must be unconditional.");
        RequireUnconditional(startBlockTrace, blockClearReceipts, "Block trace receipt-history reset must be unconditional.");
        RequireUnconditional(startBlockTrace, blockClearGasHistory, "Block trace block-gas-history reset must be unconditional.");
        RequireUnconditional(startBlockTrace, blockResetCumulative, "Block trace cumulative receipt gas reset must be unconditional.");
        RequireSourceOrder(blockResetIndex, blockResetCurrentTx, "Block trace reset must clear the index before the current transaction.");
        RequireSourceOrder(blockResetCurrentTx, blockResetTracer, "Block trace reset must clear CurrentTx before the inner tracer.");
        RequireSourceOrder(blockResetTracer, blockClearReceipts, "Block trace reset must clear the tracer before receipt storage.");
        RequireSourceOrder(blockClearReceipts, blockClearGasHistory, "Block trace reset must clear receipt storage before block-gas history.");
        RequireSourceOrder(blockClearGasHistory, blockResetCumulative, "Block trace reset must clear block-gas history before cumulative receipt gas.");

        Anchor receiptAppend = Add(anchors, BindInvocation(markSuccess, "BuildReceipt", "tracer.receiptAppend", "gasSpent"));
        Anchor receiptOtherGuard = Add(anchors, BindPrefixIf(markSuccess, "_otherTracer is ITxTracer", "tracer.nestedForwardGuard"));
        Anchor receiptOther = Add(anchors, BindInvocation(markSuccess, "MarkAsSuccess", "tracer.delegateSuccess", "otherTxTracer"));
        Anchor receiptCurrentGuard = Add(anchors, BindPrefixIf(markSuccess, "_currentTxTracer.IsTracingReceipt", "tracer.currentForwardGuard"));
        Anchor receiptCurrent = Add(anchors, BindInvocation(markSuccess, "MarkAsSuccess", "tracer.currentSuccess", "_currentTxTracer"));
        RequireCanonicalBinding(
            receiptAppend,
            "BuildReceipt(recipient, gasSpent, StatusCode.Success, logs, stateRoot)",
            "Successful receipt append must construct a success receipt from the terminal fields.");
        RequireUnconditional(markSuccess, receiptAppend, "Successful receipt append must be unconditional.");
        RequireCanonicalBinding(receiptOtherGuard, "_otherTracer is ITxTracer otherTxTracer", "The nested receipt-forwarding guard changed.");
        RequireCanonicalBinding(receiptCurrentGuard, "_currentTxTracer.IsTracingReceipt", "The current transaction receipt-forwarding guard changed.");
        RequireSourceOrder(receiptAppend, receiptOther, "Base receipt append must precede nested-tracer forwarding.");
        RequireSourceOrder(receiptOther, receiptCurrent, "Nested-tracer forwarding must precede current-tracer forwarding.");
        RequireDominates(markSuccess, receiptAppend, receiptOther, "Base receipt append must dominate nested-tracer forwarding.");
        RequireDominates(markSuccess, receiptOther, receiptCurrent, "Nested-tracer forwarding must dominate current-tracer forwarding.");
        Anchor receiptGasUpdate = Add(anchors, BindInvocation(buildReceipt, "UpdateCumulativeGasTracking", "tracer.gasUpdate", "gasConsumed"));
        RequireSourceOrder(receiptGasUpdate, receiptAppend, "Receipt construction must update cumulative gas before append.");
        Anchor receiptIndex = Add(anchors, BindAssignment(buildReceipt, "Index", "tracer.receiptIndex"));
        RequireCanonicalBinding(receiptIndex, "Index = _currentIndex", "Receipt construction must use the current transaction index.");
        RequireUnconditional(buildReceipt, receiptIndex, "Receipt index construction must be unconditional.");
        RequireSourceOrder(receiptGasUpdate, receiptIndex, "Receipt construction must update cumulative gas before assigning its index.");
        RequireUnconditional(buildReceipt, receiptGasUpdate, "Receipt cumulative gas update must be unconditional.");
        Anchor txEndDelegate = Add(anchors, BindInvocation(endTracerTx, "EndTxTrace", "tracer.tx-end-delegate", ""));
        Anchor txIndexIncrement = Add(anchors, BindIncrement(endTracerTx, "_currentIndex", "tracer.endTx.indexIncrement"));
        RequireUnconditional(endTracerTx, txEndDelegate, "EndTxTrace must forward to the wrapped tracer unconditionally.");
        RequireUnconditional(endTracerTx, txIndexIncrement, "EndTxTrace must increment the receipt index unconditionally.");
        RequireSourceOrder(txEndDelegate, txIndexIncrement, "EndTxTrace must forward before incrementing the receipt index.");
        RequireDominates(endTracerTx, txEndDelegate, txIndexIncrement, "Wrapped EndTxTrace must dominate the receipt-index increment.");
        Anchor txStartCurrent = Add(anchors, BindAssignment(startTracerTx, "CurrentTx", "tracer.txStart.currentTx"));
        Anchor txStartDelegate = Add(anchors, BindInvocation(startTracerTx, "StartNewTxTrace", "tracer.txStart.delegate", "_otherTracer"));
        Anchor txStartTracer = Add(anchors, BindAssignment(startTracerTx, "_currentTxTracer", "tracer.txStart.tracer"));
        RequireSourceOrder(txStartCurrent, txStartDelegate, "StartNewTxTrace must set CurrentTx before invoking the wrapped tracer.");
        RequireSourceOrder(txStartDelegate, txStartTracer, "StartNewTxTrace must install the wrapped tracer result after invoking it.");
        RequireSourceOrder(txStartCurrent, txStartTracer, "StartNewTxTrace must set CurrentTx before the current tracer.");

        Anchor blockStartTrace = Add(anchors, BindInvocation(processBlock, "StartNewBlockTrace", "block.startTrace", "block"));
        Anchor createBlockContext = Add(anchors, BindInvocation(processBlock, "CreateBlockExecutionContext", "block.createContext", "block.Header"));
        Anchor blockSetContext = Add(anchors, BindInvocation(processBlock, "SetBlockExecutionContext", "block.setContext", "_blockTransactionsExecutor"));
        Anchor blockPreCommit = Add(anchors, BindInvocation(processBlock, "CommitState", "block.preCommit", "spec", occurrence: 0, expectedCount: 3));
        Anchor blockFold = Add(anchors, BindInvocation(processBlock, "ProcessTransactions", "block.fold", "receiptsTracer"));
        Anchor transactionsExecuted = Add(anchors, BindInvocation(processBlock, "Invoke", "block.transactionsExecuted", "TransactionsExecuted"));
        Anchor blockPostCommit = Add(anchors, BindInvocation(processBlock, "CommitState", "block.postCommit", "spec", occurrence: 1, expectedCount: 3));
        RequireCanonicalBinding(
            blockSetContext,
            "_blockTransactionsExecutor.SetBlockExecutionContext(CreateBlockExecutionContext(block.Header, spec))",
            "BlockProcessor no longer forwards the exact constructed block execution context.");
        RequireSourceOrder(blockStartTrace, blockSetContext, "Block tracing must start before block execution context installation.");
        RequireSourceOrder(blockSetContext, blockPreCommit, "Block execution context installation must precede the pre-transaction CommitState boundary.");
        RequireSourceOrder(blockPreCommit, blockFold, "The pre-transaction CommitState boundary must precede sequential transaction folding.");
        RequireSourceOrder(blockFold, transactionsExecuted, "TransactionsExecuted must follow the completed transaction fold.");
        RequireSourceOrder(transactionsExecuted, blockPostCommit, "The post-transaction CommitState boundary must follow TransactionsExecuted.");
        RequireDominates(processBlock, blockStartTrace, blockSetContext, "StartNewBlockTrace must dominate block execution context installation.");
        RequireDominates(processBlock, blockSetContext, blockPreCommit, "Block execution context installation must dominate the pre-transaction CommitState boundary.");
        RequireDominates(processBlock, blockPreCommit, blockFold, "The pre-transaction CommitState boundary must dominate the transaction fold.");
        RequirePostDominates(processBlock, transactionsExecuted, blockFold, "TransactionsExecuted must postdominate a normally returned transaction fold.");
        RequirePostDominates(processBlock, blockPostCommit, transactionsExecuted, "The post-transaction CommitState boundary must postdominate TransactionsExecuted.");

        Anchor processOneBalPrepare = Add(anchors, BindInvocation(processOne, "PrepareForProcessing", "block.balPrepareCall", "suggestedBlock"));
        Anchor processOneBlockCall = Add(anchors, BindInvocation(processOne, "ProcessBlock", "block.processBlockCall", "blockTracer"));
        RequireSourceOrder(processOneBalPrepare, processOneBlockCall, "BAL Enabled must be derived before ProcessOne enters ProcessBlock.");
        RequireDominates(processOne, processOneBalPrepare, processOneBlockCall, "The BAL preparation call must dominate the standard ProcessBlock call.");

        Anchor processorRegistration = Add(anchors, BindFluentInvocation(diLoad, "AddScoped", "di.processor",
            "AddScoped<ITransactionProcessor,EthereumTransactionProcessor>"));
        Anchor worldRegistration = Add(anchors, BindFluentInvocation(diLoad, "AddScoped", "di.worldState",
            "AddScoped<IWorldState,WorldState>"));
        Anchor blockProcessorRegistration = Add(anchors, BindFluentInvocation(diLoad, "AddScoped", "di.blockProcessor",
            "AddScoped<IBlockProcessor,BlockProcessor>"));
        Anchor balManagerRegistration = Add(anchors, BindFluentInvocation(diLoad, "AddScoped", "di.balManager",
            "AddScoped<IBlockAccessListManager,BlockAccessListManager>"));
        Anchor validationModuleRegistration = Add(anchors, BindFluentInvocation(diLoad, "AddSingleton", "di.validationModule",
            "AddSingleton<IBlockValidationModule,StandardBlockValidationModule>"));
        Anchor executorRegistration = Add(anchors, BindFluentInvocation(validationDiLoad, "AddScoped", "di.directExecutor",
            "AddScoped<IBlockProcessor.IBlockTransactionsExecutor,BlockProcessor.BlockValidationTransactionsExecutor>"));
        Anchor decoratorRegistration = Add(anchors, BindFluentInvocation(validationDiLoad, "AddDecorator", "di.parallelDecorator",
            "AddDecorator<IBlockProcessor.IBlockTransactionsExecutor,BlockProcessor.ParallelBlockValidationTransactionsExecutor>"));
        Anchor adapterFactoryRegistration = Add(anchors, BindFluentInvocation(diLoad, "AddScoped", "di.adapterFactory",
            "AddScoped<TransactionProcessorAdapterFactory>", "CreateExecuteAdapter"));
        Anchor adapterRegistration = Add(anchors, BindFluentInvocation(diLoad, "AddScoped", "di.adapter",
            "AddScoped<ITransactionProcessorAdapter,ITransactionProcessor,TransactionProcessorAdapterFactory>", "adapterFactory"));
        Anchor createAdapter = Add(anchors, BindObjectCreation(createExecuteAdapter, "ExecuteTransactionProcessorAdapter", "di.createExecuteAdapter"));
        RequireExactDirectExecutorRegistration(executorRegistration);
        RequireCanonicalBinding(
            balManagerRegistration,
            "AddScoped<IBlockAccessListManager, BlockAccessListManager>()",
            "The DI route no longer registers the exact BlockAccessListManager.",
            allowReceiverPrefix: true);
        RequireCanonicalBinding(
            validationModuleRegistration,
            "AddSingleton<IBlockValidationModule, StandardBlockValidationModule>()",
            "The standard block-validation module registration changed.",
            allowReceiverPrefix: true);
        RequireSourceOrder(blockProcessorRegistration, executorRegistration, "The concrete base BlockProcessor registration must precede transaction executor registration.");
        RequireSourceOrder(balManagerRegistration, executorRegistration, "The BAL manager registration must precede transaction executor registration.");
        RequireFluentOrder(validationDiLoad, executorRegistration, decoratorRegistration,
            "The direct executor registration must precede the parallel decorator registration.");
        RequireCanonicalBinding(
            adapterFactoryRegistration,
            "AddScoped<TransactionProcessorAdapterFactory>(CreateExecuteAdapter)",
            "The standard transaction adapter factory registration changed.",
            allowReceiverPrefix: true);
        RequireCanonicalBinding(
            adapterRegistration,
            "AddScoped<ITransactionProcessorAdapter, ITransactionProcessor, TransactionProcessorAdapterFactory>(static (transactionProcessor, adapterFactory) => adapterFactory(transactionProcessor))",
            "The standard transaction adapter registration changed.",
            allowReceiverPrefix: true);
        RequireCanonicalBinding(
            createAdapter,
            "new ExecuteTransactionProcessorAdapter(transactionProcessor)",
            "The standard transaction adapter factory no longer constructs ExecuteTransactionProcessorAdapter.");
        RequireFluentOrder(diLoad, processorRegistration, adapterFactoryRegistration,
            "The Ethereum processor registration must precede its execute-adapter factory.");
        RequireFluentOrder(diLoad, adapterFactoryRegistration, adapterRegistration,
            "The execute-adapter factory must precede the interface adapter registration.");
        Anchor enabledMember = Add(anchors, BindMemberAccess(parallelExecutor, balProcessTransactions, "Enabled", "bal.enabledMember"));
        Anchor enabledSpec = Add(anchors, BindAssignment(balPrepare, "_blockAccessListsEnabled", "bal.enabledSpec"));
        Anchor enabledDerivation = Add(anchors, BindAssignment(balPrepare, "Enabled", "bal.enabledDerivation"));
        RequireCanonicalBinding(enabledSpec, "_blockAccessListsEnabled = spec.BlockLevelAccessListsEnabled", "BAL Enabled must derive from the release spec capability.");
        RequireCanonicalBinding(enabledDerivation, "Enabled = _blockAccessListsEnabled && !suggestedBlock.IsGenesis", "BAL Enabled must exclude genesis blocks after spec derivation.");
        RequireUnconditional(balPrepare, enabledSpec, "BAL release-spec capability derivation must be unconditional.");
        RequireUnconditional(balPrepare, enabledDerivation, "BAL Enabled derivation must be unconditional.");
        RequireSourceOrder(enabledSpec, enabledDerivation, "BAL Enabled must be derived after the release-spec capability.");
        RequireDominates(balPrepare, enabledSpec, enabledDerivation, "BAL release-spec capability must dominate BAL Enabled derivation.");
        Anchor parallelSelection = Add(anchors, BindConditional(balProcessTransactions, "balManager.ParallelExecutionEnabled", "bal.parallelSelection"));
        RequireCanonicalBinding(
            parallelSelection,
            "ExecutionFlags.ParallelExecution && !block.IsGenesis && balManager.ParallelExecutionEnabled",
            "The BAL parallel-selection guard changed its sequential fallback condition.");
        RequireReachable(balProcessTransactions, parallelSelection, "The BAL parallel-selection guard is not attached to a reachable CFG block.");
        RequireSourceOrder(balGuard, parallelSelection, "The BAL parallel-selection guard must follow the BAL-disabled direct-inner guard.");
        RequireDominates(balProcessTransactions, balGuard, parallelSelection, "The BAL-disabled guard must dominate parallel selection.");
        Anchor ethereumProcessorType = Add(anchors, BindDeclaration(processor, EthereumProcessorType, "route.ethereumProcessorType"));
        Anchor ethereumBaseType = Add(anchors, BindDeclaration(processor, EthereumProcessorBaseType, "route.ethereumBaseType"));
        Anchor genericBaseType = Add(anchors, BindDeclaration(processor, TransactionProcessorType, "route.genericBaseType", typeParameterCount: 1));
        RequireExactBaseRoute(processor);
        Anchor forkClass = Add(anchors, BindDeclaration(fork, AmsterdamType, "fork.amsterdam"));
        Anchor routePredicate = Add(anchors, BindMethodAnchor(BindMethod(routing, RoutingKernelType, "UseSystemProcessor", 2), "route.systemPredicate"));

        AddControlFlow(controlFlows, setContext, "cfg.setBlockExecutionContext", [resetExecutionGas, resetStateGas, installContext]);
        AddControlFlow(controlFlows, executeAdapterSetContext, "cfg.context.executeAdapter", [executeAdapterContext]);
        AddControlFlow(controlFlows, directExecutorSetContext, "cfg.context.directExecutor", [directExecutorContext]);
        AddControlFlow(controlFlows, decoratedSetContext, "cfg.context.decorator", [decoratedContextBal, decoratedContextInner]);
        AddControlFlow(controlFlows, balSetContext, "cfg.context.balManager", [balContextStore]);
        AddControlFlow(controlFlows, diLoad, "cfg.di.blockProcessing", [processorRegistration,
            adapterFactoryRegistration, adapterRegistration, worldRegistration,
            blockProcessorRegistration, balManagerRegistration, validationModuleRegistration]);
        AddControlFlow(controlFlows, validationDiLoad, "cfg.di.standardValidation",
            [executorRegistration, decoratorRegistration]);
        AddControlFlow(controlFlows, createExecuteAdapter, "cfg.di.createExecuteAdapter", [createAdapter]);
        AddControlFlow(controlFlows, executeAdapterMethod, "cfg.adapter.commitExecute", [adapterExecuteCommit]);
        AddControlFlow(controlFlows, commitExtension, "cfg.adapter.commitProcess", [commitProcess]);
        AddControlFlow(controlFlows, executeCore, "cfg.executeCore", [systemGuard, ordinaryExecute]);
        AddControlFlow(controlFlows, executeMachine, "cfg.execute", [staticValidation, incrementNonce, prepareFastPath, precommit, availableGas, simpleHandoff, evmHandoff]);
        AddControlFlow(controlFlows, fastPathCandidate, "cfg.fastPath.candidate", [fastPathCandidateBody]);
        AddControlFlow(controlFlows, noExecutableCode, "cfg.fastPath.noExecutableCode", [noExecutableCodeBody]);
        AddControlFlow(controlFlows, prepareFastPathMethod, "cfg.fastPath.prepare", [fastPathEntryGuard, fastPathCandidateCall, noExecutableCodeCall]);
        AddControlFlow(controlFlows, simpleTransfer, "cfg.executeSimpleTransfer", [recipientDeadCheck, stateCharge, payValue, recipientWrite, clearExecutionGas, simpleRefund, simpleAccess, updateHeaderFees, finalizeTransaction]);
        AddControlFlow(controlFlows, finalize, "cfg.finalizeTransaction", [finalizeCommit, receiptFailure, receiptSuccess, resultReturn]);
        AddControlFlow(controlFlows, startTxTrace, "cfg.adapter", [adapterStart, adapterExecute, adapterEnd]);
        AddControlFlow(controlFlows, processTransaction, "cfg.executor.processTransaction", [executorAdapterCall, executorResultGuard, executorInvalidThrow, executorProcessedEvent]);
        AddControlFlow(controlFlows, processTransactions, "cfg.executor.processTransactions", [transactionLoop, executorProcessCall, gasLimitGuard, gasLimitThrow]);
        AddControlFlow(controlFlows, balProcessTransactions, "cfg.balDecorator", [balGuard, directInnerCall]);
        AddControlFlow(controlFlows, startBlockTrace, "cfg.receipts.startBlock", [blockResetIndex, blockResetCurrentTx, blockResetTracer, blockClearReceipts, blockClearGasHistory, blockResetCumulative]);
        AddControlFlow(controlFlows, markSuccess, "cfg.receipts.markSuccess", [receiptAppend, receiptOtherGuard, receiptOther, receiptCurrentGuard, receiptCurrent]);
        AddControlFlow(controlFlows, endTracerTx, "cfg.receipts.endTx", [txEndDelegate, txIndexIncrement]);
        AddControlFlow(controlFlows, startTracerTx, "cfg.receipts.startTx", [txStartCurrent, txStartDelegate, txStartTracer]);
        AddControlFlow(controlFlows, processBlock, "cfg.block.processBlock", [blockStartTrace, createBlockContext, blockSetContext, blockPreCommit, blockFold, transactionsExecuted, blockPostCommit]);
        AddControlFlow(controlFlows, processOne, "cfg.block.processOne", [processOneBalPrepare, processOneBlockCall]);
        AddControlFlow(controlFlows, balPrepare, "cfg.bal.prepare", [enabledSpec, enabledDerivation]);
        AddControlFlow(controlFlows, balProcessTransactions, "cfg.bal.parallelSelection", [balGuard, parallelSelection, directInnerCall]);

        foreach (Anchor anchor in anchors) allBindings.Add(anchor.Binding);

        OptionShape optionShape = new(
            None: 0,
            Commit: 1,
            Restore: 2,
            SkipValidation: 4,
            Warmup: 8,
            BuildUp: 16,
            ExactTarget: "ExecutionOptions.Commit (raw = 1; no Restore/Warmup/BuildUp)",
            Binding: BindEnumMember(options, "Commit", "options.commit", expectedValue: 1));

        NormalReturnPremise Boundary(string id, Anchor anchor, string condition) =>
            new(
                id,
                anchor.Id,
                anchor.Binding.Path,
                anchor.Binding.Owner,
                anchor.Binding.Member,
                "normal-return",
                condition);

        NormalReturnPremise[] normalReturnPremises =
        [
            Boundary("context.blockRoute", blockSetContext, "ProcessBlock forwards the constructed block context normally"),
            Boundary("context.install", installContext, "SetBlockExecutionContext returns normally"),
            Boundary("context.executeAdapter", executeAdapterContext, "the execute adapter forwards the block context normally"),
            Boundary("context.directExecutor", directExecutorContext, "the direct executor forwards the block context normally"),
            Boundary("context.decoratorBal", decoratedContextBal, "the decorator installs the BAL block context normally"),
            Boundary("context.decoratorInner", decoratedContextInner, "the decorator forwards the block context to its inner executor normally"),
            Boundary("context.balStore", balContextStore, "the BAL manager stores the block context normally"),
            Boundary("adapter.commitExecute", adapterExecuteCommit, "ExecuteTransactionProcessorAdapter.Execute returns normally"),
            Boundary("adapter.commitProcess", commitProcess, "ITransactionProcessorExtensions.Execute returns the exact-Commit Process result"),
            Boundary("processor.core", processToCore, "Process returns the ordinary ExecuteCore result"),
            Boundary("processor.ordinary", ordinaryExecute, "ordinary ExecuteCore dispatch returns normally"),
            Boundary("processor.intrinsic", executeWithIntrinsic, "the cached-intrinsic Execute handoff returns normally"),
            Boundary("processor.staticAdmission", staticValidation, "ValidateStatic returns a TransactionResult"),
            Boundary("processor.senderAdmission", senderValidation, "ValidateSender returns a TransactionResult"),
            Boundary("processor.gasAdmission", buyGas, "BuyGas returns a TransactionResult"),
            Boundary("processor.nonce", incrementNonce, "IncrementNonce returns a TransactionResult"),
            Boundary("processor.prepareFastPath", prepareFastPath, "PrepareSimpleTransferFastPath returns normally"),
            Boundary("processor.preCommit", precommit, "the optional pre-execution WorldState.Commit returns normally"),
            Boundary("processor.availableGas", availableGas, "CalculateAvailableGas returns a TransactionResult"),
            Boundary("processor.simple", simpleHandoff, "the admitted simple-transfer branch returns normally"),
            Boundary("simple.refund", simpleRefund, "Refund returns settled gas accounting"),
            Boundary("simple.access", simpleAccess, "the optional simple-transfer access report returns normally"),
            Boundary("simple.headerAndFees", updateHeaderFees, "UpdateHeaderGasUsedAndPayFees returns normally"),
            Boundary("processor.finalize", finalizeTransaction, "FinalizeTransaction returns a TransactionResult"),
            Boundary("fees.payFees", payFees, "PayFees returns normally"),
            Boundary("finalize.commit", finalizeCommit, "the exact-Commit IWorldState.Commit returns normally"),
            Boundary("finalize.receiptSuccess", receiptSuccess, "MarkAsSuccess returns normally"),
            Boundary("adapter.start", adapterStart, "StartNewTxTrace returns a tracer"),
            Boundary("adapter.execute", adapterExecute, "the adapter Execute call returns a TransactionResult"),
            Boundary("adapter.end", adapterEnd, "EndTxTrace returns normally"),
            Boundary("executor.adapter", executorAdapterCall, "the direct executor adapter call returns normally"),
            Boundary("executor.processed", executorProcessedEvent, "the optional transaction-processed subscriber returns normally"),
            Boundary("block.start", blockStartTrace, "StartNewBlockTrace returns normally"),
            Boundary("block.preCommit", blockPreCommit, "the pre-fold CommitState returns normally"),
            Boundary("block.fold", blockFold, "ProcessTransactions returns the sequential receipt array"),
            Boundary("block.transactionsExecuted", transactionsExecuted, "the TransactionsExecuted subscriber invocation returns normally"),
            Boundary("block.postCommit", blockPostCommit, "the post-fold CommitState returns normally"),
            Boundary("block.balPrepare", processOneBalPrepare, "BlockAccessListManager.PrepareForProcessing returns normally"),
            Boundary("block.processBlock", processOneBlockCall, "the standard BlockProcessor.ProcessBlock call returns normally"),
            Boundary("receipt.append", receiptAppend, "BuildReceipt and receipt append return normally"),
            Boundary("receipt.other", receiptOther, "nested receipt forwarding returns normally"),
            Boundary("receipt.current", receiptCurrent, "current transaction receipt forwarding returns normally"),
            Boundary("receipt.gasUpdate", receiptGasUpdate, "UpdateCumulativeGasTracking returns cumulative receipt gas"),
            Boundary("receipt.endForward", txEndDelegate, "EndTxTrace forwards to the wrapped tracer"),
            Boundary("receipt.index", txIndexIncrement, "EndTxTrace increments the receipt index after forwarding"),
        ];

        anchors.Add(new Anchor("options.commit", "options.commit", optionShape.Binding, [], [], "enum-member-identity"));
        allBindings.Add(optionShape.Binding);

        Anchor AnchorFor(string id) => anchors.Single(anchor => anchor.Id == id);

        SemanticOperation Semantic(string id, string anchorId)
        {
            TypedBinding binding = AnchorFor(anchorId).Binding;
            return new(
                id,
                anchorId,
                binding.Path,
                binding.Owner,
                binding.Member,
                binding.OperationKind,
                binding.OperationType,
                binding.ReceiverType,
                binding.TargetSymbol,
                binding.CanonicalSyntax,
                binding.ReadInside,
                binding.WrittenInside,
                binding.ReadOutside,
                binding.WrittenOutside);
        }

        FieldwiseHandoff Seam(
            string id,
            string anchorId,
            string sourceField,
            string modelType,
            string modelField,
            string projection) =>
            new(id, anchorId, AnchorFor(anchorId).Binding.Member, sourceField, modelType, modelField, projection);

        SemanticOperation[] semanticOperations = anchors
            .Select(anchor => Semantic(anchor.Id, anchor.Id))
            .ToArray();
        FieldwiseHandoff[] fieldwiseSeams =
        [
            Seam("tx.sender", "process.toExecuteCore", "transaction.SenderAddress", "TerminalTransaction", "sender", "identity-preserving address oracle"),
            Seam("tx.recipient", "process.toExecuteCore", "transaction.To", "TerminalTransaction", "to", "identity-preserving recipient oracle"),
            Seam("tx.value", "process.toExecuteCore", "transaction.ValueRef", "TerminalTransaction", "value", "identity-preserving scalar observation"),
            Seam("tx.nonce", "admission.incrementNonce", "transaction.Nonce", "TerminalTransaction", "nonce", "identity-preserving scalar observation"),
            Seam("tx.type", "process.toExecuteCore", "transaction.Type", "TerminalTransaction", "txType", "identity-preserving scalar observation"),
            Seam("tx.gasLimit", "admission.intrinsic", "transaction.GasLimit", "TerminalTransaction", "gasLimit", "identity-preserving scalar observation"),
            Seam("tx.fees", "admission.buyGas", "transaction.MaxFeePerGas/MaxPriorityFeePerGas", "TerminalTransaction", "feeFields", "fieldwise fee projection"),
            Seam("route.codeOverridable", "dispatch.fastPathCandidate", "isCodeOverridable", "MachineInput", "isCodeOverridable", "exact Boolean fast-path guard"),
            Seam("route.authorizationList", "dispatch.fastPathCandidate", "tx.AuthorizationList", "MachineInput", "hasAuthorizationList", "null/non-null authorization-list projection"),
            Seam("route.forceSimpleTransferDisabled", "dispatch.fastPathCandidate", "ForceSimpleTransferDisabled", "MachineInput", "forceSimpleTransferDisabled", "exact Boolean fast-path guard"),
            Seam("header.gasUsed", "simple.headerAndFees", "header.GasUsed", "TerminalState", "headerGasUsed", "source-updated header gas observation"),
            Seam("gas.spent", "simple.refund", "spentGas.SpentGas", "TerminalGas", "spentGas", "fieldwise gas observation"),
            Seam("gas.operation", "simple.refund", "spentGas.OperationGas", "TerminalGas", "operationGas", "fieldwise gas observation"),
            Seam("gas.block", "simple.refund", "spentGas.EffectiveBlockGas", "TerminalGas", "blockGas", "fieldwise gas observation"),
            Seam("gas.state", "simple.refund", "spentGas.BlockStateGas", "TerminalGas", "blockStateGas", "fieldwise gas observation"),
            Seam("gas.maxUsed", "simple.refund", "spentGas.MaxUsedGas", "TerminalGas", "maxUsedGas", "fieldwise gas observation"),
            Seam("gas.refund", "simple.refund", "spentGas.GasRefund", "TerminalGas", "gasRefund", "fieldwise gas observation"),
            Seam("substate.output", "simple.finalize", "substate.Output", "TerminalBytes", "output", "opaque output oracle"),
            Seam("substate.logs", "simple.finalize", "substate.Logs", "TerminalLog", "logs", "opaque log oracle list"),
            Seam("substate.error", "simple.finalize", "substate.Error", "TerminalError", "error", "opaque error oracle"),
            Seam("substate.exception", "finalize.resultReturn", "substate.EvmExceptionType", "TerminalException", "exception", "constructor-specific exception oracle"),
            Seam("receipt.status", "tracer.receiptAppend", "StatusCode", "TerminalReceipt", "statusCode", "fieldwise receipt projection"),
            Seam("receipt.gas", "tracer.receiptAppend", "GasUsedTotal", "TerminalReceipt", "gasUsedTotal", "fieldwise cumulative receipt projection"),
            Seam("receipt.index", "tracer.receiptAppend", "Index", "TerminalReceipt", "index", "fieldwise receipt index projection"),
            Seam("receipt.recipient", "tracer.receiptAppend", "Recipient", "TerminalReceipt", "recipient", "fieldwise recipient projection"),
            Seam("receipt.logs", "tracer.receiptAppend", "Logs", "TerminalReceipt", "logs", "fieldwise log projection"),
            Seam("tracer.nestedForward", "tracer.nestedForwardGuard", "_otherTracer is ITxTracer", "SettledEntry", "nestedTracer", "typed forwarding-guard observation"),
            Seam("tracer.currentReceiptForward", "tracer.currentForwardGuard", "_currentTxTracer.IsTracingReceipt", "SettledEntry", "currentTxTracerIsTracingReceipt", "typed forwarding-guard observation"),
            Seam("result.constructor", "finalize.resultReturn", "TransactionResult", "TerminalResult", "constructor", "constructor-specific result projection"),
            Seam("result.exception", "finalize.resultReturn", "TransactionResult.EvmExceptionType", "TerminalException", "exception", "constructor-specific exception projection"),
            Seam("result.substateError", "finalize.resultReturn", "TransactionResult.SubstateError", "TerminalError", "substateError", "constructor-specific error projection"),
        ];

        RoutePremise route = new(
            ProcessorRegistration: "BlockProcessingModule.AddScoped<ITransactionProcessor, EthereumTransactionProcessor>",
            BaseType: "sealed EthereumTransactionProcessor : EthereumTransactionProcessorBase : TransactionProcessorBase<EthereumGasPolicy>",
            // The concrete BlockProcessor registration fixes the virtual ProcessBlock dispatch for this slice.
            ExecutorRegistration: "BlockProcessingModule.AddScoped<IBlockProcessor.IBlockTransactionsExecutor, BlockValidationTransactionsExecutor>",
            DirectInnerGuard: "!balManager.Enabled => return inner.ProcessTransactions(...) (sole direct true-arm return; BAL-enabled execution remains excluded)",
            BalEnabledMember: "BlockAccessListManager.Enabled = _blockAccessListsEnabled && !suggestedBlock.IsGenesis; IBlockAccessListManager.Enabled is the dispatched member",
            ParallelDecorator: "BlockProcessingModule.AddDecorator<IBlockProcessor.IBlockTransactionsExecutor, ParallelBlockValidationTransactionsExecutor>",
            ForkIdentity: "Nethermind.Specs.Forks.Amsterdam (25_Amsterdam.cs); chainspec equivalence is an external premise",
            BaseTracerType: "Nethermind.Blockchain.Tracing.BlockReceiptsTracer(parallel=false)",
            ExcludedRoutes:
            [
                "BAL-enabled decorator path",
                "parallel execution and worker tracer pool",
                "system transactions and exact SkipValidation route",
                "non-Ethereum processor/gas-policy overrides",
                "EVM frame/CREATE/delegation execution",
            ],
            Bindings: [processorRegistration, adapterFactoryRegistration, adapterRegistration, createAdapter,
                worldRegistration, blockProcessorRegistration, balManagerRegistration, executorRegistration,
                decoratorRegistration, enabledMember, enabledSpec, enabledDerivation, parallelSelection,
                ethereumProcessorType, ethereumBaseType, genericBaseType, forkClass, routePredicate,
                adapterExecuteCommit, commitProcess, blockSetContext, decoratedContextBal,
                decoratedContextInner, directExecutorContext, executeAdapterContext, balContextStore]);

        MachineShape machine = new(
            Entry: "sealed EthereumTransactionProcessor -> EthereumTransactionProcessorBase -> TransactionProcessorBase<EthereumGasPolicy>.Process(Transaction, ITxTracer, ExecutionOptions)",
            SuccessDomain: "OnlyOkTerminal adapter-supplied settled list: exact Commit; ordinary standard-mainnet; sequential non-BAL direct inner; non-create live recipient; fast path enabled; no authorization list, executable code or delegation; proof-carrying normal-return observations; EvmExceptionType.None",
            Stages:
            [
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
            ],
            Branches:
            [
                "system route excluded",
                "static/sender/gas/nonce invalid results excluded from successful theorem",
                "candidate and recipient/code/delegation guards select simple transfer",
                "available-gas false result is an explicit rejection, not success",
                "simple-transfer state OOG is excluded by live-recipient success premise",
                "EVM frame branch excluded",
            ],
            Effects:
            [
                "reset block cumulative execution/state gas",
                "recover sender and perform static/stateful admission",
                "increment sender nonce",
                "pay value then write live recipient balance",
                "settle gas and update header/fees",
                "commit exact-Commit state before receipt terminal",
                "append receipt and conditionally forward the success trace",
            ],
            Anchors: anchors.ToArray(),
            ControlFlows: controlFlows.ToArray(),
            Route: route,
            Options: optionShape,
            ExcludedBranches: ["ExecuteEvmTransaction", "BuildUp", "Restore", "SkipValidation-only system routing", "BAL/parallel"],
            NormalReturnPremises: normalReturnPremises,
            FieldwiseHandoffs:
            [
                "Transaction: sender, recipient, value, nonce, type, gas limit and fee fields",
                "Header: gas used and base-fee fields",
                "GasConsumed: spent, operation, block, state, max-used and refund fields",
                "Substate: output, logs, should-revert, error and exception fields",
                "ReceiptTerminal: status, cumulative gas, index, effective price, recipient and logs",
                "TransactionResult: constructor, exception type, substate error and description separately",
            ],
            SemanticOperations: semanticOperations,
            FieldwiseSeams: fieldwiseSeams,
            ReceiptTerminalSourceClosure: receiptTerminalSourceClosure);

        return machine;
    }

    private static SemanticUnit Unit(Dictionary<string, SemanticUnit> units, string path) =>
        units.TryGetValue(path, out SemanticUnit? unit)
            ? unit
            : throw new ExtractionException($"Missing semantic unit: {path}.");

    private static void ValidateIdentityLedgers(IReadOnlyDictionary<string, SemanticUnit> units)
    {
        if (SourceOwnerLedger.Any(entry => entry.Multiplicity != 1 || !units.ContainsKey(entry.SourcePath)) ||
            SourceOwnerLedger.GroupBy(static entry => entry.MetadataTypeName, StringComparer.Ordinal)
                .Any(group => group.Count() != 1))
        {
            throw new ExtractionException("The hard-coded source-owner identity ledger is malformed or duplicated.");
        }

        foreach (SourceOwnerIdentity identity in SourceOwnerLedger)
        {
            SemanticUnit unit = units[identity.SourcePath];
            INamedTypeSymbol owner = unit.Compilation.Assembly.GetTypeByMetadataName(identity.MetadataTypeName)
                ?? throw new ExtractionException(
                    $"Source-owner identity {identity.MetadataTypeName} is not declared by {identity.SourcePath}.");
            int ownedDeclarations = owner.DeclaringSyntaxReferences.Count(reference =>
                ReferenceEquals(reference.SyntaxTree, unit.Source.Tree));
            if (ownedDeclarations != identity.Multiplicity)
            {
                throw new ExtractionException(
                    $"Source-owner identity {identity.MetadataTypeName} has {ownedDeclarations} declarations in {identity.SourcePath}; expected {identity.Multiplicity}.");
            }
        }

        if (SourceMemberLedger.Any(entry => entry.Kind != LedgerMemberKind.Method || entry.Arity < 0 ||
                entry.Multiplicity != 1 || entry.ParameterSyntax.Length != entry.RefKinds.Length ||
                !units.ContainsKey(entry.SourcePath)) ||
            SourceMemberLedger.GroupBy(entry =>
                    $"{entry.SourcePath}\0{entry.MetadataTypeName}\0{entry.MemberName}\0{entry.CanonicalSyntax}",
                    StringComparer.Ordinal)
                .Any(group => group.Count() != 1))
        {
            throw new ExtractionException("The hard-coded source-member identity ledger is malformed or duplicated.");
        }

        foreach (IGrouping<string, SourceMemberIdentity> group in SourceMemberLedger.GroupBy(entry =>
                     $"{entry.SourcePath}\0{entry.MetadataTypeName}\0{entry.MemberName}\0{entry.Arity}\0{entry.ParameterSyntax.Length}",
                     StringComparer.Ordinal))
        {
            SourceMemberIdentity first = group.First();
            SemanticUnit unit = units[first.SourcePath];
            INamedTypeSymbol owner = ResolveExactType(unit.Compilation, first.MetadataTypeName,
                $"source-member ledger group {first.MemberName}");
            MethodDeclarationSyntax[] declarations = unit.Source.Root.DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .Where(method => method.Identifier.ValueText == first.MemberName &&
                    method.ParameterList.Parameters.Count == first.ParameterSyntax.Length)
                .Where(method => unit.Model.GetDeclaredSymbol(method) is IMethodSymbol symbol &&
                    symbol.Arity == first.Arity && SymbolEqualityComparer.Default.Equals(symbol.ContainingType, owner))
                .ToArray();
            string[] admitted = group.Select(static entry => entry.CanonicalSyntax).ToArray();
            if (declarations.Length != admitted.Length || declarations.Any(method =>
                    !admitted.Contains(CanonicalMethodSignature(method), StringComparer.Ordinal)))
            {
                throw new ExtractionException(
                    $"Source-member identity ledger does not exactly cover {first.SourcePath}:{first.MetadataTypeName}.{first.MemberName}/{first.ParameterSyntax.Length}.");
            }

            int simpleNameShadows = unit.Source.Root.DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .Where(method => method.Identifier.ValueText == first.MemberName &&
                    method.ParameterList.Parameters.Count == first.ParameterSyntax.Length)
                .Select(method => unit.Model.GetDeclaredSymbol(method))
                .OfType<IMethodSymbol>()
                .Count(symbol => symbol.Arity == first.Arity &&
                    string.Equals(symbol.ContainingType?.Name, owner.Name, StringComparison.Ordinal) &&
                    !SymbolEqualityComparer.Default.Equals(symbol.ContainingType, owner));
            if (simpleNameShadows != 0)
            {
                throw new ExtractionException(
                    $"Source-member identity ledger rejected {simpleNameShadows} simple-name shadow(s) for {first.MetadataTypeName}.{first.MemberName}.");
            }

            foreach (SourceMemberIdentity identity in group)
            {
                MethodDeclarationSyntax declaration = declarations.Single(method =>
                    string.Equals(CanonicalMethodSignature(method), identity.CanonicalSyntax, StringComparison.Ordinal));
                IMethodSymbol symbol = unit.Model.GetDeclaredSymbol(declaration)
                    ?? throw new ExtractionException($"Source-member ledger has no symbol for {identity.MetadataTypeName}.{identity.MemberName}.");
                if (symbol.Arity != identity.Arity ||
                    !SignatureMatches(symbol, identity.ParameterSyntax, identity.RefKinds))
                {
                    throw new ExtractionException(
                        $"Source-member signature/ref-kind identity mismatch for {identity.MetadataTypeName}.{identity.MemberName}.");
                }
            }
        }

        if (InvocationTargetLedger.Any(entry => entry.Kind is not (LedgerMemberKind.Method or LedgerMemberKind.LocalFunction or LedgerMemberKind.DelegateInvoke) || entry.Arity < 0 ||
                entry.ParameterCount < 0 || entry.Multiplicity != 1 ||
                entry.ParameterCount != entry.RefKinds.Length ||
                entry.ParameterTypeMetadataNames is not null &&
                entry.ParameterCount != entry.ParameterTypeMetadataNames.Length ||
                entry.TypeArgumentMetadataNames is not null &&
                entry.Arity != entry.TypeArgumentMetadataNames.Length) ||
            InvocationTargetLedger.GroupBy(static entry => entry.Id, StringComparer.Ordinal)
                .Any(group => group.Count() != 1) ||
            DeclarationLedger.Any(entry => entry.Multiplicity != 1 || !units.ContainsKey(entry.SourcePath) ||
                (entry.TargetParameterMetadataTypeNames is null) != (entry.TargetRefKinds is null) ||
                entry.TargetParameterMetadataTypeNames is not null &&
                entry.TargetParameterMetadataTypeNames.Length != entry.TargetRefKinds!.Length) ||
            DeclarationLedger.GroupBy(static entry => entry.Id, StringComparer.Ordinal)
                .Any(group => group.Count() != 1) ||
            AssignmentTargetLedger.Any(static entry => entry.Multiplicity != 1) ||
            AssignmentTargetLedger.GroupBy(static entry => entry.Id, StringComparer.Ordinal)
                .Any(group => group.Count() != 1) ||
            MemberTargetLedger.Any(static entry => entry.Multiplicity != 1) ||
            MemberTargetLedger.GroupBy(static entry => entry.Id, StringComparer.Ordinal)
                .Any(group => group.Count() != 1))
        {
            throw new ExtractionException("The hard-coded target/declaration identity ledger is malformed or duplicated.");
        }


        IEnumerable<string> referencedTypes = SourceMemberLedger.Select(static entry => entry.MetadataTypeName)
            .Concat(InvocationTargetLedger.Select(static entry => entry.MetadataTypeName))
            .Concat(InvocationTargetLedger.Select(static entry => entry.ReceiverMetadataTypeName)
                .OfType<string>())
            .Concat(InvocationTargetLedger.SelectMany(static entry =>
                entry.ParameterTypeMetadataNames ?? Array.Empty<string>()))
            .Concat(InvocationTargetLedger.SelectMany(static entry =>
                entry.TypeArgumentMetadataNames ?? Array.Empty<string>()))
            .Concat(DeclarationLedger.Select(static entry => entry.MetadataTypeName))
            .Concat(DeclarationLedger.Select(static entry => entry.TargetMetadataTypeName)
                .OfType<string>())
            .Concat(DeclarationLedger.SelectMany(static entry =>
                entry.TargetParameterMetadataTypeNames ?? Array.Empty<string>()))
            .Concat(AssignmentTargetLedger.Select(static entry => entry.MetadataTypeName))
            .Concat(MemberTargetLedger.SelectMany(static entry =>
                new[] { entry.MetadataTypeName, entry.ReceiverMetadataTypeName }));
        HashSet<string> sourceOwnedTypes = SourceOwnerLedger
            .Select(static entry => entry.MetadataTypeName)
            .ToHashSet(StringComparer.Ordinal);
        string[] missingOwners = referencedTypes.Distinct(StringComparer.Ordinal)
            .Where(name => !sourceOwnedTypes.Contains(name) && !CompilerOwnedTargetTypes.Contains(name))
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();
        if (missingOwners.Length != 0)
        {
            throw new ExtractionException(
                $"The source-owner identity ledger does not cover: {string.Join(", ", missingOwners)}.");
        }

        string[] unboundSourceTargets = InvocationTargetLedger
            .Where(entry => entry.Kind == LedgerMemberKind.Method &&
                !CompilerOwnedTargetTypes.Contains(entry.MetadataTypeName) &&
                !SourceMemberLedger.Any(member =>
                    string.Equals(member.MetadataTypeName, entry.MetadataTypeName, StringComparison.Ordinal) &&
                    string.Equals(member.MemberName, entry.MemberName, StringComparison.Ordinal) &&
                    member.Arity == entry.Arity && member.ParameterSyntax.Length == entry.ParameterCount &&
                    member.RefKinds.SequenceEqual(entry.RefKinds, StringComparer.Ordinal)))
            .Select(static entry => entry.Id)
            .OrderBy(static id => id, StringComparer.Ordinal)
            .ToArray();
        if (unboundSourceTargets.Length != 0)
        {
            throw new ExtractionException(
                $"The source-member identity ledger does not bind invocation targets: {string.Join(", ", unboundSourceTargets)}.");
        }
    }

    private static SourceMemberIdentity Method(
        string sourcePath,
        string metadataTypeName,
        string memberName,
        string canonicalSyntax,
        string[] parameterSyntax,
        string[]? refKinds = null,
        int arity = 0,
        int multiplicity = 1) =>
        new(sourcePath, metadataTypeName, memberName, LedgerMemberKind.Method, arity,
            parameterSyntax, refKinds ?? Enumerable.Repeat("none", parameterSyntax.Length).ToArray(),
            canonicalSyntax, multiplicity);

    private static InvocationTargetIdentity Target(
        string id,
        string metadataTypeName,
        string memberName,
        int parameterCount,
        string? receiver = null,
        string[]? refKinds = null,
        int arity = 0,
        int multiplicity = 1,
        LedgerMemberKind kind = LedgerMemberKind.Method,
        string? canonicalSyntax = null,
        string[]? parameterTypeMetadataNames = null,
        string[]? typeArgumentMetadataNames = null) =>
        new(id, metadataTypeName, memberName, kind, arity, parameterCount,
            refKinds ?? Enumerable.Repeat("none", parameterCount).ToArray(), receiver, multiplicity,
            canonicalSyntax, parameterTypeMetadataNames, typeArgumentMetadataNames);

    private static DeclarationIdentity Declaration(
        string id,
        SemanticUnit unit,
        LedgerMemberKind kind)
    {
        DeclarationIdentity[] matches = DeclarationLedger.Where(entry =>
            string.Equals(entry.Id, id, StringComparison.Ordinal) &&
            string.Equals(entry.SourcePath, unit.Source.RelativePath, StringComparison.Ordinal) &&
            entry.Kind == kind).ToArray();
        if (matches.Length != 1)
            throw new ExtractionException($"Declaration identity ledger has {matches.Length} entries for {id} in {unit.Source.RelativePath}.");
        return matches[0];
    }

    private static INamedTypeSymbol ResolveExactType(
        CSharpCompilation compilation,
        string metadataTypeName,
        string context)
    {
        INamedTypeSymbol? sourceType = compilation.Assembly.GetTypeByMetadataName(metadataTypeName);
        if (sourceType is not null) return sourceType;

        List<INamedTypeSymbol> matches = [];
        foreach (IAssemblySymbol assembly in compilation.SourceModule.ReferencedAssemblySymbols)
        {
            INamedTypeSymbol? candidate = assembly.GetTypeByMetadataName(metadataTypeName);
            if (candidate is not null && !matches.Any(existing =>
                    SymbolEqualityComparer.Default.Equals(existing, candidate)))
            {
                matches.Add(candidate);
            }
        }

        if (matches.Count != 1)
            throw new ExtractionException($"Exact metadata type identity {metadataTypeName} for {context} resolved {matches.Count} times.");
        return matches[0];
    }

    private static ITypeSymbol OriginalDefinition(ITypeSymbol type) =>
        type is INamedTypeSymbol named ? named.OriginalDefinition : type;

    private static bool SignatureMatches(
        IMethodSymbol method,
        IReadOnlyList<string> parameterSyntax,
        IReadOnlyList<string> refKinds)
    {
        if (method.Parameters.Length != parameterSyntax.Count || refKinds.Count != parameterSyntax.Count ||
            !method.Parameters.Select(static parameter => RefKindName(parameter.RefKind))
                .SequenceEqual(refKinds, StringComparer.Ordinal))
        {
            return false;
        }

        return method.DeclaringSyntaxReferences
            .Select(static reference => reference.GetSyntax())
            .OfType<MethodDeclarationSyntax>()
            .Any(syntax => syntax.ParameterList.Parameters.Select(parameter =>
                    Canonical(parameter.Type ?? throw new ExtractionException("A source-ledger method parameter has no type syntax.")))
                .SequenceEqual(parameterSyntax, StringComparer.Ordinal));
    }

    private static string RefKindName(RefKind refKind) => refKind switch
    {
        RefKind.None => "none",
        RefKind.Ref => "ref",
        RefKind.Out => "out",
        RefKind.In => "in",
        _ => refKind.ToString().ToLowerInvariant(),
    };

    private static string CanonicalMethodSignature(MethodDeclarationSyntax method) =>
        string.Concat(method.Modifiers.Select(static modifier => modifier.Text)) +
        Canonical(method.ReturnType) +
        (method.ExplicitInterfaceSpecifier is null ? string.Empty : Canonical(method.ExplicitInterfaceSpecifier)) +
        method.Identifier.Text +
        (method.TypeParameterList is null ? string.Empty : Canonical(method.TypeParameterList)) +
        Canonical(method.ParameterList);

    private static string CanonicalTypeIdentity(TypeDeclarationSyntax declaration) =>
        string.Concat(declaration.Modifiers.Select(static modifier => modifier.Text)) +
        declaration.Keyword.Text + declaration.Identifier.Text +
        (declaration.TypeParameterList is null ? string.Empty : Canonical(declaration.TypeParameterList));

    private static MethodHandle BindMethod(SemanticUnit unit, string ownerMetadataName, string memberName, int parameterCount, string? parameterType = null)
    {
        SourceMemberIdentity[] ledgerMatches = SourceMemberLedger
            .Where(entry => string.Equals(entry.SourcePath, unit.Source.RelativePath, StringComparison.Ordinal) &&
                string.Equals(entry.MetadataTypeName, ownerMetadataName, StringComparison.Ordinal) &&
                string.Equals(entry.MemberName, memberName, StringComparison.Ordinal) &&
                entry.ParameterSyntax.Length == parameterCount &&
                (parameterType is null || entry.ParameterSyntax.Any(type =>
                    type.Contains(parameterType, StringComparison.Ordinal))))
            .ToArray();
        if (ledgerMatches.Length != 1)
        {
            throw new ExtractionException(
                $"Source-member identity ledger has {ledgerMatches.Length} entries for {unit.Source.RelativePath}:{ownerMetadataName}.{memberName}/{parameterCount}.");
        }

        SourceMemberIdentity identity = ledgerMatches[0];
        INamedTypeSymbol owner = ResolveExactType(unit.Compilation, identity.MetadataTypeName,
            $"source-member identity {identity.MetadataTypeName}.{identity.MemberName}");
        int simpleNameShadows = unit.Source.Root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Where(method => method.Identifier.ValueText == memberName && method.ParameterList.Parameters.Count == parameterCount)
            .Select(method => unit.Model.GetDeclaredSymbol(method))
            .OfType<IMethodSymbol>()
            .Count(symbol => string.Equals(symbol.ContainingType?.Name, owner.Name, StringComparison.Ordinal) &&
                !SymbolEqualityComparer.Default.Equals(symbol.ContainingType, owner));
        if (simpleNameShadows != 0)
        {
            throw new ExtractionException(
                $"Source-member identity rejected {simpleNameShadows} simple-name shadow(s) for {identity.MetadataTypeName}.{identity.MemberName}.");
        }

        MethodDeclarationSyntax[] ownerDeclarations = unit.Source.Root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Where(method => method.Identifier.ValueText == memberName && method.ParameterList.Parameters.Count == parameterCount)
            .Where(method => unit.Model.GetDeclaredSymbol(method) is IMethodSymbol symbol &&
                SymbolEqualityComparer.Default.Equals(symbol.ContainingType, owner))
            .ToArray();
        string[] admittedCanonicalSignatures = SourceMemberLedger
            .Where(entry => string.Equals(entry.SourcePath, unit.Source.RelativePath, StringComparison.Ordinal) &&
                string.Equals(entry.MetadataTypeName, ownerMetadataName, StringComparison.Ordinal) &&
                string.Equals(entry.MemberName, memberName, StringComparison.Ordinal) &&
                entry.ParameterSyntax.Length == parameterCount)
            .Select(static entry => entry.CanonicalSyntax)
            .ToArray();
        if (ownerDeclarations.Any(method => !admittedCanonicalSignatures.Contains(
                CanonicalMethodSignature(method), StringComparer.Ordinal)))
        {
            throw new ExtractionException(
                $"Source-member identity rejected an unledgered competing declaration for {identity.MetadataTypeName}.{identity.MemberName}/{parameterCount}.");
        }

        MethodDeclarationSyntax[] candidates = unit.Source.Root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Where(method => method.Identifier.ValueText == memberName && method.ParameterList.Parameters.Count == parameterCount)
            .Where(method => unit.Model.GetDeclaredSymbol(method) is IMethodSymbol symbol &&
                SymbolEqualityComparer.Default.Equals(symbol.ContainingType, owner))
            .Where(method => string.Equals(CanonicalMethodSignature(method), identity.CanonicalSyntax, StringComparison.Ordinal))
            .ToArray();
        if (candidates.Length != identity.Multiplicity)
        {
            throw new ExtractionException(
                $"Source-member identity mismatch for {identity.SourcePath}:{identity.MetadataTypeName}.{identity.MemberName}; expected exact canonical multiplicity {identity.Multiplicity}, found {candidates.Length}.");
        }

        MethodDeclarationSyntax method = candidates[0];
        if (method.Ancestors().Any(static node => node is LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax))
        {
            throw new ExtractionException($"Local-function or lambda method owner rejected: {ownerMetadataName}.{memberName}.");
        }

        IMethodSymbol symbol = unit.Model.GetDeclaredSymbol(method)
            ?? throw new ExtractionException($"No declared symbol for {ownerMetadataName}.{memberName}.");
        RejectSymbol(symbol, $"method {ownerMetadataName}.{memberName}");
        IMethodSymbol[] exactMembers = owner.GetMembers(identity.MemberName)
            .OfType<IMethodSymbol>()
            .Where(candidate => candidate.MethodKind == MethodKind.Ordinary && candidate.Arity == identity.Arity &&
                SignatureMatches(candidate, identity.ParameterSyntax, identity.RefKinds) &&
                candidate.DeclaringSyntaxReferences.Any(reference =>
                    ReferenceEquals(reference.SyntaxTree, unit.Source.Tree) && reference.Span == method.Span))
            .ToArray();
        if (symbol.MethodKind != MethodKind.Ordinary || symbol.Arity != identity.Arity ||
            !SymbolEqualityComparer.Default.Equals(symbol.ContainingType, owner) ||
            exactMembers.Length != identity.Multiplicity ||
            !SymbolEqualityComparer.Default.Equals(symbol, exactMembers[0]))
        {
            throw new ExtractionException(
                $"Source-member symbol identity mismatch for {identity.SourcePath}:{identity.MetadataTypeName}.{identity.MemberName}.");
        }

        return new MethodHandle(unit, method, symbol);
    }

    private static TypedBinding BindEnumMember(SemanticUnit unit, string memberName, string id, int? expectedValue = null)
    {
        DeclarationIdentity identity = Declaration(id, unit, LedgerMemberKind.EnumMember);
        INamedTypeSymbol owner = ResolveExactType(unit.Compilation, identity.MetadataTypeName,
            $"enum-member identity {id}");
        int simpleNameShadows = unit.Source.Root.DescendantNodes()
            .OfType<EnumMemberDeclarationSyntax>()
            .Where(node => node.Identifier.ValueText == memberName)
            .Select(node => unit.Model.GetDeclaredSymbol(node))
            .OfType<IFieldSymbol>()
            .Count(symbol => string.Equals(symbol.ContainingType?.Name, owner.Name, StringComparison.Ordinal) &&
                !SymbolEqualityComparer.Default.Equals(symbol.ContainingType, owner));
        if (simpleNameShadows != 0)
            throw new ExtractionException($"Enum-member identity rejected {simpleNameShadows} simple-name shadow(s) for {id}.");
        EnumMemberDeclarationSyntax member = unit.Source.Root.DescendantNodes()
            .OfType<EnumMemberDeclarationSyntax>()
            .Where(node => node.Identifier.ValueText == memberName &&
                string.Equals(Canonical(node), identity.CanonicalSyntax, StringComparison.Ordinal) &&
                unit.Model.GetDeclaredSymbol(node) is IFieldSymbol field &&
                SymbolEqualityComparer.Default.Equals(field.ContainingType, owner))
            .SingleOrDefault()
            ?? throw new ExtractionException($"Enum-member identity mismatch for {id}.");
        IFieldSymbol symbol = unit.Model.GetDeclaredSymbol(member) as IFieldSymbol
            ?? throw new ExtractionException($"No declared enum symbol for {memberName}.");
        IFieldSymbol[] exactMembers = owner.GetMembers(memberName).OfType<IFieldSymbol>()
            .Where(field => field.HasConstantValue && field.DeclaringSyntaxReferences.Any(reference =>
                ReferenceEquals(reference.SyntaxTree, unit.Source.Tree) && reference.Span == member.Span))
            .ToArray();
        if (exactMembers.Length != identity.Multiplicity ||
            !SymbolEqualityComparer.Default.Equals(symbol, exactMembers[0]))
        {
            throw new ExtractionException($"Enum-member symbol identity mismatch for {id}.");
        }

        if (expectedValue is not null)
        {
            if (!Equals(symbol.ConstantValue, expectedValue.Value))
            {
                throw new ExtractionException($"Enum member {memberName} has a changed constant value.");
            }
        }

        return BindNode(unit, member, id, null);
    }

    private static Anchor BindDeclaration(SemanticUnit unit, string metadataTypeName, string id, int? typeParameterCount = null)
    {
        DeclarationIdentity identity = Declaration(id, unit, LedgerMemberKind.Type);
        if (!string.Equals(identity.MetadataTypeName, metadataTypeName, StringComparison.Ordinal))
            throw new ExtractionException($"Declaration ledger owner mismatch for {id}.");
        INamedTypeSymbol expected = ResolveExactType(unit.Compilation, metadataTypeName, $"declaration identity {id}");
        int simpleNameShadows = unit.Source.Root.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .Select(node => unit.Model.GetDeclaredSymbol(node))
            .OfType<INamedTypeSymbol>()
            .Count(symbol => string.Equals(symbol.Name, expected.Name, StringComparison.Ordinal) &&
                symbol.Arity == expected.Arity && !SymbolEqualityComparer.Default.Equals(symbol, expected));
        if (simpleNameShadows != 0)
            throw new ExtractionException($"Declaration identity rejected {simpleNameShadows} simple-name shadow(s) for {id}.");
        TypeDeclarationSyntax declaration = unit.Source.Root.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .Where(node => unit.Model.GetDeclaredSymbol(node) is INamedTypeSymbol symbol &&
                SymbolEqualityComparer.Default.Equals(symbol, expected))
            .Where(node => typeParameterCount is null || node.TypeParameterList?.Parameters.Count == typeParameterCount)
            .Where(node => string.Equals(CanonicalTypeIdentity(node), identity.CanonicalSyntax, StringComparison.Ordinal))
            .SingleOrDefault()
            ?? throw new ExtractionException($"Declaration identity mismatch for {id}.");
        if (expected.DeclaringSyntaxReferences.Count(reference =>
                ReferenceEquals(reference.SyntaxTree, unit.Source.Tree) && reference.Span == declaration.Span) != identity.Multiplicity)
            throw new ExtractionException($"Declaration source ownership mismatch for {id}.");
        return new Anchor(id, "declaration", BindNode(unit, declaration, id, null), [], [], "declaration-identity");
    }

    private static Anchor BindMethodAnchor(MethodHandle method, string id) =>
        new(id, "method-declaration", BindNode(method.Unit, method.Syntax, id, null), [], [], "declared-method-identity");

    private static Anchor BindExpressionBody(MethodHandle method, string id)
    {
        ExpressionSyntax expression = method.Syntax.ExpressionBody?.Expression
            ?? throw new ExtractionException($"Method {method.Symbol.Name} has no expression body for {id}.");
        RejectNestedFunctionOwner(expression, id);
        return new Anchor(id, "expression-body", BindNode(method.Unit, expression, id, method), [], [], "typed-expression-body");
    }

    private static Anchor BindObjectCreation(MethodHandle method, string typeName, string id)
    {
        DeclarationIdentity identity = Declaration(id, method.Unit, LedgerMemberKind.Constructor);
        INamedTypeSymbol expectedType = ResolveExactType(method.Unit.Compilation,
            identity.TargetMetadataTypeName ?? throw new ExtractionException($"Constructor target ledger is incomplete for {id}."),
            $"constructor identity {id}");
        ObjectCreationExpressionSyntax[] creations = method.Syntax.DescendantNodes()
            .OfType<ObjectCreationExpressionSyntax>()
            .Where(creation => method.Unit.Model.GetOperation(creation) is IObjectCreationOperation operation &&
                operation.Constructor is not null &&
                SymbolEqualityComparer.Default.Equals(operation.Constructor.ContainingType, expectedType) &&
                string.Equals(Canonical(creation), identity.CanonicalSyntax, StringComparison.Ordinal))
            .ToArray();
        if (creations.Length != identity.Multiplicity)
        {
            throw new ExtractionException(
                $"Constructor identity mismatch for {typeName} in {method.Symbol.Name} for {id}; found {creations.Length}.");
        }

        RejectNestedFunctionOwner(creations[0], id);
        IObjectCreationOperation operation = (IObjectCreationOperation)method.Unit.Model.GetOperation(creations[0])!;
        IMethodSymbol[] constructors = expectedType.InstanceConstructors
            .Where(constructor => constructor.Arity == 0 &&
                ConstructorSignatureMatches(method.Unit.Compilation, constructor, identity))
            .ToArray();
        if (constructors.Length != 1 || operation.Constructor is null ||
            !SymbolEqualityComparer.Default.Equals(operation.Constructor.OriginalDefinition, constructors[0].OriginalDefinition))
            throw new ExtractionException($"Constructor symbol identity mismatch for {id}.");
        return new Anchor(id, "object-creation", BindNode(method.Unit, creations[0], id, method), [], [], "typed-object-creation");
    }

    private static bool ConstructorSignatureMatches(
        CSharpCompilation compilation,
        IMethodSymbol constructor,
        DeclarationIdentity identity)
    {
        string[] parameterTypes = identity.TargetParameterMetadataTypeNames
            ?? throw new ExtractionException($"Constructor parameter-type ledger is incomplete for {identity.Id}.");
        string[] refKinds = identity.TargetRefKinds
            ?? throw new ExtractionException($"Constructor ref-kind ledger is incomplete for {identity.Id}.");
        if (constructor.Parameters.Length != parameterTypes.Length ||
            !constructor.Parameters.Select(static parameter => RefKindName(parameter.RefKind))
                .SequenceEqual(refKinds, StringComparer.Ordinal))
        {
            return false;
        }

        for (int i = 0; i < constructor.Parameters.Length; i++)
        {
            INamedTypeSymbol expectedParameterType = ResolveExactType(compilation, parameterTypes[i],
                $"constructor parameter {identity.Id}[{i}]");
            if (!SymbolEqualityComparer.Default.Equals(OriginalDefinition(constructor.Parameters[i].Type),
                    expectedParameterType))
            {
                return false;
            }
        }

        return true;
    }

    private static Anchor BindPrimaryConstructorParameter(
        SemanticUnit unit,
        string metadataTypeName,
        string parameterName,
        string expectedType,
        string id)
    {
        DeclarationIdentity identity = Declaration(id, unit, LedgerMemberKind.PrimaryConstructorParameter);
        if (!string.Equals(identity.MetadataTypeName, metadataTypeName, StringComparison.Ordinal))
            throw new ExtractionException($"Primary-constructor owner identity mismatch for {id}.");
        INamedTypeSymbol expectedOwner = ResolveExactType(unit.Compilation, metadataTypeName,
            $"primary-constructor identity {id}");
        TypeDeclarationSyntax declaration = unit.Source.Root.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .SingleOrDefault(node => unit.Model.GetDeclaredSymbol(node) is INamedTypeSymbol symbol &&
                SymbolEqualityComparer.Default.Equals(symbol, expectedOwner))
            ?? throw new ExtractionException($"Missing exact route type {metadataTypeName} for {id}.");
        ParameterSyntax[] parameters = declaration.ChildNodes()
            .OfType<ParameterListSyntax>()
            .SelectMany(static list => list.Parameters)
            .Where(parameter => parameter.Identifier.ValueText == parameterName)
            .ToArray();
        if (parameters.Length != 1 || parameters[0].Type is null)
        {
            throw new ExtractionException(
                $"Expected one primary-constructor parameter {parameterName} on {metadataTypeName} for {id}.");
        }

        ParameterSyntax parameter = parameters[0];
        IParameterSymbol symbol = unit.Model.GetDeclaredSymbol(parameter)
            ?? throw new ExtractionException($"No declared parameter symbol for {id}.");
        RejectSymbol(symbol, $"binding {id}");
        INamedTypeSymbol expectedParameterType = ResolveExactType(unit.Compilation,
            identity.TargetMetadataTypeName ?? throw new ExtractionException($"Primary-constructor type ledger is incomplete for {id}."),
            $"primary-constructor parameter type {id}");
        if (!SymbolEqualityComparer.Default.Equals(symbol.ContainingType, expectedOwner) ||
            !SymbolEqualityComparer.Default.Equals(OriginalDefinition(symbol.Type), expectedParameterType) ||
            !string.Equals(Canonical(parameter), identity.CanonicalSyntax, StringComparison.Ordinal) ||
            !string.Equals(Canonical(parameter.Type), CanonicalText(expectedType), StringComparison.Ordinal))
        {
            throw new ExtractionException(
                $"Primary-constructor parameter {parameterName} on {metadataTypeName} no longer has exact symbol/type {expectedType}.");
        }

        return new Anchor(id, "primary-constructor-parameter", BindNode(unit, parameter, id, null),
            [], [], "typed-primary-constructor-parameter");
    }

    private static Anchor BindFieldInitializer(
        SemanticUnit unit,
        string metadataTypeName,
        string fieldName,
        string expected,
        string id)
    {
        DeclarationIdentity identity = Declaration(id, unit, LedgerMemberKind.Field);
        if (!string.Equals(identity.MetadataTypeName, metadataTypeName, StringComparison.Ordinal))
            throw new ExtractionException($"Field owner identity mismatch for {id}.");
        INamedTypeSymbol expectedOwner = ResolveExactType(unit.Compilation, metadataTypeName, $"field identity {id}");
        TypeDeclarationSyntax declaration = unit.Source.Root.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .SingleOrDefault(node => unit.Model.GetDeclaredSymbol(node) is INamedTypeSymbol symbol &&
                SymbolEqualityComparer.Default.Equals(symbol, expectedOwner))
            ?? throw new ExtractionException($"Missing exact route type {metadataTypeName} for {id}.");
        VariableDeclaratorSyntax[] variables = declaration.Members
            .OfType<FieldDeclarationSyntax>()
            .SelectMany(static field => field.Declaration.Variables)
            .Where(variable => variable.Identifier.ValueText == fieldName)
            .ToArray();
        if (variables.Length != 1 || variables[0].Initializer is null)
        {
            throw new ExtractionException($"Expected one initialized field {fieldName} on {metadataTypeName} for {id}.");
        }

        VariableDeclaratorSyntax variable = variables[0];
        IFieldSymbol symbol = unit.Model.GetDeclaredSymbol(variable)
            ?? throw new ExtractionException($"No declared field symbol for {id}.");
        RejectSymbol(symbol, $"binding {id}");
        IFieldSymbol[] exactFields = expectedOwner.GetMembers(fieldName).OfType<IFieldSymbol>()
            .Where(field => field.DeclaringSyntaxReferences.Any(reference =>
                ReferenceEquals(reference.SyntaxTree, unit.Source.Tree) && reference.Span == variable.Span))
            .ToArray();
        if (!SymbolEqualityComparer.Default.Equals(symbol.ContainingType, expectedOwner) ||
            exactFields.Length != identity.Multiplicity ||
            !SymbolEqualityComparer.Default.Equals(symbol, exactFields[0]) ||
            !string.Equals(Canonical(variable), identity.CanonicalSyntax, StringComparison.Ordinal) ||
            !string.Equals(Canonical(variable), CanonicalText(expected), StringComparison.Ordinal))
        {
            throw new ExtractionException($"Field initializer {fieldName} on {metadataTypeName} changed identity.");
        }

        return new Anchor(id, "field-initializer", BindNode(unit, variable, id, null),
            [], [], "typed-field-initializer");
    }

    private static void RequireExactBaseRoute(SemanticUnit unit)
    {
        INamedTypeSymbol ethereumProcessor = ResolveExactType(unit.Compilation, EthereumProcessorType, "Ethereum processor base route");
        INamedTypeSymbol ethereumBase = ResolveExactType(unit.Compilation, EthereumProcessorBaseType, "Ethereum processor-base route");
        INamedTypeSymbol genericBase = ResolveExactType(unit.Compilation, TransactionProcessorType, "generic processor-base route");
        INamedTypeSymbol ethereumGasPolicy = ResolveExactType(unit.Compilation,
            "Nethermind.Evm.GasPolicy.EthereumGasPolicy", "Ethereum gas-policy base argument");

        RequireCanonicalBaseType(unit, ethereumProcessor, "EthereumTransactionProcessorBase",
            "The standard Ethereum processor base syntax changed identity.");
        RequireCanonicalBaseType(unit, ethereumBase, "TransactionProcessorBase<EthereumGasPolicy>",
            "Base-type identity mismatch for route.ethereumGasPolicyBase: expected TransactionProcessorBase<EthereumGasPolicy>.");

        if (!ethereumProcessor.IsSealed || !BaseTypeIs(ethereumProcessor, ethereumBase))
        {
            throw new ExtractionException("The standard Ethereum transaction processor no longer has the exact sealed base route.");
        }

        if (!ethereumBase.IsAbstract || !BaseTypeIs(ethereumBase, genericBase, ethereumGasPolicy))
        {
            throw new ExtractionException("The standard Ethereum transaction processor base no longer binds the exact EthereumGasPolicy base.");
        }

        if (!genericBase.IsAbstract)
        {
            throw new ExtractionException("The generic transaction processor base is no longer abstract.");
        }
    }

    private static void RequireExactDirectExecutorRoute(MethodHandle processTransactions, MethodHandle processTransaction)
    {
        INamedTypeSymbol executorType = processTransactions.Symbol.ContainingType
            ?? throw new ExtractionException("The direct transaction executor has no containing type.");

        // ProcessTransactions is the concrete interface implementation selected by DI. Its
        // ProcessTransaction helper is intentionally virtual in the production type, but no
        // derived executor may replace the non-virtual outer entry point in this slice.
        if (executorType.IsAbstract || executorType.BaseType?.SpecialType != SpecialType.System_Object ||
            processTransactions.Symbol.MethodKind != MethodKind.Ordinary || processTransactions.Symbol.IsStatic ||
            processTransactions.Symbol.IsAbstract || processTransactions.Symbol.IsVirtual ||
            processTransactions.Symbol.IsOverride || processTransaction.Symbol.MethodKind != MethodKind.Ordinary ||
            processTransaction.Symbol.IsStatic || processTransaction.Symbol.IsAbstract ||
            !processTransaction.Symbol.IsVirtual || processTransaction.Symbol.IsOverride)
        {
            throw new ExtractionException("The direct BlockValidationTransactionsExecutor route is not the exact non-virtual outer entry point.");
        }
    }

    private static void RequireExactDirectExecutorRegistration(Anchor registration)
    {
        string canonical = registration.Binding.CanonicalSyntax;
        if (!canonical.Contains("BlockProcessor.BlockValidationTransactionsExecutor", StringComparison.Ordinal) ||
            !canonical.Contains("IBlockProcessor.IBlockTransactionsExecutor", StringComparison.Ordinal))
        {
            throw new ExtractionException("The DI route no longer registers the exact direct BlockValidationTransactionsExecutor.");
        }
    }

    private static bool BaseTypeIs(
        INamedTypeSymbol derived,
        INamedTypeSymbol expectedDefinition,
        INamedTypeSymbol? expectedTypeArgument = null)
    {
        INamedTypeSymbol? baseType = derived.BaseType;
        if (baseType is null || !SymbolEqualityComparer.Default.Equals(baseType.OriginalDefinition, expectedDefinition))
        {
            return false;
        }

        return expectedTypeArgument is null || baseType.TypeArguments.Length == 1 &&
            SymbolEqualityComparer.Default.Equals(OriginalDefinition(baseType.TypeArguments[0]), expectedTypeArgument);
    }

    private static void RequireCanonicalBaseType(
        SemanticUnit unit,
        INamedTypeSymbol type,
        string expected,
        string message)
    {
        TypeDeclarationSyntax declaration = type.DeclaringSyntaxReferences
            .Where(reference => ReferenceEquals(reference.SyntaxTree, unit.Source.Tree))
            .Select(static reference => reference.GetSyntax())
            .OfType<TypeDeclarationSyntax>()
            .SingleOrDefault()
            ?? throw new ExtractionException($"Base-type source identity for {type.ToDisplayString()} is not owned by {unit.Source.RelativePath}.");
        BaseTypeSyntax baseType = declaration.BaseList?.Types.SingleOrDefault()
            ?? throw new ExtractionException($"Base-type source identity for {type.ToDisplayString()} is missing or ambiguous.");
        if (!string.Equals(Canonical(baseType.Type), CanonicalText(expected), StringComparison.Ordinal))
            throw new ExtractionException(message);
    }

    private static Anchor BindFluentInvocation(
        MethodHandle method,
        string targetName,
        string id,
        string ownSignature,
        string ownArgument = "")
    {
        string expectedSignature = CanonicalText(ownSignature);
        string expectedArgument = CanonicalText(ownArgument);
        InvocationExpressionSyntax[] candidates = method.Syntax.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(invocation => InvocationName(invocation) == targetName)
            .Where(invocation => InvocationSimpleName(invocation) is SimpleNameSyntax name &&
                string.Equals(Canonical(name), expectedSignature, StringComparison.Ordinal))
            .Where(invocation => expectedArgument.Length == 0 ||
                Canonical(invocation.ArgumentList).Contains(expectedArgument, StringComparison.Ordinal))
            .ToArray();
        if (candidates.Length != 1)
        {
            throw new ExtractionException(
                $"Expected invocation {ownSignature} for {id}, found {candidates.Length}.");
        }

        InvocationExpressionSyntax invocation = candidates[0];
        RejectNestedFunctionOwner(invocation, id);
        if (method.Unit.Model.GetOperation(invocation) is not IInvocationOperation operation ||
            operation.TargetMethod is null || operation.TargetMethod.Name != targetName)
        {
            throw new ExtractionException($"Invocation {id} did not resolve to the expected fluent target method.");
        }

        RequireInvocationTargetIdentity(method.Unit, invocation, operation, id);

        return new Anchor(id, "invocation", BindNode(method.Unit, invocation, id, method), [], [], "typed-fluent-invocation");
    }

    private static Anchor BindInvocation(
        MethodHandle method,
        string targetName,
        string id,
        string requiredText,
        int occurrence = 0,
        int? expectedCount = null)
    {
        InvocationExpressionSyntax[] candidates = method.Syntax.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(invocation => InvocationName(invocation) == targetName)
            .Where(invocation => requiredText.Length == 0 || Canonical(invocation).Contains(CanonicalText(requiredText), StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (occurrence < 0 || candidates.Length != (expectedCount ?? occurrence + 1) || candidates.Length <= occurrence)
        {
            throw new ExtractionException($"Expected invocation {occurrence} of {method.Symbol.Name}.{targetName} for {id}, found {candidates.Length}; expected {(expectedCount ?? occurrence + 1)}.");
        }

        InvocationExpressionSyntax invocation = candidates[occurrence];
        RejectNestedFunctionOwner(invocation, id);
        IOperation operation = method.Unit.Model.GetOperation(invocation)
            ?? throw new ExtractionException($"No IOperation for invocation {id}.");
        if (operation is not IInvocationOperation invocationOperation ||
            invocationOperation.TargetMethod is null || invocationOperation.TargetMethod.Name != targetName)
        {
            throw new ExtractionException($"Invocation {id} did not resolve to the expected target method.");
        }

        RequireInvocationTargetIdentity(method.Unit, invocation, invocationOperation, id);

        return new Anchor(id, "invocation", BindNode(method.Unit, invocation, id, method), [], [], "typed-invocation");
    }

    private static void RequireInvocationTargetIdentity(
        SemanticUnit unit,
        InvocationExpressionSyntax syntax,
        IInvocationOperation operation,
        string id)
    {
        InvocationTargetIdentity[] entries = InvocationTargetLedger
            .Where(entry => string.Equals(entry.Id, id, StringComparison.Ordinal))
            .ToArray();
        if (entries.Length != 1)
            throw new ExtractionException($"Invocation target identity ledger has {entries.Length} entries for {id}.");

        InvocationTargetIdentity identity = entries[0];
        IMethodSymbol target = operation.TargetMethod.ReducedFrom ?? operation.TargetMethod;
        IMethodSymbol definition = target.OriginalDefinition;
        INamedTypeSymbol expectedOwner = ResolveExactType(unit.Compilation, identity.MetadataTypeName,
            $"invocation target {id}");
        MethodKind expectedMethodKind = identity.Kind switch
        {
            LedgerMemberKind.Method => MethodKind.Ordinary,
            LedgerMemberKind.LocalFunction => MethodKind.LocalFunction,
            LedgerMemberKind.DelegateInvoke => MethodKind.DelegateInvoke,
            _ => throw new ExtractionException($"Unsupported invocation member kind in the identity ledger for {id}."),
        };
        bool signatureMatches = InvocationSignatureMatches(unit.Compilation, definition, identity, expectedMethodKind);
        int exactMultiplicity;
        if (identity.Kind == LedgerMemberKind.LocalFunction)
        {
            LocalFunctionStatementSyntax[] declarations = definition.DeclaringSyntaxReferences
                .Where(reference => ReferenceEquals(reference.SyntaxTree, unit.Source.Tree))
                .Select(static reference => reference.GetSyntax())
                .OfType<LocalFunctionStatementSyntax>()
                .Where(declaration => string.Equals(CanonicalLocalFunctionSignature(declaration),
                    identity.CanonicalSyntax, StringComparison.Ordinal))
                .ToArray();
            exactMultiplicity = declarations.Count(declaration =>
                SymbolEqualityComparer.Default.Equals(unit.Model.GetDeclaredSymbol(declaration), definition));
        }
        else
        {
            IMethodSymbol[] signatureCandidates = expectedOwner.GetMembers(identity.MemberName)
                .OfType<IMethodSymbol>()
                .Where(candidate => InvocationSignatureMatches(unit.Compilation, candidate.OriginalDefinition,
                    identity, expectedMethodKind))
                .ToArray();
            exactMultiplicity = signatureCandidates.Count(candidate =>
                SymbolEqualityComparer.Default.Equals(candidate.OriginalDefinition, definition));
            if (signatureCandidates.Length != identity.Multiplicity)
            {
                throw new ExtractionException(
                    $"Invocation target signature identity for {id} resolved {signatureCandidates.Length} overloads; expected {identity.Multiplicity}.");
            }
        }

        if (!signatureMatches ||
            !string.Equals(definition.Name, identity.MemberName, StringComparison.Ordinal) ||
            !SymbolEqualityComparer.Default.Equals(definition.ContainingType?.OriginalDefinition, expectedOwner) ||
            exactMultiplicity != identity.Multiplicity)
        {
            throw new ExtractionException(
                $"Invocation target identity mismatch for {id}: expected {identity.MetadataTypeName}.{identity.MemberName}/{identity.ParameterCount}.");
        }

        if (identity.TypeArgumentMetadataNames is not null)
        {
            if (operation.TargetMethod.TypeArguments.Length != identity.TypeArgumentMetadataNames.Length)
                throw new ExtractionException($"Invocation generic type-argument identity mismatch for {id}.");
            for (int i = 0; i < operation.TargetMethod.TypeArguments.Length; i++)
            {
                INamedTypeSymbol expectedTypeArgument = ResolveExactType(unit.Compilation,
                    identity.TypeArgumentMetadataNames[i], $"invocation type argument {id}[{i}]");
                if (!SymbolEqualityComparer.Default.Equals(
                        OriginalDefinition(operation.TargetMethod.TypeArguments[i]),
                        expectedTypeArgument))
                {
                    throw new ExtractionException(
                        $"Invocation generic type-argument identity mismatch for {id} at position {i}.");
                }
            }
        }

        if (identity.ReceiverMetadataTypeName is null) return;

        ITypeSymbol? receiver = operation.Instance?.Type;
        if (receiver is null && syntax.Expression is MemberAccessExpressionSyntax memberAccess)
            receiver = unit.Model.GetTypeInfo(memberAccess.Expression).Type;
        if (receiver is null && operation.TargetMethod.IsExtensionMethod && operation.Arguments.Length != 0)
            receiver = operation.Arguments[0].Value.Type;
        INamedTypeSymbol expectedReceiver = ResolveExactType(unit.Compilation, identity.ReceiverMetadataTypeName,
            $"invocation receiver {id}");
        if (receiver is null ||
            !SymbolEqualityComparer.Default.Equals(OriginalDefinition(receiver), expectedReceiver))
        {
            throw new ExtractionException(
                $"Invocation receiver identity mismatch for {id}: expected {identity.ReceiverMetadataTypeName}.");
        }
    }

    private static bool InvocationSignatureMatches(
        CSharpCompilation compilation,
        IMethodSymbol method,
        InvocationTargetIdentity identity,
        MethodKind expectedMethodKind)
    {
        if (method.MethodKind != expectedMethodKind || method.Arity != identity.Arity ||
            method.Parameters.Length != identity.ParameterCount ||
            !method.Parameters.Select(static parameter => RefKindName(parameter.RefKind))
                .SequenceEqual(identity.RefKinds, StringComparer.Ordinal))
        {
            return false;
        }

        if (identity.ParameterTypeMetadataNames is null) return true;

        for (int i = 0; i < method.Parameters.Length; i++)
        {
            INamedTypeSymbol expectedParameterType = ResolveExactType(compilation,
                identity.ParameterTypeMetadataNames[i], $"invocation parameter {identity.Id}[{i}]");
            if (!SymbolEqualityComparer.Default.Equals(OriginalDefinition(method.Parameters[i].Type),
                    expectedParameterType))
            {
                return false;
            }
        }

        return true;
    }

    private static string CanonicalLocalFunctionSignature(LocalFunctionStatementSyntax function) =>
        string.Concat(function.Modifiers.Select(static modifier => modifier.Text)) +
        Canonical(function.ReturnType) + function.Identifier.Text +
        (function.TypeParameterList is null ? string.Empty : Canonical(function.TypeParameterList)) +
        Canonical(function.ParameterList);

    private static void RequireMemberTargetIdentity(
        SemanticUnit unit,
        MemberAccessExpressionSyntax syntax,
        string id)
    {
        MemberTargetIdentity[] entries = MemberTargetLedger
            .Where(entry => string.Equals(entry.Id, id, StringComparison.Ordinal))
            .ToArray();
        if (entries.Length != 1)
            throw new ExtractionException($"Member target identity ledger has {entries.Length} entries for {id}.");

        MemberTargetIdentity identity = entries[0];
        ISymbol symbol = unit.Model.GetSymbolInfo(syntax).Symbol
            ?? throw new ExtractionException($"Member target identity for {id} did not resolve.");
        INamedTypeSymbol expectedOwner = ResolveExactType(unit.Compilation, identity.MetadataTypeName,
            $"member target {id}");
        INamedTypeSymbol expectedReceiver = ResolveExactType(unit.Compilation, identity.ReceiverMetadataTypeName,
            $"member receiver {id}");
        ITypeSymbol? receiver = unit.Model.GetTypeInfo(syntax.Expression).Type;
        ISymbol[] exactMembers = expectedOwner.GetMembers(identity.MemberName)
            .Where(candidate => SymbolEqualityComparer.Default.Equals(candidate, symbol))
            .ToArray();
        if (symbol.Kind != identity.Kind || exactMembers.Length != identity.Multiplicity ||
            !SymbolEqualityComparer.Default.Equals(symbol.ContainingType?.OriginalDefinition, expectedOwner) ||
            receiver is null || !SymbolEqualityComparer.Default.Equals(OriginalDefinition(receiver), expectedReceiver))
        {
            throw new ExtractionException($"Member/receiver identity mismatch for {id}.");
        }
    }

    private static void RequireAssignmentTargetIdentity(
        SemanticUnit unit,
        ExpressionSyntax syntax,
        string id)
    {
        AssignmentTargetIdentity[] entries = AssignmentTargetLedger
            .Where(entry => string.Equals(entry.Id, id, StringComparison.Ordinal))
            .ToArray();
        if (entries.Length != 1)
            throw new ExtractionException($"Assignment target identity ledger has {entries.Length} entries for {id}.");

        AssignmentTargetIdentity identity = entries[0];
        IOperation operation = unit.Model.GetOperation(syntax)
            ?? throw new ExtractionException($"Assignment target identity for {id} has no IOperation.");
        ISymbol symbol = operation switch
        {
            IFieldReferenceOperation field => field.Field,
            IPropertyReferenceOperation property => property.Property,
            _ => throw new ExtractionException($"Assignment target identity for {id} is not a field or property."),
        };
        INamedTypeSymbol expectedOwner = ResolveExactType(unit.Compilation, identity.MetadataTypeName,
            $"assignment target {id}");
        ISymbol[] exactMembers = expectedOwner.GetMembers(identity.MemberName)
            .Where(candidate => SymbolEqualityComparer.Default.Equals(candidate, symbol))
            .ToArray();
        if (symbol.Kind != identity.Kind || exactMembers.Length != identity.Multiplicity ||
            !SymbolEqualityComparer.Default.Equals(symbol.ContainingType?.OriginalDefinition, expectedOwner))
        {
            throw new ExtractionException($"Assignment target identity mismatch for {id}.");
        }
    }

    private static Anchor BindMemberAccess(SemanticUnit unit, MethodHandle method, string memberName, string id)
    {
        MemberAccessExpressionSyntax[] members = method.Syntax.DescendantNodes()
            .OfType<MemberAccessExpressionSyntax>()
            .Where(member => member.Name.Identifier.ValueText == memberName)
            .ToArray();
        if (members.Length != 1) throw new ExtractionException($"Expected one {method.Symbol.Name}.{memberName} member access for {id}, found {members.Length}.");
        RejectNestedFunctionOwner(members[0], id);
        RequireMemberTargetIdentity(unit, members[0], id);
        return new Anchor(id, "member-access", BindNode(unit, members[0], id, method), [], [], "typed-member-access");
    }

    private static Anchor BindAssignment(MethodHandle method, string targetName, string id)
    {
        AssignmentExpressionSyntax[] assignments = method.Syntax.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Where(assignment => Canonical(assignment.Left) == targetName)
            .ToArray();
        if (assignments.Length != 1) throw new ExtractionException($"Expected one assignment to {targetName} for {id}, found {assignments.Length}.");
        RejectNestedFunctionOwner(assignments[0], id);
        RequireAssignmentTargetIdentity(method.Unit, assignments[0].Left, id);
        return new Anchor(id, "assignment", BindNode(method.Unit, assignments[0], id, method), [], [], "typed-assignment");
    }

    private static Anchor BindReturn(MethodHandle method, string id, string requiredText)
    {
        ReturnStatementSyntax[] returns = method.Syntax.DescendantNodes()
            .OfType<ReturnStatementSyntax>()
            .Where(returnStatement => requiredText.Length == 0 || Canonical(returnStatement).Contains(CanonicalText(requiredText), StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (returns.Length != 1) throw new ExtractionException($"Expected one return for {id}, found {returns.Length}.");
        RejectNestedFunctionOwner(returns[0], id);
        return new Anchor(id, "return", BindNode(method.Unit, returns[0], id, method), [], [], "normal-return-site");
    }

    private static Anchor BindPrefixIf(MethodHandle method, string requiredText, string id)
    {
        IfStatementSyntax[] statements = method.Syntax.DescendantNodes()
            .OfType<IfStatementSyntax>()
            .Where(statement => Canonical(statement.Condition).Contains(CanonicalText(requiredText), StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (statements.Length != 1) throw new ExtractionException($"Expected one guard containing '{requiredText}' for {id}, found {statements.Length}.");
        RejectNestedFunctionOwner(statements[0], id);
        return new Anchor(id, "guard", BindNode(method.Unit, statements[0].Condition, id, method), [], [], "typed-guard");
    }

    private static Anchor BindConditional(MethodHandle method, string requiredText, string id)
    {
        ConditionalExpressionSyntax[] expressions = method.Syntax.DescendantNodes()
            .OfType<ConditionalExpressionSyntax>()
            .Where(expression => Canonical(expression.Condition).Contains(CanonicalText(requiredText), StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (expressions.Length != 1)
        {
            throw new ExtractionException($"Expected one conditional expression containing '{requiredText}' for {id}, found {expressions.Length}.");
        }

        RejectNestedFunctionOwner(expressions[0], id);
        return new Anchor(id, "conditional", BindNode(method.Unit, expressions[0].Condition, id, method), [], [], "typed-conditional");
    }

    private static Anchor BindIncrement(MethodHandle method, string targetName, string id)
    {
        PostfixUnaryExpressionSyntax[] increments = method.Syntax.DescendantNodes()
            .OfType<PostfixUnaryExpressionSyntax>()
            .Where(increment => increment.IsKind(SyntaxKind.PostIncrementExpression) &&
                Canonical(increment.Operand) == targetName)
            .ToArray();
        if (increments.Length != 1)
            throw new ExtractionException($"Expected one post-increment of {targetName} for {id}, found {increments.Length}.");
        RejectNestedFunctionOwner(increments[0], id);
        RequireAssignmentTargetIdentity(method.Unit, increments[0].Operand, id);
        return new Anchor(id, "increment", BindNode(method.Unit, increments[0], id, method), [], [], "typed-index-increment");
    }

    private static void RejectNestedFunctionOwner(SyntaxNode node, string id)
    {
        if (node.Ancestors().Any(static ancestor => ancestor is LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax))
            throw new ExtractionException($"Source binding {id} is owned by a local function or lambda.");
    }

    private static void RequireCanonicalBinding(
        Anchor anchor,
        string expected,
        string message,
        bool allowReceiverPrefix = false)
    {
        string actual = CanonicalText(anchor.Binding.CanonicalSyntax);
        string wanted = CanonicalText(expected);
        if (!string.Equals(actual, wanted, StringComparison.Ordinal) &&
            (!allowReceiverPrefix || !actual.EndsWith(wanted, StringComparison.Ordinal)))
        {
            throw new ExtractionException(message);
        }
    }

    private static void RequireUnconditional(MethodHandle method, Anchor anchor, string message)
    {
        SyntaxNode? node = method.Syntax.DescendantNodesAndSelf()
            .SingleOrDefault(candidate => candidate.SpanStart == anchor.Binding.Position &&
                string.Equals(Canonical(candidate), anchor.Binding.CanonicalSyntax, StringComparison.Ordinal));
        if (node is null || !IsAttachedToMethod(method, anchor.Binding) ||
            node.Ancestors().Any(IsConditionalOrExceptionalContainer))
        {
            throw new ExtractionException(message);
        }
    }

    private static void RequireReachable(MethodHandle method, Anchor anchor, string message)
    {
        if (!IsAttachedToMethod(method, anchor.Binding))
        {
            throw new ExtractionException(message);
        }
    }

    private static bool IsAttachedToMethod(MethodHandle method, TypedBinding binding) =>
        binding.ControlFlowBlock >= 0 && binding.IsReachable &&
        string.Equals(binding.Path, method.Unit.Source.RelativePath, StringComparison.Ordinal) &&
        string.Equals(binding.Owner, method.Symbol.ContainingType?.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat), StringComparison.Ordinal) &&
        string.Equals(binding.Member, method.Symbol.Name, StringComparison.Ordinal) &&
        string.Equals(binding.ContainingMember, method.Symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat), StringComparison.Ordinal);

    private static bool IsConditionalOrExceptionalContainer(SyntaxNode node) =>
        node is IfStatementSyntax or ElseClauseSyntax or ForStatementSyntax or ForEachStatementSyntax or
        WhileStatementSyntax or DoStatementSyntax or SwitchStatementSyntax or SwitchSectionSyntax or
        SwitchExpressionArmSyntax or WhenClauseSyntax or CatchClauseSyntax or FinallyClauseSyntax or
        ConditionalExpressionSyntax or ConditionalAccessExpressionSyntax;

    private static Anchor Add(List<Anchor> anchors, Anchor anchor)
    {
        if (anchors.Any(existing => existing.Id == anchor.Id)) throw new ExtractionException($"Duplicate anchor id: {anchor.Id}.");
        anchors.Add(anchor);
        return anchor;
    }

    private static TypedBinding BindNode(SemanticUnit unit, SyntaxNode node, string id, MethodHandle? method)
    {
        IOperation? operation = unit.Model.GetOperation(node);
        SymbolInfo symbolInfo = unit.Model.GetSymbolInfo(node);
        ISymbol? symbol = symbolInfo.Symbol ?? (node switch
        {
            MethodDeclarationSyntax declaration => unit.Model.GetDeclaredSymbol(declaration),
            EnumMemberDeclarationSyntax declaration => unit.Model.GetDeclaredSymbol(declaration),
            TypeDeclarationSyntax declaration => unit.Model.GetDeclaredSymbol(declaration),
            ParameterSyntax declaration => unit.Model.GetDeclaredSymbol(declaration),
            VariableDeclaratorSyntax declaration => unit.Model.GetDeclaredSymbol(declaration),
            _ => null,
        });

        if (symbolInfo.CandidateReason != CandidateReason.None || symbolInfo.CandidateSymbols.Length != 0)
        {
            throw new ExtractionException($"Ambiguous source binding for {id}: {symbolInfo.CandidateReason}.");
        }

        if (symbol is not null) RejectSymbol(symbol, $"binding {id}");
        if (operation is IInvalidOperation || operation is not null && HasErrorType(operation.Type))
        {
            throw new ExtractionException($"Invalid or error-typed IOperation for {id}.");
        }

        IMethodSymbol? containingSymbol = method?.Symbol;
        ControlFlowGraph? graph = method is null ? null : GetGraph(method);
        int block = method is null || graph is null ? -1 : FindBlock(graph, node);
        bool reachable = block >= 0 && graph!.Blocks.FirstOrDefault(candidate => candidate.Ordinal == block)?.IsReachable == true;
        if (method is not null && block < 0)
        {
            throw new ExtractionException($"Source binding {id} is not attached to a CFG block.");
        }

        DataFlowAnalysis? dataFlow = null;
        if (method is not null)
        {
            try
            {
                dataFlow = unit.Model.AnalyzeDataFlow(node);
            }
            catch (ArgumentException exception)
            {
                throw new ExtractionException($"Data-flow analysis failed for {id}: {exception.Message}");
            }

            if (!dataFlow.Succeeded)
            {
                throw new ExtractionException($"Data-flow analysis did not succeed for {id}.");
            }
        }

        string symbolId = symbol?.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) ?? string.Empty;
        string targetSymbol = operation switch
        {
            IInvocationOperation invocation => invocation.TargetMethod?.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) ??
                throw new ExtractionException($"Invocation {id} has no target symbol."),
            IPropertyReferenceOperation property => property.Property.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
            IFieldReferenceOperation field => field.Field.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
            IMethodReferenceOperation methodReference => methodReference.Method.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
            ISimpleAssignmentOperation assignment => AssignmentTargetSymbol(assignment),
            IIncrementOrDecrementOperation increment => AssignmentTargetSymbol(increment),
            _ => symbolId,
        };
        string receiverType = operation switch
        {
            IInvocationOperation invocation => invocation.Instance?.Type?.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) ?? string.Empty,
            IPropertyReferenceOperation property => property.Instance?.Type?.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) ?? string.Empty,
            IFieldReferenceOperation field => field.Instance?.Type?.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) ?? string.Empty,
            _ => string.Empty,
        };
        string operationType = operation?.Type?.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) ??
            symbol switch
            {
                IMethodSymbol methodSymbol => methodSymbol.ReturnType.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
                _ => string.Empty,
            };
        FileLinePositionSpan span = unit.Source.Tree.GetLineSpan(node.Span);
        string[] ReadNames(IEnumerable<ISymbol> values) => values
            .Select(static value => value.Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();

        return new TypedBinding(
            unit.Source.RelativePath,
            method?.Symbol.ContainingType?.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) ?? symbol?.ContainingType?.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) ?? string.Empty,
            method?.Symbol.Name ?? symbol?.Name ?? id,
            node.Kind().ToString(),
            Canonical(node),
            Sha256(Encoding.UTF8.GetBytes(Canonical(node))),
            symbolId,
            symbol?.Kind.ToString() ?? operation?.Kind.ToString() ?? "Unknown",
            operation?.Kind.ToString() ?? "Declaration",
            operationType,
            receiverType,
            targetSymbol,
            containingSymbol?.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) ?? string.Empty,
            node.SpanStart,
            span.StartLinePosition.Line + 1,
            span.StartLinePosition.Character + 1,
            span.EndLinePosition.Line + 1,
            span.EndLinePosition.Character + 1,
            block,
            reachable,
            dataFlow?.Succeeded ?? true,
            operation is not null && HasErrorType(operation.Type),
            symbolInfo.CandidateSymbols.Length != 0,
            symbolInfo.CandidateReason.ToString(),
            ReadNames(dataFlow?.ReadInside ?? ImmutableArray<ISymbol>.Empty),
            ReadNames(dataFlow?.WrittenInside ?? ImmutableArray<ISymbol>.Empty),
            ReadNames(dataFlow?.ReadOutside ?? ImmutableArray<ISymbol>.Empty),
            ReadNames(dataFlow?.WrittenOutside ?? ImmutableArray<ISymbol>.Empty));
    }

    private static string AssignmentTargetSymbol(ISimpleAssignmentOperation assignment) =>
        assignment.Target switch
        {
            ILocalReferenceOperation local => local.Local.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
            IParameterReferenceOperation parameter => parameter.Parameter.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
            IFieldReferenceOperation field => field.Field.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
            IPropertyReferenceOperation property => property.Property.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
            _ => assignment.Target.Type?.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) ?? string.Empty,
        };

    private static string AssignmentTargetSymbol(IIncrementOrDecrementOperation increment) =>
        increment.Target switch
        {
            ILocalReferenceOperation local => local.Local.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
            IParameterReferenceOperation parameter => parameter.Parameter.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
            IFieldReferenceOperation field => field.Field.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
            IPropertyReferenceOperation property => property.Property.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
            _ => increment.Target.Type?.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) ?? string.Empty,
        };

    private static void RejectSymbol(ISymbol symbol, string context)
    {
        if (symbol is IErrorTypeSymbol || symbol.ContainingType is IErrorTypeSymbol ||
            symbol.ContainingAssembly is null || symbol.ToDisplayString().Contains("<error", StringComparison.OrdinalIgnoreCase))
        {
            throw new ExtractionException($"Error or unresolved symbol in {context}: {symbol.ToDisplayString()}.");
        }
    }

    private static bool HasErrorType(ITypeSymbol? type)
    {
        if (type is null) return false;
        if (type.TypeKind == TypeKind.Error) return true;
        return type is INamedTypeSymbol named && named.TypeArguments.Any(HasErrorType);
    }

    private static string InvocationName(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText,
        _ => string.Empty,
    };

    private static SimpleNameSyntax? InvocationSimpleName(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        SimpleNameSyntax simple => simple,
        MemberAccessExpressionSyntax member => member.Name,
        MemberBindingExpressionSyntax binding => binding.Name,
        _ => null,
    };

    private static string Canonical(SyntaxNode node) =>
        string.Concat(node.DescendantTokens().Select(static token => token.Text));

    private static string CanonicalTokens(SyntaxNode node) =>
        string.Join(";", node.DescendantTokens().Select(token => $"{token.Kind()}:{token.ValueText}"));

    private static string CanonicalText(string text) =>
        string.Concat(text.Where(static character => !char.IsWhiteSpace(character)));

    private static ControlFlowGraph GetGraph(MethodHandle method)
    {
        if (method.Graph is not null) return method.Graph;
        IOperation operation = method.Unit.Model.GetOperation(method.Syntax)
            ?? throw new ExtractionException($"No IOperation for method {method.Symbol.Name}.");
        if (operation is not IMethodBodyOperation body)
        {
            throw new ExtractionException($"Method {method.Symbol.Name} has no method-body operation.");
        }

        try
        {
            method.Graph = ControlFlowGraph.Create(body);
            return method.Graph;
        }
        catch (ArgumentException exception)
        {
            throw new ExtractionException($"Cannot construct CFG for {method.Symbol.Name}: {exception.Message}");
        }
        catch (InvalidOperationException exception)
        {
            throw new ExtractionException($"Cannot construct CFG for {method.Symbol.Name}: {exception.Message}");
        }
    }

    private static int FindBlock(ControlFlowGraph graph, SyntaxNode node)
    {
        foreach (BasicBlock block in graph.Blocks)
        {
            if (block.Operations.Any(operation => ContainsSyntax(operation, node)) ||
                block.BranchValue is not null && ContainsSyntax(block.BranchValue, node))
            {
                return block.Ordinal;
            }
        }

        return -1;
    }

    private static bool ContainsSyntax(IOperation operation, SyntaxNode node) =>
        ReferenceEquals(operation.Syntax.SyntaxTree, node.SyntaxTree) && operation.Syntax.FullSpan.Contains(node.FullSpan);

    private static void RequireSourceOrder(Anchor earlier, Anchor later, string message)
    {
        if (!string.Equals(earlier.Binding.Path, later.Binding.Path, StringComparison.Ordinal) ||
            earlier.Binding.Position >= later.Binding.Position)
        {
            throw new ExtractionException(message);
        }
    }

    private static void RequireFluentOrder(
        MethodHandle method,
        Anchor earlier,
        Anchor later,
        string message)
    {
        SyntaxNode? earlierNode = method.Syntax.DescendantNodesAndSelf().SingleOrDefault(candidate =>
            candidate.SpanStart == earlier.Binding.Position &&
            string.Equals(Canonical(candidate), earlier.Binding.CanonicalSyntax, StringComparison.Ordinal));
        SyntaxNode? laterNode = method.Syntax.DescendantNodesAndSelf().SingleOrDefault(candidate =>
            candidate.SpanStart == later.Binding.Position &&
            string.Equals(Canonical(candidate), later.Binding.CanonicalSyntax, StringComparison.Ordinal));
        bool ordered = earlierNode is not null && laterNode is not null &&
            (earlierNode.Span.End <= laterNode.SpanStart ||
                laterNode.SpanStart <= earlierNode.SpanStart &&
                laterNode.Span.End >= earlierNode.Span.End &&
                earlierNode.Span.End < laterNode.Span.End);
        if (!ordered)
        {
            throw new ExtractionException(message);
        }
    }

    private static void RequireDominates(MethodHandle method, Anchor dominator, Anchor node, string message)
    {
        if (!string.Equals(dominator.Binding.Path, node.Binding.Path, StringComparison.Ordinal) ||
            dominator.Binding.ControlFlowBlock < 0 || node.Binding.ControlFlowBlock < 0 ||
            !Dominates(GetGraph(method), dominator.Binding.ControlFlowBlock, node.Binding.ControlFlowBlock, normalOnly: true))
        {
            throw new ExtractionException(message);
        }
    }

    private static void RequirePostDominates(MethodHandle method, Anchor postDominator, Anchor node, string message)
    {
        if (!string.Equals(postDominator.Binding.Path, node.Binding.Path, StringComparison.Ordinal) ||
            postDominator.Binding.ControlFlowBlock < 0 || node.Binding.ControlFlowBlock < 0 ||
            !PostDominates(GetGraph(method), postDominator.Binding.ControlFlowBlock, node.Binding.ControlFlowBlock, normalOnly: true))
        {
            throw new ExtractionException(message);
        }
    }

    private static bool Dominates(ControlFlowGraph graph, int dominator, int node, bool normalOnly)
    {
        HashSet<int>[] predecessors = BuildPredecessors(graph, normalOnly);
        int[] reachable = graph.Blocks.Where(static block => block.IsReachable).Select(static block => block.Ordinal).ToArray();
        if (!reachable.Contains(dominator) || !reachable.Contains(node)) return false;
        int entry = graph.Blocks.First(block => block.Kind == BasicBlockKind.Entry).Ordinal;
        Dictionary<int, HashSet<int>> sets = reachable.ToDictionary(
            ordinal => ordinal,
            ordinal => ordinal == entry ? [entry] : reachable.ToHashSet());
        bool changed;
        do
        {
            changed = false;
            foreach (int ordinal in reachable.Where(value => value != entry))
            {
                HashSet<int>[] incoming = predecessors[ordinal]
                    .Where(sets.ContainsKey)
                    .Select(predecessor => sets[predecessor])
                    .ToArray();
                HashSet<int> next = incoming.Length == 0
                    ? [ordinal]
                    : new HashSet<int>(incoming[0]);
                foreach (HashSet<int> incomingSet in incoming.Skip(1)) next.IntersectWith(incomingSet);
                next.Add(ordinal);
                if (!sets[ordinal].SetEquals(next))
                {
                    sets[ordinal] = next;
                    changed = true;
                }
            }
        } while (changed);

        return sets[node].Contains(dominator);
    }

    private static bool PostDominates(ControlFlowGraph graph, int postDominator, int node, bool normalOnly)
    {
        Dictionary<int, HashSet<int>> successors = BuildSuccessors(graph, normalOnly);
        int[] reachable = graph.Blocks.Where(static block => block.IsReachable).Select(static block => block.Ordinal).ToArray();
        if (!reachable.Contains(postDominator) || !reachable.Contains(node)) return false;
        HashSet<int> exits = reachable.Where(ordinal => successors[ordinal].Count == 0).ToHashSet();
        Dictionary<int, HashSet<int>> sets = reachable.ToDictionary(
            ordinal => ordinal,
            ordinal => exits.Contains(ordinal) ? [ordinal] : reachable.ToHashSet());
        bool changed;
        do
        {
            changed = false;
            foreach (int ordinal in reachable.Where(value => !exits.Contains(value)))
            {
                HashSet<int>[] outgoing = successors[ordinal]
                    .Where(sets.ContainsKey)
                    .Select(successor => sets[successor])
                    .ToArray();
                HashSet<int> next = outgoing.Length == 0
                    ? [ordinal]
                    : new HashSet<int>(outgoing[0]);
                foreach (HashSet<int> outgoingSet in outgoing.Skip(1)) next.IntersectWith(outgoingSet);
                next.Add(ordinal);
                if (!sets[ordinal].SetEquals(next))
                {
                    sets[ordinal] = next;
                    changed = true;
                }
            }
        } while (changed);

        return sets[node].Contains(postDominator);
    }

    private static HashSet<int>[] BuildPredecessors(ControlFlowGraph graph, bool normalOnly)
    {
        int max = graph.Blocks.Max(static block => block.Ordinal);
        HashSet<int>[] predecessors = Enumerable.Range(0, max + 1).Select(static _ => new HashSet<int>()).ToArray();
        foreach (BasicBlock block in graph.Blocks.Where(static block => block.IsReachable))
        {
            foreach (ControlFlowBranch branch in Branches(block))
            {
                if (normalOnly && IsExceptionalBranch(block, branch)) continue;
                if (branch.Destination is BasicBlock destination && destination.IsReachable)
                    predecessors[destination.Ordinal].Add(block.Ordinal);
            }
        }

        return predecessors;
    }

    private static Dictionary<int, HashSet<int>> BuildSuccessors(ControlFlowGraph graph, bool normalOnly)
    {
        Dictionary<int, HashSet<int>> successors = graph.Blocks
            .Where(static block => block.IsReachable)
            .ToDictionary(static block => block.Ordinal, static _ => new HashSet<int>());
        foreach (BasicBlock block in graph.Blocks.Where(static block => block.IsReachable))
        {
            foreach (ControlFlowBranch branch in Branches(block))
            {
                if (normalOnly && IsExceptionalBranch(block, branch)) continue;
                if (branch.Destination is BasicBlock destination && destination.IsReachable)
                    successors[block.Ordinal].Add(destination.Ordinal);
            }
        }

        return successors;
    }

    private static bool IsExceptionalBranch(BasicBlock block, ControlFlowBranch branch) =>
        branch.Semantics.ToString().Contains("Exception", StringComparison.OrdinalIgnoreCase) ||
        branch.Semantics.ToString().Contains("Throw", StringComparison.OrdinalIgnoreCase) ||
        block.Operations.Any(static operation => operation is IThrowOperation);

    private static IEnumerable<ControlFlowBranch> Branches(BasicBlock block)
    {
        if (block.FallThroughSuccessor is ControlFlowBranch fallThrough) yield return fallThrough;
        if (block.ConditionalSuccessor is ControlFlowBranch conditional) yield return conditional;
    }

    private static void RequireSoleReturnInTrueArm(MethodHandle method, Anchor guard, Anchor call, string message)
    {
        IfStatementSyntax? statement = method.Syntax.DescendantNodes()
            .OfType<IfStatementSyntax>()
            .SingleOrDefault(candidate => candidate.Condition.SpanStart == guard.Binding.Position);
        if (!IsDirectReturnInTrueArm(statement, call))
        {
            throw new ExtractionException(message);
        }
    }

    private static void RequireSoleInvocationInTrueArm(MethodHandle method, Anchor guard, Anchor call, string message)
    {
        IfStatementSyntax? statement = method.Syntax.DescendantNodes()
            .OfType<IfStatementSyntax>()
            .SingleOrDefault(candidate => candidate.Condition.SpanStart == guard.Binding.Position);
        if (!IsDirectGuardStatement(statement, call))
        {
            throw new ExtractionException(message);
        }
    }

    private static bool IsDirectReturnInTrueArm(IfStatementSyntax? statement, Anchor call)
    {
        if (statement?.Statement is not BlockSyntax block || statement.Else is not null || block.Statements.Count != 1 ||
            block.Statements[0] is not ReturnStatementSyntax returnStatement ||
            returnStatement.Expression is not InvocationExpressionSyntax invocation)
        {
            return false;
        }

        return invocation.SpanStart == call.Binding.Position &&
            string.Equals(Canonical(invocation), call.Binding.CanonicalSyntax, StringComparison.Ordinal);
    }

    private static bool IsDirectGuardStatement(IfStatementSyntax? statement, Anchor call)
    {
        if (statement?.Else is not null)
        {
            return false;
        }

        StatementSyntax[] body = statement?.Statement switch
        {
            BlockSyntax block => block.Statements.ToArray(),
            StatementSyntax single => [single],
            _ => [],
        };
        return body.Length == 1 && body[0] is ExpressionStatementSyntax expressionStatement &&
            expressionStatement.Expression is InvocationExpressionSyntax invocation &&
            invocation.SpanStart == call.Binding.Position &&
            string.Equals(Canonical(invocation), call.Binding.CanonicalSyntax, StringComparison.Ordinal);
    }

    private static void AddControlFlow(
        List<ControlFlowIdentity> output,
        MethodHandle method,
        string id,
        IEnumerable<Anchor> anchors)
    {
        ControlFlowGraph graph = GetGraph(method);
        BasicBlock[] reachable = graph.Blocks.Where(static block => block.IsReachable).OrderBy(static block => block.Ordinal).ToArray();
        string[] edges = reachable
            .SelectMany(block => Branches(block)
                .Where(branch => branch.Destination is BasicBlock destination && destination.IsReachable)
                .Select(branch => $"{block.Ordinal}->{((BasicBlock)branch.Destination!).Ordinal}:{branch.Semantics}"))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static edge => edge, StringComparer.Ordinal)
            .ToArray();
        int[] normalExits = reachable
            .Where(block => Branches(block).Any(branch => branch.Destination is BasicBlock destination && destination.Kind == BasicBlockKind.Exit && !IsExceptionalBranch(block, branch)))
            .Select(static block => block.Ordinal)
            .Distinct()
            .OrderBy(static ordinal => ordinal)
            .ToArray();
        int[] exceptionalExits = reachable
            .Where(block => Branches(block).Any(branch => branch.Destination is BasicBlock destination && destination.Kind == BasicBlockKind.Exit && IsExceptionalBranch(block, branch)))
            .Select(static block => block.Ordinal)
            .Distinct()
            .OrderBy(static ordinal => ordinal)
            .ToArray();
        Anchor[] selected = anchors.ToArray();
        List<string> facts = [];
        for (int left = 0; left < selected.Length; left++)
        {
            for (int right = left + 1; right < selected.Length; right++)
            {
                Anchor earlier = selected[left];
                Anchor later = selected[right];
                if (earlier.Binding.ControlFlowBlock >= 0 && later.Binding.ControlFlowBlock >= 0 &&
                    Dominates(graph, earlier.Binding.ControlFlowBlock, later.Binding.ControlFlowBlock, normalOnly: true))
                {
                    facts.Add($"dominates:{earlier.Id}->{later.Id}");
                }
            }
        }

        output.Add(new ControlFlowIdentity(
            id,
            method.Unit.Source.RelativePath,
            method.Symbol.ContainingType?.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) ?? string.Empty,
            method.Symbol.Name,
            reachable.Select(static block => block.Ordinal).ToArray(),
            edges,
            normalExits,
            exceptionalExits,
            facts.OrderBy(static fact => fact, StringComparer.Ordinal).ToArray()));
    }

    private static void ValidateIr(IrDocument document)
    {
        if (document.SchemaVersion != SchemaVersion || document.ExtractorVersion != ExtractorVersion ||
            document.Kernel != Kernel || document.AcceptanceState != AcceptanceState ||
            !IsSha256(document.SourceClosureSha256) || !IsSha256(document.CompilerClosureSha256) ||
            document.Sources is null || document.Dependencies is null ||
            document.CompilerReferences is null || document.Machine is null || document.Route is null ||
            document.Options is null || document.ArithmeticRules is null || document.ExternalCorrespondence is null ||
            document.OpenObligations is null)
        {
            throw new ExtractionException("The ordinary machine IR header or required section changed.");
        }

        if (document.Sources.Length != SourceSpecs.Length || document.Dependencies.Length != DependencySpecs.Length ||
            document.Machine.Anchors is null || document.Machine.Anchors.Length == 0 ||
            document.Machine.ControlFlows is null || document.Machine.ControlFlows.Length == 0 ||
            document.Machine.Stages is null || document.Machine.Branches is null || document.Machine.Effects is null ||
            document.Machine.NormalReturnPremises is null || document.Machine.NormalReturnPremises.Length == 0 ||
            document.Machine.FieldwiseHandoffs is null || document.Machine.FieldwiseHandoffs.Length == 0 ||
            document.Machine.SemanticOperations is null || document.Machine.SemanticOperations.Length == 0 ||
            document.Machine.FieldwiseSeams is null || document.Machine.FieldwiseSeams.Length == 0 ||
            document.Machine.ReceiptTerminalSourceClosure is null ||
            document.Machine.Anchors.Any(static anchor => anchor is null) ||
            document.Machine.SemanticOperations.Any(static operation => operation is null) ||
            document.Machine.FieldwiseSeams.Any(static seam => seam is null) ||
            document.Route.Bindings is null || document.Route.Bindings.Length == 0)
        {
            throw new ExtractionException("The ordinary machine IR is incomplete.");
        }

        ValidateReceiptTerminalSourceClosure(document.Machine.ReceiptTerminalSourceClosure);

        if (document.Machine.SemanticOperations.Length != document.Machine.Anchors.Length ||
            !document.Machine.SemanticOperations.Select(static operation => operation.Id)
                .SequenceEqual(document.Machine.Anchors.Select(static anchor => anchor.Id), StringComparer.Ordinal) ||
            document.Machine.FieldwiseSeams.Length != FieldwiseSeamIds.Length ||
            !document.Machine.FieldwiseSeams.Select(static seam => seam.Id)
                .SequenceEqual(FieldwiseSeamIds, StringComparer.Ordinal))
        {
            throw new ExtractionException("The ordinary machine semantic operation or seam roster changed.");
        }

        if (document.Options.None != 0 || document.Options.Commit != 1 || document.Options.Restore != 2 ||
            document.Options.SkipValidation != 4 || document.Options.Warmup != 8 || document.Options.BuildUp != 16 ||
            !document.Options.ExactTarget.Contains("Commit", StringComparison.Ordinal))
        {
            throw new ExtractionException("The source-bound exact-Commit option shape changed.");
        }

        foreach (SourceIdentity source in document.Sources)
        {
            if (source is null || string.IsNullOrWhiteSpace(source.Path) || string.IsNullOrWhiteSpace(source.Role) ||
                !IsSha256(source.Sha256) || !IsSha256(source.SyntaxSha256))
            {
                throw new ExtractionException("The machine IR has an incomplete source identity.");
            }
        }

        foreach (DependencyIdentity dependency in document.Dependencies)
        {
            if (dependency is null || string.IsNullOrWhiteSpace(dependency.Path) || string.IsNullOrWhiteSpace(dependency.Role) || !IsSha256(dependency.Sha256))
            {
                throw new ExtractionException("The machine IR has an incomplete dependency identity.");
            }
        }

        ValidateCompilerIdentities(document.CompilerReferences);
        if (!string.Equals(document.CompilerClosureSha256, CompilerAggregateHash(document.CompilerReferences), StringComparison.Ordinal))
            throw new ExtractionException("The ordinary machine IR compiler closure hash changed.");
        if (!string.Equals(document.SourceClosureSha256, CombinedSourceHash(document.Sources), StringComparison.Ordinal))
            throw new ExtractionException("The ordinary machine IR source closure hash changed.");
        HashSet<string> anchorIds = new(StringComparer.Ordinal);
        foreach (Anchor anchor in document.Machine.Anchors)
        {
            if (anchor is null || string.IsNullOrWhiteSpace(anchor.Id) || !anchorIds.Add(anchor.Id) || anchor.Binding is null)
            {
                throw new ExtractionException("The machine IR has a duplicate or incomplete anchor.");
            }

            ValidateBinding(anchor.Binding);
        }

        HashSet<string> premiseIds = new(StringComparer.Ordinal);
        Dictionary<string, Anchor> premiseAnchors = document.Machine.Anchors.ToDictionary(static anchor => anchor.Id, StringComparer.Ordinal);
        foreach (NormalReturnPremise premise in document.Machine.NormalReturnPremises)
        {
            if (premise is null || string.IsNullOrWhiteSpace(premise.Id) || !premiseIds.Add(premise.Id) ||
                string.IsNullOrWhiteSpace(premise.AnchorId) || !premiseAnchors.TryGetValue(premise.AnchorId, out Anchor premiseAnchor) ||
                string.IsNullOrWhiteSpace(premise.Path) || string.IsNullOrWhiteSpace(premise.Owner) ||
                string.IsNullOrWhiteSpace(premise.Member) || premise.OutcomeType != "normal-return" ||
                string.IsNullOrWhiteSpace(premise.Condition) ||
                premise.Path != premiseAnchor.Binding.Path || premise.Owner != premiseAnchor.Binding.Owner ||
                premise.Member != premiseAnchor.Binding.Member)
            {
                throw new ExtractionException("The ordinary machine IR has an incomplete normal-return premise.");
            }
        }

        HashSet<string> semanticOperationIds = new(StringComparer.Ordinal);
        foreach (SemanticOperation operation in document.Machine.SemanticOperations)
        {
            if (operation is null || string.IsNullOrWhiteSpace(operation.Id) || !semanticOperationIds.Add(operation.Id) ||
                string.IsNullOrWhiteSpace(operation.AnchorId) || !premiseAnchors.TryGetValue(operation.AnchorId, out Anchor operationAnchor) ||
                string.IsNullOrWhiteSpace(operation.Path) || string.IsNullOrWhiteSpace(operation.Owner) ||
                string.IsNullOrWhiteSpace(operation.Member) || string.IsNullOrWhiteSpace(operation.OperationKind) ||
                string.IsNullOrWhiteSpace(operation.OperationType) || operation.CanonicalSyntax is null ||
                operation.ReadInside is null || operation.WrittenInside is null || operation.ReadOutside is null ||
                operation.WrittenOutside is null || operation.Path != operationAnchor.Binding.Path ||
                operation.Owner != operationAnchor.Binding.Owner || operation.Member != operationAnchor.Binding.Member ||
                operation.OperationKind != operationAnchor.Binding.OperationKind ||
                operation.OperationType != operationAnchor.Binding.OperationType ||
                operation.ReceiverType != operationAnchor.Binding.ReceiverType ||
                operation.TargetSymbol != operationAnchor.Binding.TargetSymbol ||
                operation.CanonicalSyntax != operationAnchor.Binding.CanonicalSyntax ||
                !operation.ReadInside.SequenceEqual(operationAnchor.Binding.ReadInside, StringComparer.Ordinal) ||
                !operation.WrittenInside.SequenceEqual(operationAnchor.Binding.WrittenInside, StringComparer.Ordinal) ||
                !operation.ReadOutside.SequenceEqual(operationAnchor.Binding.ReadOutside, StringComparer.Ordinal) ||
                !operation.WrittenOutside.SequenceEqual(operationAnchor.Binding.WrittenOutside, StringComparer.Ordinal))
            {
                throw new ExtractionException("The ordinary machine IR has an incomplete semantic operation.");
            }
        }

        HashSet<string> seamIds = new(StringComparer.Ordinal);
        foreach (FieldwiseHandoff seam in document.Machine.FieldwiseSeams)
        {
            if (seam is null || string.IsNullOrWhiteSpace(seam.Id) || !seamIds.Add(seam.Id) ||
                string.IsNullOrWhiteSpace(seam.AnchorId) || !premiseAnchors.TryGetValue(seam.AnchorId, out Anchor seamAnchor) ||
                string.IsNullOrWhiteSpace(seam.SourceMember) || string.IsNullOrWhiteSpace(seam.SourceField) ||
                string.IsNullOrWhiteSpace(seam.ModelType) || string.IsNullOrWhiteSpace(seam.ModelField) ||
                string.IsNullOrWhiteSpace(seam.Projection) || seam.SourceMember != seamAnchor.Binding.Member ||
                seam.Projection.Contains("TransactionResult.Equals", StringComparison.Ordinal) ||
                seam.Projection.Contains("whole-result", StringComparison.OrdinalIgnoreCase))
            {
                throw new ExtractionException("The ordinary machine IR has an invalid fieldwise seam.");
            }
        }
    }

    private static void ValidateManifest(Manifest manifest)
    {
        if (manifest.SchemaVersion != SchemaVersion || manifest.ExtractorVersion != ExtractorVersion ||
            manifest.Kernel != Kernel || manifest.AcceptanceState != AcceptanceState ||
            !IsSha256(manifest.SourceClosureSha256) || !IsSha256(manifest.CompilerClosureSha256) ||
            !IsSha256(manifest.CombinedSourceSha256) ||
            !IsSha256(manifest.SemanticIrSha256) || manifest.Sources is null || manifest.Dependencies is null ||
            manifest.CompilerReferences is null || manifest.Bindings is null || manifest.ControlFlows is null ||
            manifest.ReceiptTerminalSourceClosure is null || manifest.Ir is null || manifest.Lean is null ||
            manifest.OpenObligations is null)
        {
            throw new ExtractionException("The ordinary machine manifest header changed.");
        }

        if (manifest.Sources.Length != SourceSpecs.Length || manifest.Dependencies.Length != DependencySpecs.Length ||
            manifest.Bindings.Length == 0 || manifest.ControlFlows.Length == 0 ||
            manifest.Ir.Path != IrFileName || manifest.Lean.Path != Normalize(DefaultLeanPath) ||
            !IsSha256(manifest.Ir.Sha256) || !IsSha256(manifest.Lean.Sha256))
        {
            throw new ExtractionException("The ordinary machine manifest is incomplete.");
        }

        ValidateReceiptTerminalSourceClosure(manifest.ReceiptTerminalSourceClosure);

        ValidateCompilerIdentities(manifest.CompilerReferences);
        if (!string.Equals(manifest.CompilerClosureSha256, CompilerAggregateHash(manifest.CompilerReferences), StringComparison.Ordinal))
            throw new ExtractionException("The ordinary machine manifest compiler closure hash changed.");
        string sourceClosure = CombinedSourceHash(manifest.Sources);
        if (!string.Equals(manifest.SourceClosureSha256, sourceClosure, StringComparison.Ordinal) ||
            !string.Equals(manifest.CombinedSourceSha256, sourceClosure, StringComparison.Ordinal))
        {
            throw new ExtractionException("The ordinary machine manifest source closure hash changed.");
        }
        HashSet<string> keys = new(StringComparer.Ordinal);
        foreach (TypedBinding binding in manifest.Bindings)
        {
            ValidateBinding(binding);
            string key = $"{binding.Path}\0{binding.Position}\0{binding.Member}";
            if (!keys.Add(key)) throw new ExtractionException("The ordinary machine manifest has duplicate binding positions.");
        }
    }

    private static void ValidateBinding(TypedBinding binding)
    {
        if (binding is null || string.IsNullOrWhiteSpace(binding.Path) || string.IsNullOrWhiteSpace(binding.Member) ||
            string.IsNullOrWhiteSpace(binding.NodeKind) || string.IsNullOrWhiteSpace(binding.CanonicalSyntax) ||
            string.IsNullOrWhiteSpace(binding.OperationKind) || binding.Position < 0 || binding.ControlFlowBlock < -1 ||
            binding.IsErrorSymbol || binding.HasCandidateSymbols || binding.CandidateReason != "None" ||
            !IsSha256(binding.SyntaxSha256) || binding.ReadInside is null || binding.WrittenInside is null ||
            binding.ReadOutside is null || binding.WrittenOutside is null ||
            !string.Equals(binding.SyntaxSha256, Sha256(Encoding.UTF8.GetBytes(binding.CanonicalSyntax)), StringComparison.Ordinal))
        {
            throw new ExtractionException("The ordinary machine manifest contains an invalid typed binding.");
        }

        if (binding.ControlFlowBlock >= 0 && !binding.IsReachable)
        {
            throw new ExtractionException("A typed machine binding is attached to an unreachable CFG block.");
        }
    }

    private static void ValidateSourceIdentities(string root, SourceIdentity[] identities)
    {
        if (identities.Length != SourceSpecs.Length) throw new ExtractionException("The machine source identity count changed.");
        for (int index = 0; index < identities.Length; index++)
        {
            SourceIdentity identity = identities[index];
            SourceSpec expected = SourceSpecs[index];
            if (identity is null || identity.Path != expected.Path || identity.Role != expected.Role ||
                !IsSha256(identity.Sha256) || !IsSha256(identity.SyntaxSha256))
                throw new ExtractionException($"The machine source identity changed: {expected.Path}.");
            string path = Path.GetFullPath(Path.Combine(root, identity.Path));
            EnsureWithin(root, path);
            if (!File.Exists(path))
            {
                throw new ExtractionException($"The checked-in machine source does not match its manifest: {identity.Path}.");
            }

            byte[] bytes = File.ReadAllBytes(path);
            if (Sha256(bytes) != identity.Sha256)
                throw new ExtractionException($"The checked-in machine source does not match its manifest: {identity.Path}.");

            SourceText text = SourceText.From(bytes, Encoding.UTF8, canBeEmbedded: false, checksumAlgorithm: SourceHashAlgorithm.Sha256);
            SyntaxTree tree = CSharpSyntaxTree.ParseText(text, ParseOptions, identity.Path);
            CompilationUnitSyntax syntaxRoot = tree.GetCompilationUnitRoot();
            string syntaxSha = Sha256(Encoding.UTF8.GetBytes(CanonicalTokens(syntaxRoot)));
            if (!string.Equals(syntaxSha, identity.SyntaxSha256, StringComparison.Ordinal))
                throw new ExtractionException($"The machine source syntax identity changed: {identity.Path}.");
        }
    }

    private static void ValidateDependencyIdentities(string root, DependencyIdentity[] identities)
    {
        if (identities.Length != DependencySpecs.Length) throw new ExtractionException("The machine dependency identity count changed.");
        for (int index = 0; index < identities.Length; index++)
        {
            DependencyIdentity identity = identities[index];
            DependencySpec expected = DependencySpecs[index];
            if (identity is null || identity.Path != expected.Path || identity.Role != expected.Role || !IsSha256(identity.Sha256))
                throw new ExtractionException($"The machine dependency identity changed: {expected.Path}.");
            string path = Path.GetFullPath(Path.Combine(root, identity.Path));
            EnsureWithin(root, path);
            if (!File.Exists(path) || Sha256(File.ReadAllBytes(path)) != identity.Sha256)
                throw new ExtractionException($"The settled machine dependency does not match its manifest: {identity.Path}.");
        }
    }

    private static void ValidateCompilerIdentities(string root, CompilerReferenceIdentity[] identities)
    {
        ValidateCompilerIdentities(identities);
        foreach (CompilerReferenceIdentity identity in identities)
        {
            string path = ResolveReferencePath(root, identity.Path);
            if (!File.Exists(path) || Sha256(File.ReadAllBytes(path)) != identity.Sha256)
                throw new ExtractionException($"Compiler/reference manifest drift: {identity.Path}.");

            (string assemblyName, string mvid, string[] dependencies) = ReadCompilerMetadata(path, identity.Path);
            if (!string.Equals(assemblyName, identity.AssemblyName, StringComparison.Ordinal) ||
                !string.Equals(mvid, identity.Mvid, StringComparison.Ordinal) ||
                !dependencies.SequenceEqual(identity.Dependencies, StringComparer.Ordinal))
            {
                throw new ExtractionException($"Compiler/reference metadata drift: {identity.Path}.");
            }
        }
    }

    private static (string AssemblyName, string Mvid, string[] Dependencies) ReadCompilerMetadata(string fullPath, string logicalPath)
    {
        try
        {
            AssemblyName assemblyName = AssemblyName.GetAssemblyName(fullPath);
            using PEReader peReader = new(File.OpenRead(fullPath));
            MetadataReader metadata = peReader.GetMetadataReader();
            Guid moduleVersionId = metadata.GetGuid(metadata.GetModuleDefinition().Mvid);
            string[] dependencies = metadata.AssemblyReferences
                .Select(handle => metadata.GetString(metadata.GetAssemblyReference(handle).Name))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static name => name, StringComparer.Ordinal)
                .ToArray();
            return (assemblyName.Name ?? throw new InvalidDataException("The assembly has no simple name."), moduleVersionId.ToString("D"), dependencies);
        }
        catch (Exception exception) when (exception is not ExtractionException)
        {
            throw new ExtractionException($"Compiler/reference metadata is unreadable for {logicalPath}: {exception.Message}");
        }
    }

    private static void ValidateCompilerIdentities(CompilerReferenceIdentity[] identities)
    {
        if (identities is null || identities.Length != ReceiptCompilerInventoryCount)
            throw new ExtractionException("The machine compiler/reference identity count changed.");
        string[] paths = identities.Select(static identity => identity.Path).ToArray();
        string[] sorted = paths.OrderBy(static path => path, StringComparer.OrdinalIgnoreCase).ThenBy(static path => path, StringComparer.Ordinal).ToArray();
        if (!paths.SequenceEqual(sorted, StringComparer.Ordinal)) throw new ExtractionException("The machine compiler/reference path order changed.");
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (CompilerReferenceIdentity identity in identities)
        {
            if (identity is null || string.IsNullOrWhiteSpace(identity.Path) || !seen.Add(identity.Path) ||
                string.IsNullOrWhiteSpace(identity.AssemblyName) || !IsSha256(identity.Sha256) ||
                !Guid.TryParseExact(identity.Mvid, "D", out _) || !identity.Selected || identity.Dependencies is null ||
                !string.Equals(identity.AssemblyName, Path.GetFileNameWithoutExtension(identity.Path), StringComparison.OrdinalIgnoreCase))
            {
                throw new ExtractionException("The machine compiler/reference identity is incomplete.");
            }

            string normalized = Normalize(identity.Path);
            if (!string.Equals(identity.Path, normalized, StringComparison.Ordinal) ||
                normalized.Contains("../", StringComparison.Ordinal) || normalized.Contains("/..", StringComparison.Ordinal) ||
                !normalized.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                throw new ExtractionException($"The machine compiler/reference path is not canonical: {identity.Path}.");
            }

            string[] dependencies = identity.Dependencies;
            if (!dependencies.SequenceEqual(
                    dependencies.Distinct(StringComparer.Ordinal).OrderBy(static value => value, StringComparer.Ordinal),
                    StringComparer.Ordinal))
            {
                throw new ExtractionException($"The machine compiler/reference dependency closure is not canonical: {identity.Path}.");
            }
        }

        if (!string.Equals(CompilerAggregateHash(identities), ReceiptCompilerInventoryAggregateSha256, StringComparison.Ordinal))
            throw new ExtractionException("The machine compiler/reference aggregate changed.");
    }

    private static string CompilerAggregateHash(IEnumerable<CompilerReferenceIdentity> identities) =>
        Sha256(Encoding.UTF8.GetBytes(string.Join('\n', identities.Select(identity =>
            $"{identity.Path}\0{identity.AssemblyName}\0{identity.Sha256}")) + "\n"));

    private static string CombinedSourceHash(IEnumerable<SourceIdentity> identities) =>
        Sha256(Encoding.UTF8.GetBytes(string.Join('\n', identities.Select(identity =>
            $"{identity.Path}\0{identity.Role}\0{identity.Sha256}\0{identity.SyntaxSha256}")) + "\n"));

    private static T Deserialize<T>(byte[] bytes, string description)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(bytes, JsonOptions)
                ?? throw new ExtractionException($"The {description} is empty.");
        }
        catch (JsonException exception)
        {
            throw new ExtractionException($"The {description} is not canonical JSON: {exception.Message}");
        }
    }

    private static byte[] Serialize<T>(T value)
    {
        string json = JsonSerializer.Serialize(value, JsonOptions).ReplaceLineEndings("\n") + "\n";
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(json);
    }

    private static void AtomicWrite(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void EnsureWithin(string parent, string child)
    {
        string root = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string candidate = Path.GetFullPath(child);
        if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new ExtractionException($"Path escaped the intended output root: {child}.");
        }
    }

    private static string Normalize(string path) => path.Replace('\\', '/');

    private static bool IsSha256(string? value) =>
        value is not null && value.Length == 64 && value.All(Uri.IsHexDigit) && value == value.ToLowerInvariant();

    internal static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed record PinnedSource(string Path, string Role, string Sha256);
    private sealed record PinnedDependency(string Path, string Role, string Sha256);
    private sealed record PinnedCompilerInventory(string Path, string Sha256, int Count, string AggregateSha256);
    private sealed record PinsDocument(
        int SchemaVersion,
        string Status,
        string Authority,
        PinnedSource[] Sources,
        PinnedDependency[] Dependencies,
        PinnedCompilerInventory CompilerReferenceInventory);
    private sealed record DependencySpec(string Path, string Role);
    private enum LedgerMemberKind
    {
        Type,
        Method,
        Constructor,
        LocalFunction,
        DelegateInvoke,
        Field,
        EnumMember,
        PrimaryConstructorParameter,
    }

    private sealed record SourceMemberIdentity(
        string SourcePath,
        string MetadataTypeName,
        string MemberName,
        LedgerMemberKind Kind,
        int Arity,
        string[] ParameterSyntax,
        string[] RefKinds,
        string CanonicalSyntax,
        int Multiplicity);

    private sealed record SourceOwnerIdentity(
        string SourcePath,
        string MetadataTypeName,
        int Multiplicity = 1);

    private sealed record InvocationTargetIdentity(
        string Id,
        string MetadataTypeName,
        string MemberName,
        LedgerMemberKind Kind,
        int Arity,
        int ParameterCount,
        string[] RefKinds,
        string? ReceiverMetadataTypeName,
        int Multiplicity,
        string? CanonicalSyntax,
        string[]? ParameterTypeMetadataNames,
        string[]? TypeArgumentMetadataNames);

    private sealed record DeclarationIdentity(
        string Id,
        string SourcePath,
        string MetadataTypeName,
        LedgerMemberKind Kind,
        string CanonicalSyntax,
        int Multiplicity,
        string? TargetMetadataTypeName = null,
        string[]? TargetParameterMetadataTypeNames = null,
        string[]? TargetRefKinds = null);

    private sealed record AssignmentTargetIdentity(
        string Id,
        string MetadataTypeName,
        string MemberName,
        SymbolKind Kind,
        int Multiplicity = 1);

    private sealed record MemberTargetIdentity(
        string Id,
        string MetadataTypeName,
        string MemberName,
        SymbolKind Kind,
        string ReceiverMetadataTypeName,
        int Multiplicity = 1);

    private sealed class SemanticUnit(SourceFile source, CSharpCompilation compilation, SemanticModel model)
    {
        internal SourceFile Source { get; } = source;
        internal CSharpCompilation Compilation { get; } = compilation;
        internal SemanticModel Model { get; } = model;
    }

    private sealed class MethodHandle(SemanticUnit unit, MethodDeclarationSyntax syntax, IMethodSymbol symbol)
    {
        internal SemanticUnit Unit { get; } = unit;
        internal MethodDeclarationSyntax Syntax { get; } = syntax;
        internal IMethodSymbol Symbol { get; } = symbol;
        internal ControlFlowGraph? Graph { get; set; }
    }
}
