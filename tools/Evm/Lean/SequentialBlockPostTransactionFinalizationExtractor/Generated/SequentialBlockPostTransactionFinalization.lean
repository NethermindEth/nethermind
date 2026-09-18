-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

-- This file is generated. Do not edit.
-- It models only the normal synchronous post-transaction finalization tail.
-- Delegate bodies are observations; root/hash/reward/withdrawal/request/BAL bodies are not reimplemented.
-- CalculateBlooms mutates receipt observations; header-bloom mutation is intentionally omitted because BlockReceiptsTracer is outside this source closure.

namespace SequentialBlockPostTransactionFinalizationExtractor.Generated.SequentialBlockPostTransactionFinalization

inductive FailureReason where
| nonStandardPath
| balEnabled
| backgroundReceipts
| nonNormalReturn
| incompleteTransactionFold
  deriving DecidableEq, Repr

inductive OutcomeKind where
| unsupported
| completed
  deriving DecidableEq, Repr

inductive TerminalResultWitness where
| ok
| evmException (exceptionType : Nat) (substateError : Option Nat)
  deriving DecidableEq, Repr

structure Receipt where
  id : Nat
  logCount : Nat
  deriving DecidableEq, Repr

structure Header where
  id : Nat
  blobGasUsed : Option Nat
  bloom : Option Nat
  receiptsRoot : Option Nat
  stateRoot : Option Nat
  hash : Option Nat
  deriving DecidableEq, Repr

structure Block where
  id : Nat
  deriving DecidableEq, Repr

structure ReleaseSpec where
  id : Nat
  eip4844Enabled : Bool
  deriving DecidableEq, Repr

structure WorldState where
  id : Nat
  deriving DecidableEq, Repr

structure Tracer where
  id : Nat
  deriving DecidableEq, Repr

structure StandardHandler where
  id : Nat
  deriving DecidableEq, Repr

structure CompletedTransactionFold where
  completed : Bool
  receiptCount : Nat
  logCount : Nat
  terminalReceiptProjection : List Receipt
  terminalLogProjection : List Nat
  terminalResults : List TerminalResultWitness
  completedIndices : List Nat
  deriving DecidableEq, Repr

structure HookObservations where
  blobGas : Nat
  bloom : Nat
  receiptsRoot : Nat
  rewards : Nat
  withdrawals : Nat
  executionRequests : Nat
  endBlockTrace : Nat
  accountChanges : Nat
  stateRoot : Nat
  balFinalized : Nat
  headerHash : Nat
  deriving DecidableEq, Repr

structure HookNormalReturns where
  rewards : Bool
  withdrawals : Bool
  executionRequests : Bool
  calculateBlooms : Bool
  calculateReceiptsRoot : Bool
  endBlockTrace : Bool
  accountChanges : Bool
  stateRoot : Bool
  balFinalization : Bool
  headerHash : Bool
  deriving DecidableEq, Repr

structure FinalizationInput where
  block : Block
  header : Header
  receipts : List Receipt
  spec : ReleaseSpec
  world : WorldState
  tracer : Tracer
  standardHandler : StandardHandler
  fold : CompletedTransactionFold
  hooks : HookObservations
  hookNormalReturns : HookNormalReturns
  standardExactBase : Bool
  balEnabled : Bool
  normalReturn : Bool
  transactionsExecutedNormalReturn : Bool
  postTransactionCommitNormalReturn : Bool
  backgroundReceipts : Bool
  mainProcessingThread : Bool
  shouldComputeStateRoot : Bool
  deriving DecidableEq, Repr

inductive FinalizationEvent where
| commitNoRoots (ordinal : Nat)
| blobGasAssigned
| receiptTaskInitializedNull
| bloomsCalculated
| receiptsRootAssigned
| rewardsApplied
| withdrawalsApplied
| executionRequestsProcessed
| endBlockTrace (accumulateBlockBloom : Bool)
| commitRoots
| accountChangesCaptured
| stateRootComputed
| balFinalized
| headerHashAssigned
| returnedReceipts
  deriving DecidableEq, Repr

structure FinalizationResult where
  outcome : OutcomeKind
  block : Block
  header : Header
  receipts : List Receipt
  spec : ReleaseSpec
  world : WorldState
  tracer : Tracer
  standardHandler : StandardHandler
  foldWitness : CompletedTransactionFold
  hooksWitness : HookObservations
  hookNormalReturnsWitness : HookNormalReturns
  transactionsExecutedNormalReturnWitness : Bool
  postTransactionCommitNormalReturnWitness : Bool
  postTransactionCommitWitness : Bool
  events : List FinalizationEvent
  mainProcessingThread : Bool
  stateRootGuard : Bool
  eip4844Guard : Bool
  backgroundGuardFalse : Bool
  balDisabled : Bool
  deriving DecidableEq, Repr

