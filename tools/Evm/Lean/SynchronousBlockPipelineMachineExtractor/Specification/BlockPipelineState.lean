-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

namespace SynchronousBlockPipelineMachineExtractor.Specification

abbrev Address := String
abbrev Hash := String
abbrev Bytes := List UInt8

inductive Phase where
  | suggestedBlockValidation
  | synchronousBranchSelectionAndPreparation
  | senderAndAuthorityRecovery
  | openWorldStateScope
  | daoTransition
  | beaconRootSystemCall
  | historicalBlockhashStateChange
  | userTransactionFold
  | blobGasReceiptRootAndBloom
  | rewards
  | withdrawals
  | executionRequestsAndSystemCalls
  | storageAndStateRoots
  | blockAccessList
  | processedHeaderValidation
  | commitTreeInvocation
  | synchronousResultClassificationAndHeadFinalization
  deriving DecidableEq, Repr

def Phase.all : List Phase :=
  [.suggestedBlockValidation,
   .synchronousBranchSelectionAndPreparation,
   .senderAndAuthorityRecovery,
   .openWorldStateScope,
   .daoTransition,
   .beaconRootSystemCall,
   .historicalBlockhashStateChange,
   .userTransactionFold,
   .blobGasReceiptRootAndBloom,
   .rewards,
   .withdrawals,
   .executionRequestsAndSystemCalls,
   .storageAndStateRoots,
   .blockAccessList,
   .processedHeaderValidation,
   .commitTreeInvocation,
   .synchronousResultClassificationAndHeadFinalization]

def Phase.label : Phase → String
  | .suggestedBlockValidation => "suggested-block validation"
  | .synchronousBranchSelectionAndPreparation => "synchronous branch selection and preparation"
  | .senderAndAuthorityRecovery => "sender and EIP-7702 authority recovery"
  | .openWorldStateScope => "open world-state scope at parent root"
  | .daoTransition => "DAO transition when applicable"
  | .beaconRootSystemCall => "beacon-root system call"
  | .historicalBlockhashStateChange => "historical blockhash state change"
  | .userTransactionFold => "user transaction fold"
  | .blobGasReceiptRootAndBloom => "blob gas and receipt root and bloom"
  | .rewards => "rewards"
  | .withdrawals => "withdrawals"
  | .executionRequestsAndSystemCalls => "execution requests and system calls"
  | .storageAndStateRoots => "storage and state roots"
  | .blockAccessList => "block access list"
  | .processedHeaderValidation => "processed-header validation"
  | .commitTreeInvocation => "CommitTree invocation"
  | .synchronousResultClassificationAndHeadFinalization =>
      "synchronous result classification and head/processed-chain finalization"

def Phase.processOne : List Phase :=
  [.daoTransition,
   .beaconRootSystemCall,
   .historicalBlockhashStateChange,
   .userTransactionFold,
   .blobGasReceiptRootAndBloom,
   .rewards,
   .withdrawals,
   .executionRequestsAndSystemCalls,
   .storageAndStateRoots,
   .blockAccessList,
   .processedHeaderValidation]

def Phase.branch : List Phase :=
  [.openWorldStateScope,
   .daoTransition,
   .beaconRootSystemCall,
   .historicalBlockhashStateChange,
   .userTransactionFold,
   .blobGasReceiptRootAndBloom,
   .rewards,
   .withdrawals,
   .executionRequestsAndSystemCalls,
   .storageAndStateRoots,
   .blockAccessList,
   .processedHeaderValidation,
   .commitTreeInvocation]

def Phase.chain : List Phase :=
  [.suggestedBlockValidation,
   .synchronousBranchSelectionAndPreparation,
   .senderAndAuthorityRecovery,
   .openWorldStateScope,
   .processedHeaderValidation,
   .commitTreeInvocation,
   .synchronousResultClassificationAndHeadFinalization]

inductive Outcome where
  | accepted
  | skipped
  | rejectedInvalidBlock
  | escaped
  deriving DecidableEq, Repr

inductive InvalidBlockReason where
  | transactionFailure
  | blockGasLimit
  | processedHeader
  | receiptRoot
  | stateRoot
  | blockAccessList
  deriving DecidableEq, Repr

inductive EscapingException where
  | genericHook
  | cancellation
  | backgroundReceipt
  | scope
  | commitTree
  | missingParentData
  | headFinalization
  deriving DecidableEq, Repr

inductive SkipReason where
  | unknownParent
  | notBetterThanHead
  | emptyBranch
  deriving DecidableEq, Repr

inductive Failure where
  | invalidBlock (reason : InvalidBlockReason)
  | escaping (reason : EscapingException)
  | skipped (reason : SkipReason)
  deriving DecidableEq, Repr

