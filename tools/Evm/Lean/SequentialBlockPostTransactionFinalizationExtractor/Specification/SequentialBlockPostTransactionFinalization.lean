-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

namespace SequentialBlockPostTransactionFinalizationExtractor.Specification.SequentialBlockPostTransactionFinalization

inductive FailureReason where
| nonStandardPath
| balEnabled
| backgroundReceipts
| nonNormalReturn
| incompleteTransactionFold
  deriving DecidableEq, Repr

structure Receipt where
  id : Nat
  logCount : Nat
  deriving DecidableEq, Repr

inductive TerminalResultWitness where
| ok
| evmException (exceptionType : Nat) (substateError : Option Nat)
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

structure Input where
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

inductive Event where
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

inductive HeaderEffect where
| blobGas (value : Nat)
| receiptsRoot (value : Nat)
| stateRoot (value : Nat)
| hash (value : Nat)
  deriving DecidableEq, Repr

structure Observation where
  completed : Bool
  failure : Option FailureReason
  block : Block
  headerBefore : Header
  headerAfter : Header
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
  events : List Event
  effects : List HeaderEffect
  deriving DecidableEq, Repr

def allHookNormalReturns (returns : HookNormalReturns) : Bool :=
  returns.rewards && returns.withdrawals && returns.executionRequests && returns.calculateBlooms &&
  returns.calculateReceiptsRoot && returns.endBlockTrace && returns.accountChanges && returns.stateRoot &&
  returns.balFinalization && returns.headerHash

def expectedEvents (input : Input) : List Event :=
  [.commitNoRoots 0] ++
  (if input.spec.eip4844Enabled then [.blobGasAssigned] else []) ++
  [.receiptTaskInitializedNull, .bloomsCalculated, .receiptsRootAssigned,
   .rewardsApplied, .withdrawalsApplied, .commitNoRoots 1,
   .executionRequestsProcessed, .endBlockTrace true, .commitRoots] ++
  (if input.mainProcessingThread then [.accountChangesCaptured] else []) ++
  (if input.shouldComputeStateRoot then [.stateRootComputed] else []) ++
  [.balFinalized, .headerHashAssigned, .returnedReceipts]

def expectedHeaderEffects (input : Input) : List HeaderEffect :=
  (if input.spec.eip4844Enabled then [.blobGas input.hooks.blobGas] else []) ++
  [.receiptsRoot input.hooks.receiptsRoot] ++
  (if input.shouldComputeStateRoot then [.stateRoot input.hooks.stateRoot] else []) ++
  [.hash input.hooks.headerHash]

def applyHeaderEffect (header : Header) : HeaderEffect → Header
| .blobGas value => { header with blobGasUsed := some value }
| .receiptsRoot value => { header with receiptsRoot := some value }
| .stateRoot value => { header with stateRoot := some value }
| .hash value => { header with hash := some value }

def applyHeaderEffects (header : Header) : List HeaderEffect → Header
| [] => header
| effect :: rest => applyHeaderEffects (applyHeaderEffect header effect) rest

def normalTail (input : Input) (observation : Observation) : Prop :=
  input.standardExactBase = true ∧ input.balEnabled = false ∧ input.normalReturn = true ∧
  input.transactionsExecutedNormalReturn = true ∧ input.postTransactionCommitNormalReturn = true ∧
  input.backgroundReceipts = false ∧ input.fold.completed = true ∧
  allHookNormalReturns input.hookNormalReturns = true ∧
  observation.completed = true ∧ observation.failure = none ∧
  observation.block = input.block ∧ observation.headerBefore = input.header ∧
  observation.headerAfter = applyHeaderEffects input.header (expectedHeaderEffects input) ∧
  observation.receipts = input.receipts ∧ observation.spec = input.spec ∧
  observation.world = input.world ∧ observation.tracer = input.tracer ∧
  observation.standardHandler = input.standardHandler ∧
  observation.foldWitness = input.fold ∧ observation.hooksWitness = input.hooks ∧
  observation.hookNormalReturnsWitness = input.hookNormalReturns ∧
  observation.transactionsExecutedNormalReturnWitness = input.transactionsExecutedNormalReturn ∧
  observation.postTransactionCommitNormalReturnWitness = input.postTransactionCommitNormalReturn ∧
  observation.postTransactionCommitWitness = true ∧
  observation.events = expectedEvents input ∧
  observation.effects = expectedHeaderEffects input

def unsupportedTail (input : Input) (reason : FailureReason) (observation : Observation) : Prop :=
  observation.completed = false ∧ observation.failure = some reason ∧
  observation.block = input.block ∧ observation.headerBefore = input.header ∧
  observation.receipts = input.receipts ∧ observation.spec = input.spec ∧
  observation.world = input.world ∧ observation.tracer = input.tracer ∧
  observation.standardHandler = input.standardHandler

def eventOrder (observation : Observation) : List Event := observation.events

def receiptCount (observation : Observation) : Nat := observation.receipts.length

def logCount (observation : Observation) : Nat :=
  observation.receipts.foldl (fun total receipt => total + receipt.logCount) 0

end SequentialBlockPostTransactionFinalizationExtractor.Specification.SequentialBlockPostTransactionFinalization