inductive FinalizationOutcome where
| unsupported (reason : FailureReason)
| completed (result : FinalizationResult)
  deriving DecidableEq, Repr

def allHookNormalReturns (returns : HookNormalReturns) : Bool :=
  returns.rewards && returns.withdrawals && returns.executionRequests && returns.calculateBlooms &&
  returns.calculateReceiptsRoot && returns.endBlockTrace && returns.accountChanges && returns.stateRoot &&
  returns.balFinalization && returns.headerHash

def run (input : FinalizationInput) : FinalizationOutcome :=
  if !input.standardExactBase then
    .unsupported .nonStandardPath
  else if input.balEnabled then
    .unsupported .balEnabled
  else if input.normalReturn = false then
    .unsupported .nonNormalReturn
  else if input.transactionsExecutedNormalReturn = false then
    .unsupported .nonNormalReturn
  else if input.postTransactionCommitNormalReturn = false then
    .unsupported .nonNormalReturn
  else if allHookNormalReturns input.hookNormalReturns = false then
    .unsupported .nonNormalReturn
  else if input.backgroundReceipts then
    .unsupported .backgroundReceipts
  else if input.fold.completed = false then
    .unsupported .incompleteTransactionFold
  else
    let headerAfterBlob :=
      if input.spec.eip4844Enabled then
        { input.header with blobGasUsed := some input.hooks.blobGas }
      else
        input.header
    let headerAfterReceipts :=
      { headerAfterBlob with
        receiptsRoot := some input.hooks.receiptsRoot }
    let headerAfterStateRoot :=
      if input.shouldComputeStateRoot then
        { headerAfterReceipts with stateRoot := some input.hooks.stateRoot }
      else
        headerAfterReceipts
    let finalHeader := { headerAfterStateRoot with hash := some input.hooks.headerHash }
    let events :=
      [.commitNoRoots 0] ++
      (if input.spec.eip4844Enabled then [.blobGasAssigned] else []) ++
      [.receiptTaskInitializedNull, .bloomsCalculated, .receiptsRootAssigned,
       .rewardsApplied, .withdrawalsApplied, .commitNoRoots 1,
       .executionRequestsProcessed, .endBlockTrace true, .commitRoots] ++
      (if input.mainProcessingThread then [.accountChangesCaptured] else []) ++
      (if input.shouldComputeStateRoot then [.stateRootComputed] else []) ++
      [.balFinalized, .headerHashAssigned, .returnedReceipts]
    .completed
      { outcome := .completed
        block := input.block
        header := finalHeader
        receipts := input.receipts
        spec := input.spec
        world := input.world
        tracer := input.tracer
        standardHandler := input.standardHandler
        foldWitness := input.fold
        hooksWitness := input.hooks
        hookNormalReturnsWitness := input.hookNormalReturns
        transactionsExecutedNormalReturnWitness := input.transactionsExecutedNormalReturn
        postTransactionCommitNormalReturnWitness := input.postTransactionCommitNormalReturn
        postTransactionCommitWitness := true
        events := events
        mainProcessingThread := input.mainProcessingThread
        stateRootGuard := input.shouldComputeStateRoot
        eip4844Guard := input.spec.eip4844Enabled
        backgroundGuardFalse := !input.backgroundReceipts
        balDisabled := !input.balEnabled }

def completed (input : FinalizationInput) : Bool :=
  match run input with
  | .completed _ => true
  | .unsupported _ => false

def receiptCount (result : FinalizationResult) : Nat := result.receipts.length

def logCount (result : FinalizationResult) : Nat :=
  result.receipts.foldl (fun total receipt => total + receipt.logCount) 0