structure GasCounters where
  executionGas : Nat
  stateGas : Nat
  cumulativePaidGas : Nat
  headerGasUsed : Nat
  deriving DecidableEq, Repr

def GasCounters.withHeaderMaximum (executionGas stateGas cumulativePaidGas : Nat) : GasCounters :=
  { executionGas := executionGas
    stateGas := stateGas
    cumulativePaidGas := cumulativePaidGas
    headerGasUsed := max executionGas stateGas }

structure LogObservation where
  address : Address
  topics : List Hash
  data : Bytes
  deriving DecidableEq, Repr

structure ReceiptObservation where
  transactionId : Hash
  status : String
  gasUsed : Nat
  cumulativeGasUsed : Nat
  logs : List LogObservation
  logsBloom : Hash
  contractAddress : Option Address
  deriving DecidableEq, Repr

inductive ReceiptArtifactMode where
  | synchronous
  | background
  deriving DecidableEq, Repr

structure ReceiptArtifactObservation where
  mode : ReceiptArtifactMode
  receiptsUpdatedBeforeRoot : Bool
  rootInstalledBeforeTraceEnd : Bool
  backgroundTaskAwaited : Bool
  failureObserved : Bool
  deriving DecidableEq, Repr

structure HeaderObservation where
  parentHash : Hash
  unclesHash : Hash
  beneficiary : Address
  author : Option Address
  stateRoot : Hash
  transactionsRoot : Hash
  receiptsRoot : Hash
  bloom : Hash
  difficulty : Nat
  number : Nat
  gasUsed : Nat
  gasLimit : Nat
  timestamp : Nat
  extraData : Bytes
  mixHash : Hash
  nonce : Hash
  totalDifficulty : Nat
  baseFeePerGas : Option Nat
  withdrawalsRoot : Option Hash
  parentBeaconBlockRoot : Option Hash
  requestsHash : Option Hash
  blockAccessListHash : Option Hash
  blobGasUsed : Option Nat
  excessBlobGas : Option Nat
  isPostMerge : Bool
  slotNumber : Option Nat
  deriving DecidableEq, Repr

structure ProcessingHeaderObservation where
  proposed : HeaderObservation
  processing : HeaderObservation
  resetFields : List String
  preservedFields : List String
  deriving DecidableEq, Repr

structure LogicalState where
  stateToken : Hash
  journalVersion : Nat
  accountChanges : List Hash
  deriving DecidableEq, Repr

structure ScopeState where
  isOpen : Bool
  externallyOwned : Bool
  baseBlock : Option Hash
  depth : Nat
  generation : Nat
  deriving DecidableEq, Repr

structure TransactionObservation where
  transactionId : Hash
  isSystem : Bool
  paidGas : Nat
  executionGas : Nat
  stateGas : Nat
  receipt : ReceiptObservation
  deriving DecidableEq, Repr

structure BlockWorld where
  logicalState : LogicalState
  scope : ScopeState
  gas : GasCounters
  header : ProcessingHeaderObservation
  receipts : List ReceiptObservation
  receiptArtifacts : ReceiptArtifactObservation
  requestsHash : Option Hash
  blockAccessListHash : Option Hash
  stateRoot : Option Hash
  inclusionListSatisfied : Option Bool
  trace : List String
  observations : List String
  deriving DecidableEq, Repr

structure BlockInput where
  blockId : Hash
  parentBlock : Option Hash
  isGenesis : Bool
  isEligible : Bool
  simpleChecksPass : Bool
  preprocessed : Bool
  readOnly : Bool
  proposedHeader : HeaderObservation
  transactions : List TransactionObservation
  options : List String
  deriving DecidableEq, Repr

structure PipelineFailure where
  kind : Failure
  message : String
  caughtAsInvalidBlock : Bool
  failedIndex : Option Nat
  scopeRestored : Bool
  commitTreeAttempted : Bool
  completedEffects : List String
  deriving DecidableEq, Repr

structure BlockPipelineResult where
  outcome : Outcome
  returnedWorld : BlockWorld
  failure : Option PipelineFailure
  completedPhases : List Phase
  commitTreeInvoked : Bool
  deriving DecidableEq, Repr

inductive ScopeOperation where
  | opened
  | genesisExternallyOwned
  | disposedForRetry
  | reopenedForRetry
  | disposedAtCheckpoint
  | reopenedAtCheckpoint
  | resetAfterCommit
  | disposedAtBranchEnd
  deriving DecidableEq, Repr

structure ScopeEvent where
  operation : ScopeOperation
  blockId : Hash
  index : Option Nat
  generation : Nat
  deriving DecidableEq, Repr

structure ParallelAttempt where
  eligible : Bool
  wasAttempted : Bool
  completed : Bool
  retryableBalFailure : Bool
  sequentialRetryForced : Bool
  failedAttemptEffects : List String
  scopeEvents : List ScopeEvent
  deriving DecidableEq, Repr

structure BranchPipelineResult where
  outcome : Outcome
  returnedWorld : BlockWorld
  committedPrefixWorld : BlockWorld
  blocks : List BlockPipelineResult
  scopeEvents : List ScopeEvent
  failure : Option PipelineFailure
  parallelAttempt : Option ParallelAttempt
  failedIndex : Option Nat
  commitTreeInvocationCount : Nat
  deriving DecidableEq, Repr

inductive ChainFinalizationStep where
  | totalDifficultyAssigned
  | mainChainUpdateAttempted
  | markProcessedAttempted
  deriving DecidableEq, Repr

structure ChainFinalizationObservation where
  totalDifficultyUpdated : Bool
  mainChainUpdateReturnedTrue : Bool
  markProcessedInvoked : Bool
  markProcessedCompleted : Bool
  completedSteps : List ChainFinalizationStep
  effects : List String
  escapedFailure : Option PipelineFailure
  deriving DecidableEq, Repr

inductive ChainPipelineResult where
  | preBranch
      (outcome : Outcome)
      (finalization : Option ChainFinalizationObservation)
      (trace : List Phase)
      (failure : Option PipelineFailure)
  | branched
      (outcome : Outcome)
      (branch : BranchPipelineResult)
      (finalization : Option ChainFinalizationObservation)
      (trace : List Phase)
      (failure : Option PipelineFailure)
  deriving DecidableEq, Repr

inductive FailureDisposition where
  | signal
  | rejectInvalidBlock
  | retrySequential
  | escape
  deriving DecidableEq, Repr

inductive HookId where
  | suggestedBlockSimpleChecks
  | evaluateEligibility
  | selectAndPrepareBranch
  | recoverSignatures
  | recoverSignatureImplementation
  | recoverAuthorities
  | beginBranchScope
  | beginGenesisScope
  | prepareBal
  | selectSystemContractHandler
  | applyDaoTransition
  | prepareBlockForProcessing
  | setOtherTracer
  | startBlockTrace
  | setBlockExecutionContext
  | setupBlockAccessList
  | storeBeaconRoot
  | applyBlockhashStateChanges
  | commitPreTransactionState
  | executeTransactionFold
  | transactionsExecutedSignal
  | commitPostTransactionState
  | buildReceiptsAndCumulativePaidGas
  | calculateBlobGas
  | calculateReceiptBlooms
  | accumulateBlockBloom
  | calculateReceiptsRoot
  | installSynchronousReceiptArtifacts
  | scheduleBackgroundReceiptArtifacts
  | awaitBackgroundReceiptArtifacts
  | applyMinerRewards
  | processWithdrawals
  | commitPostSystemState
  | processExecutionRequests
  | endBlockTrace
  | commitStorageAndStateRoots
  | setAccountChanges
  | computeStateRoot
  | finalizeBal
  | validateProcessedBlock
  | disposeAccountChanges
  | disposeRetryScope
  | reopenRetryScope
  | disposeCheckpointScope
  | reopenCheckpointScope
  | waitPrewarm
  | commitTree
  | resetScope
  | disposeBranchScope
  | recordInclusionListSignal
  | incrementSuccessfulPrefix
  | catchBranchException
  | completeBranchProcessing
  | classifySynchronousResult
  | updateTotalDifficulty
  | tryUpdateMainChain
  | markChainAsProcessed
  deriving DecidableEq, Repr

structure HookDependency where
  hook : HookId
  phase : Phase
  productionPath : String
  productionSymbol : String
  inputShape : String
  outputShape : String
  failureDisposition : FailureDisposition
  mayMutateLogicalState : Bool
  receiptDependent : Bool
  requiresScope : Bool
  deriving DecidableEq, Repr

def openBoundaries : List String :=
  ["parallel transaction execution versus sequential execution and BAL equivalence",
   "ordinary EVM frame loop, world-state journals, and transaction semantic adapters",
   "precompile and cryptographic semantics",
   "receipt settlement object representation and exact UInt256/width behavior",
   "receipt bloom hashing, receipt-root trie/RLP hashing, state-root trie hashing, and header hashing",
   "database persistence, CommitTree durability, crash recovery, and restart behavior",
   "background receipt task scheduling, thread interleavings, cancellation and exception precedence",
   "DI container resolution, virtual dispatch, Fody/CLR/JIT/compiler correctness",
   "block access-list production/validation equivalence",
   "suggested-block and sender/EIP-7702 preprocessing semantic adapters",
   "DAO, beacon-root, historical-blockhash, rewards, withdrawals, execution-request and system-call semantics",
   "head selection, total-difficulty representation, and processed-chain persistence",
   "async queue scheduling and concurrency outside the synchronous route"]

end SynchronousBlockPipelineMachineExtractor.Specification