end SequentialBlockPostTransactionFinalizationExtractor.Generated.SequentialBlockPostTransactionFinalization
-- Source-bound typed identities admitted by the extractor:
-- block.post-transaction-commit
-- block.blob-gas-guard
-- block.blob-gas-calculation
-- block.background-task-null
-- block.receipts-background-guard
-- block.sync-blooms
-- block.sync-receipts-root
-- block.rewards
-- block.withdrawals
-- block.finalization-commit
-- block.execution-requests
-- block.end-block-trace
-- block.storage-roots-commit
-- block.main-thread-guard
-- block.account-changes
-- block.state-root-guard
-- block.state-root
-- block.background-result-guard
-- block.bal-finalization
-- block.background-finally-guard
-- block.hash
-- block.return-receipts
-- Typed commit identities:
-- commitNoRoots(0): post-transaction-no-roots @ CommitState(spec) -> _stateProvider.Commit(spec,commitRoots:false)
-- commitNoRoots(1): finalization-no-roots @ CommitState(spec) -> _stateProvider.Commit(spec,commitRoots:false)
-- commitRoots: storage-roots @ CommitStateAndStorageRoots(spec) -> _stateProvider.Commit(spec,commitRoots:true)
-- Complete source-local preservation closure (external-hook premises do not replace these checks):
-- global::Nethermind.Consensus.Processing.BlockProcessor.ProcessBlock(Nethermind.Core.Block,Nethermind.Evm.Tracing.IBlockTracer,Nethermind.Consensus.Processing.ProcessingOptions,Nethermind.Core.Specs.IReleaseSpec,System.Threading.CancellationToken) executable-body=80d2f26d89842515ead4ad275d0ecd424df2395b80ba3b30569a280cf9080bb2
-- global::Nethermind.Consensus.Processing.BlockProcessor.AccumulateBlockBloom(Nethermind.Core.TxReceipt[]) @ src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs body=add2837433c5d3d18d5ddc670aa36d5c73d024a1b73497c5409d9c3ea733ab5e
-- global::Nethermind.Consensus.Processing.BlockProcessor.ApplyMinerReward(Nethermind.Consensus.Rewards.BlockReward,Nethermind.Core.Specs.IReleaseSpec) @ src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs body=9243e1988ea538748bb87c7e6f2f2c488db6b3ae81a72703815a4dd432037330
-- global::Nethermind.Consensus.Processing.BlockProcessor.ApplyMinerRewards(Nethermind.Core.Block,Nethermind.Evm.Tracing.IBlockTracer,Nethermind.Core.Specs.IReleaseSpec) @ src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs body=80bd12efcd7c533307f6cd2da28554bd331ab164b7578e341297325d8a81a654
-- global::Nethermind.Consensus.Processing.BlockProcessor.CalculateBlooms(Nethermind.Core.TxReceipt[]) @ src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs body=52628ec2ee27c2ebb75e5cd5f7c527aa50ee68080cd59631f2965c007e8a1318
-- global::Nethermind.Consensus.Processing.BlockProcessor.CalculateReceiptsRoot(Nethermind.Core.TxReceipt[],Nethermind.Core.Specs.IReleaseSpec,Nethermind.Core.Block) @ src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs body=c7a64338bbe5f3eabc9fe9c6699db88848e6be56af62255e4fb33d81afa6f345
-- global::Nethermind.Consensus.Processing.BlockProcessor.CommitState(Nethermind.Core.Specs.IReleaseSpec) @ src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs body=7cd46a953aa3d5524db0883358b8e629b0741c548ee1e1e7f5e609a68c523d82
-- global::Nethermind.Consensus.Processing.BlockProcessor.CommitStateAndStorageRoots(Nethermind.Core.Specs.IReleaseSpec) @ src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs body=f366476bb0490f6c788bce0ba7f1ea1bed830427716029b16cddd3f4eebc90f6
-- global::Nethermind.Consensus.Processing.BlockProcessor.ComputeStateRoot(Nethermind.Core.BlockHeader) @ src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs body=f2b6c4f3eed0f57f712d044a55154259ed680ae03a3bfd6605cd4136cb42a219
-- global::Nethermind.Consensus.Processing.BlockProcessor.CountLogs(Nethermind.Core.TxReceipt[]) @ src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs body=9e6ae117ecbd4d97a3f8aa86bfd19fadf367d390f2f59087ddc639d0fe722f06
-- global::Nethermind.Consensus.Processing.BlockProcessor.CreateBlockExecutionContext(Nethermind.Core.BlockHeader,Nethermind.Core.Specs.IReleaseSpec) @ src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs body=c6061361cfaaa61961b114b89dc835d8b91697fd45c55d9633c8d02b113bb456
-- global::Nethermind.Consensus.Processing.BlockProcessor.SetAccountChanges(Nethermind.Core.Block) @ src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs body=8ec2b585964311e79203d574baf4ce92d5e9a436b6b8955eb8741652f5af5843
-- global::Nethermind.Consensus.Processing.BlockProcessor.ShouldCalculateReceiptsInBackground(Nethermind.Core.TxReceipt[]) @ src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.std.cs body=35f9710b6403354d97203b8a383dc01fe5a9e90fd094628fb2e985cad2dccede
-- global::Nethermind.Consensus.Processing.BlockProcessor.ShouldComputeStateRoot(Nethermind.Core.BlockHeader) @ src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs body=77e2a33c3428bde4e2f8e056ddd3c3f892d0092ed008d5f56a4957cd0ec13bca
-- global::Nethermind.Consensus.Processing.BlockProcessor.TraceMinerReward(Nethermind.Consensus.Rewards.BlockReward) @ src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs body=54e75525b573c26bb4d97e794e44eedc04cb641ae64dfa6f001229f51702b77c
-- Instrumentation wrapper: src/Nethermind/Nethermind.Core/Metric/MetricsTimer.cs source=9e72b71ff14688b9dfb16f5a09db1263153440154e5a1886e4f949f3ab5c4ca1
-- global::Nethermind.Consensus.Processing.BlockProcessor.CalculateBlooms(Nethermind.Core.TxReceipt[]) -> global::Nethermind.Consensus.Processing.BlockProcessor.BloomsTimeSink declaration=2666099d09fb638d2ec053386f7aead33b21e3d43a5a79a86db0a474cba11c31
-- global::Nethermind.Consensus.Processing.BlockProcessor.CommitState(Nethermind.Core.Specs.IReleaseSpec) -> global::Nethermind.Consensus.Processing.BlockProcessor.CommitTimeSink declaration=5c87be1345df77b9abf2ce6ca7fd7e15568a89ef5d853927de028bf578357e3e
-- global::Nethermind.Consensus.Processing.BlockProcessor.CalculateReceiptsRoot(Nethermind.Core.TxReceipt[],Nethermind.Core.Specs.IReleaseSpec,Nethermind.Core.Block) -> global::Nethermind.Consensus.Processing.BlockProcessor.ReceiptsRootTimeSink declaration=8c2b60cd60f4cfd7010b71b82eaac4cd35c0cc5e00d960d957cf60be7a9eba21
-- global::Nethermind.Consensus.Processing.BlockProcessor.ComputeStateRoot(Nethermind.Core.BlockHeader) -> global::Nethermind.Consensus.Processing.BlockProcessor.StateRootTimeSink declaration=0900a60eba60f797d396d80e732bd104cdb6a35931988164993acbd82b6a051f
-- global::Nethermind.Consensus.Processing.BlockProcessor.CommitStateAndStorageRoots(Nethermind.Core.Specs.IReleaseSpec) -> global::Nethermind.Consensus.Processing.BlockProcessor.StorageMerkleTimeSink declaration=3dd8e64b1a6692574a778f925a15340615f1f9e8fe3a3b0751d1494c854db39a
-- Typed selected-arm header assignments:
-- header.blob-gas-used [true-arm] header.BlobGasUsed = BlobGasCalculator.CalculateBlobGas(block.Transactions)
-- header.receipts-root [false-arm] header.ReceiptsRoot = CalculateReceiptsRoot(receipts,spec,block)
-- header.receipts-root excludes (header.Bloom,header.ReceiptsRoot)=bloomsAndReceiptsRootTask.GetAwaiter().GetResult()
-- Ordered source observables:
-- 0: post-transaction-commit [always] commit-no-roots
-- 1: blob-gas-if-enabled [spec.IsEip4844Enabled] header.BlobGasUsed
-- 2: receipt-task-null [always] background-task=null
-- 3: synchronous-blooms [background=false] receipt-blooms
-- 4: synchronous-receipts-root [background=false] header.ReceiptsRoot
-- 5: rewards [normal-return] rewards-hook
-- 6: withdrawals [normal-return] withdrawals-hook
-- 7: finalization-commit [always] commit-no-roots
-- 8: execution-requests [normal-return] execution-requests-hook
-- 9: end-block-trace-true [background=false] EndBlockTrace(true)
-- 10: storage-roots-commit [always] commit-roots
-- 11: main-thread-account-changes [main-thread=true] account-changes
-- 12: state-root-if-enabled [state-root=true] state-root
-- 13: bal-finalization [BAL-disabled] BAL-finalization-observation
-- 14: header-hash [normal-return] header.Hash
-- 15: return-receipts [normal-return] return receipts
-- Compiler closure: tools/Evm/Lean/ReceiptTerminalFoldExtractor/COMPILER_REFERENCE_PINS.json count=434 aggregate=6fdfa102a4190083ae62080355aa6691b5f46acc32d4f9da2212012686d9c2e2 inventory=1f73a3800a957dd02d9d7b06b3b955c81bb92fd9eb8d2831920905394bfc1d5c
-- Source-entry adapter: static-layout-only; runtime identity and guard premises remain explicit.
-- block.processBlock.standard-exact-base-adapter claim=static-layout-only; runtime caller supplies exact-base receiver, selected standard executor, BAL-disabled, named external normal-return/no-additional-effect premises, and separate TransactionsExecuted/post-transaction-CommitState normal-return premises exactBase=BlockProcessor.ProcessBlock exact base receiver executor=_blockTransactionsExecutor.ProcessTransactions(block,options,ReceiptsTracer,token) bal=runtime premise: BAL disabled background=runtime premise: ShouldCalculateReceiptsInBackground(receipts)=false mainThread=runtime premise: BlockchainProcessor.IsMainProcessingThread is selected as observed stateRoot=runtime premise: ShouldComputeStateRoot(header) is selected as observed spec=runtime premise: spec is the release-spec receiver used by the selected standard path transactionsExecutedNormalReturn=runtime premise: TransactionsExecuted subscribers return normally postTransactionCommitNormalReturn=runtime premise: post-transaction CommitState(spec) returns normally transactionsExecutedEvent=global::Nethermind.Consensus.Processing.BlockProcessor.TransactionsExecuted transactionsExecutedBinding=TransactionsExecuted?.Invoke() postTransactionCommitBinding=CommitState(spec) stateRootWrite=header.StateRoot=_stateProvider.StateRoot stateRootValue=_stateProvider.StateRoot accountChangesWrite=block.AccountChanges=_stateProvider.GetAccountChanges() accountChangesValue=_stateProvider.GetAccountChanges() receiptsReferences=8 receiptsWrites=1
-- Header identity: header=block.Header <- global::Nethermind.Core.Block.Header
-- Preserved value: header symbol=global::Nethermind.Consensus.Processing.BlockProcessor.ProcessBlock.header definition=header=block.Header reads=9
-- Preserved value: block symbol=global::Nethermind.Consensus.Processing.BlockProcessor.ProcessBlock(Nethermind.Core.Block,Nethermind.Evm.Tracing.IBlockTracer,Nethermind.Consensus.Processing.ProcessingOptions,Nethermind.Core.Specs.IReleaseSpec,System.Threading.CancellationToken).0 definition=Blockblock reads=15
-- Preserved value: spec symbol=global::Nethermind.Consensus.Processing.BlockProcessor.ProcessBlock(Nethermind.Core.Block,Nethermind.Evm.Tracing.IBlockTracer,Nethermind.Consensus.Processing.ProcessingOptions,Nethermind.Core.Specs.IReleaseSpec,System.Threading.CancellationToken).3 definition=IReleaseSpecspec reads=13
-- Preserved value: blockTracer symbol=global::Nethermind.Consensus.Processing.BlockProcessor.ProcessBlock(Nethermind.Core.Block,Nethermind.Evm.Tracing.IBlockTracer,Nethermind.Consensus.Processing.ProcessingOptions,Nethermind.Core.Specs.IReleaseSpec,System.Threading.CancellationToken).1 definition=IBlockTracerblockTracer reads=2
-- Preserved value: _stateProvider symbol=global::Nethermind.Consensus.Processing.BlockProcessor._stateProvider definition=_stateProvider=stateProvider reads=1
-- Preserved value: _systemContractHandler symbol=global::Nethermind.Consensus.Processing.BlockProcessor._systemContractHandler definition=_systemContractHandler reads=4
-- Preserved value: ReceiptsTracer symbol=global::Nethermind.Consensus.Processing.BlockProcessor.ReceiptsTracer definition=protectedBlockReceiptsTracerReceiptsTracer{get;set;}=new(); reads=4
-- Task flow: bloomsAndReceiptsRootTask localSymbol=global::Nethermind.Consensus.Processing.BlockProcessor.ProcessBlock.bloomsAndReceiptsRootTask backgroundAssignments=1 synchronousAssignments=0 nullPreserved=True backgroundResultExcluded=True finallyObservationExcluded=True
