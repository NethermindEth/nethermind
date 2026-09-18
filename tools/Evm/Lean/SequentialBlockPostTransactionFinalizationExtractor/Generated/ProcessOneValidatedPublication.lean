-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

-- Generated source-attached transition. Object identifiers describe identity, not mutable contents.
-- Escapes intentionally have no final-state assertion: external calls may partially mutate state.
namespace SequentialBlockPostTransactionFinalizationExtractor.Generated.ProcessOneValidatedPublication

def acceptanceState : String := "source-admitted"
def semanticIrSha256 : String := "201d8d2e6163d3a75b439cebf0e5fa7754792c3ba63de70b246939bc9d4cad3a"
def sourceClosureSha256 : String := "c4f8fe9960f5d8205b0a89fd4ecaeb9df06c790c23f02680d253d3bd4b28bc85"

structure SourceSite where
  id : String
  path : String
  owner : String
  target : String
  position : Nat
  cfgBlock : Int
  deriving DecidableEq, Repr

def sourceSites : List SourceSite :=
  [{ id := "processOne.processBlock-return", path := "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", owner := "(global::Nethermind.Core.Block Block, global::Nethermind.Core.TxReceipt[] Receipts) global::Nethermind.Consensus.Processing.BlockProcessor.ProcessOne(global::Nethermind.Core.Block, global::Nethermind.Consensus.Processing.ProcessingOptions, global::Nethermind.Evm.Tracing.IBlockTracer, global::Nethermind.Core.Specs.IReleaseSpec, global::System.Threading.CancellationToken)", target := "global::Nethermind.Core.TxReceipt[] global::Nethermind.Consensus.Processing.BlockProcessor.ProcessBlock(global::Nethermind.Core.Block, global::Nethermind.Evm.Tracing.IBlockTracer, global::Nethermind.Consensus.Processing.ProcessingOptions, global::Nethermind.Core.Specs.IReleaseSpec, global::System.Threading.CancellationToken)", position := 3805, cfgBlock := 9 },
  { id := "processOne.processed-commit", path := "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", owner := "(global::Nethermind.Core.Block Block, global::Nethermind.Core.TxReceipt[] Receipts) global::Nethermind.Consensus.Processing.BlockProcessor.ProcessOne(global::Nethermind.Core.Block, global::Nethermind.Consensus.Processing.ProcessingOptions, global::Nethermind.Evm.Tracing.IBlockTracer, global::Nethermind.Core.Specs.IReleaseSpec, global::System.Threading.CancellationToken)", target := "bool processed", position := 3873, cfgBlock := 9 },
  { id := "processOne.retry-access-list-catch", path := "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", owner := "(global::Nethermind.Core.Block Block, global::Nethermind.Core.TxReceipt[] Receipts) global::Nethermind.Consensus.Processing.BlockProcessor.ProcessOne(global::Nethermind.Core.Block, global::Nethermind.Consensus.Processing.ProcessingOptions, global::Nethermind.Evm.Tracing.IBlockTracer, global::Nethermind.Core.Specs.IReleaseSpec, global::System.Threading.CancellationToken)", target := "global::Nethermind.State.BlockAccessListBasedWorldState.InvalidBlockLevelAccessListException", position := 3994, cfgBlock := 10 },
  { id := "processOne.retry-parallel-catch", path := "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", owner := "(global::Nethermind.Core.Block Block, global::Nethermind.Core.TxReceipt[] Receipts) global::Nethermind.Consensus.Processing.BlockProcessor.ProcessOne(global::Nethermind.Core.Block, global::Nethermind.Consensus.Processing.ProcessingOptions, global::Nethermind.Evm.Tracing.IBlockTracer, global::Nethermind.Core.Specs.IReleaseSpec, global::System.Threading.CancellationToken)", target := "global::Nethermind.Consensus.Processing.BlockAccessListManager.ParallelExecutionException", position := 4207, cfgBlock := 12 },
  { id := "processOne.disposal-finally", path := "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", owner := "(global::Nethermind.Core.Block Block, global::Nethermind.Core.TxReceipt[] Receipts) global::Nethermind.Consensus.Processing.BlockProcessor.ProcessOne(global::Nethermind.Core.Block, global::Nethermind.Consensus.Processing.ProcessingOptions, global::Nethermind.Evm.Tracing.IBlockTracer, global::Nethermind.Core.Specs.IReleaseSpec, global::System.Threading.CancellationToken)", target := "void global::Nethermind.Core.BlockExtensions.DisposeAccountChanges(global::Nethermind.Core.Block)", position := 4537, cfgBlock := 17 },
  { id := "processOne.validate-call", path := "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", owner := "(global::Nethermind.Core.Block Block, global::Nethermind.Core.TxReceipt[] Receipts) global::Nethermind.Consensus.Processing.BlockProcessor.ProcessOne(global::Nethermind.Core.Block, global::Nethermind.Consensus.Processing.ProcessingOptions, global::Nethermind.Evm.Tracing.IBlockTracer, global::Nethermind.Core.Specs.IReleaseSpec, global::System.Threading.CancellationToken)", target := "void global::Nethermind.Consensus.Processing.BlockProcessor.ValidateProcessedBlock(global::Nethermind.Core.Block, global::Nethermind.Consensus.Processing.ProcessingOptions, global::Nethermind.Core.Block, global::Nethermind.Core.TxReceipt[])", position := 4586, cfgBlock := 19 },
  { id := "processOne.validation-guard", path := "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", owner := "void global::Nethermind.Consensus.Processing.BlockProcessor.ValidateProcessedBlock(global::Nethermind.Core.Block, global::Nethermind.Consensus.Processing.ProcessingOptions, global::Nethermind.Core.Block, global::Nethermind.Core.TxReceipt[])", target := "global::Nethermind.Consensus.Processing.ProcessingOptions.NoValidation", position := 4997, cfgBlock := 1 },
  { id := "processOne.validator-call", path := "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", owner := "void global::Nethermind.Consensus.Processing.BlockProcessor.ValidateProcessedBlock(global::Nethermind.Core.Block, global::Nethermind.Consensus.Processing.ProcessingOptions, global::Nethermind.Core.Block, global::Nethermind.Core.TxReceipt[])", target := "bool global::Nethermind.Consensus.Validators.IBlockValidator.ValidateProcessedBlock(global::Nethermind.Core.Block, global::Nethermind.Core.TxReceipt[], global::Nethermind.Core.Block, out string)", position := 5033, cfgBlock := 2 },
  { id := "processOne.validation-dispose", path := "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", owner := "void global::Nethermind.Consensus.Processing.BlockProcessor.ValidateProcessedBlock(global::Nethermind.Core.Block, global::Nethermind.Consensus.Processing.ProcessingOptions, global::Nethermind.Core.Block, global::Nethermind.Core.TxReceipt[])", target := "void global::Nethermind.Core.BlockExtensions.DisposeAccountChanges(global::Nethermind.Core.Block)", position := 5146, cfgBlock := 3 },
  { id := "processOne.invalid-block-throw", path := "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", owner := "void global::Nethermind.Consensus.Processing.BlockProcessor.ValidateProcessedBlock(global::Nethermind.Core.Block, global::Nethermind.Consensus.Processing.ProcessingOptions, global::Nethermind.Core.Block, global::Nethermind.Core.TxReceipt[])", target := "global::Nethermind.Core.Exceptions.InvalidBlockException.InvalidBlockException(global::Nethermind.Core.Block, string, global::System.Exception)", position := 5322, cfgBlock := 5 },
  { id := "processOne.post-validation-call", path := "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", owner := "void global::Nethermind.Consensus.Processing.BlockProcessor.ValidateProcessedBlock(global::Nethermind.Core.Block, global::Nethermind.Consensus.Processing.ProcessingOptions, global::Nethermind.Core.Block, global::Nethermind.Core.TxReceipt[])", target := "void global::Nethermind.Consensus.Processing.BlockProcessor.PostValidation(global::Nethermind.Core.Block, global::Nethermind.Core.Block, global::Nethermind.Core.TxReceipt[], global::Nethermind.Consensus.Processing.ProcessingOptions)", position := 5391, cfgBlock := 6 },
  { id := "processOne.store-guard", path := "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", owner := "(global::Nethermind.Core.Block Block, global::Nethermind.Core.TxReceipt[] Receipts) global::Nethermind.Consensus.Processing.BlockProcessor.ProcessOne(global::Nethermind.Core.Block, global::Nethermind.Consensus.Processing.ProcessingOptions, global::Nethermind.Evm.Tracing.IBlockTracer, global::Nethermind.Core.Specs.IReleaseSpec, global::System.Threading.CancellationToken)", target := "global::Nethermind.Consensus.Processing.ProcessingOptions.StoreReceipts", position := 4685, cfgBlock := 19 },
  { id := "processOne.insert-deferred", path := "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", owner := "void global::Nethermind.Consensus.Processing.BlockProcessor.StoreTxReceipts(global::Nethermind.Core.Block, global::Nethermind.Core.TxReceipt[], global::Nethermind.Core.Specs.IReleaseSpec)", target := "void global::Nethermind.Blockchain.Receipts.IReceiptStorage.InsertDeferred(global::Nethermind.Core.Block, global::Nethermind.Core.TxReceipt[], global::Nethermind.Core.Specs.IReleaseSpec)", position := 14865, cfgBlock := 1 },
  { id := "processOne.return-tuple", path := "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", owner := "(global::Nethermind.Core.Block Block, global::Nethermind.Core.TxReceipt[] Receipts) global::Nethermind.Consensus.Processing.BlockProcessor.ProcessOne(global::Nethermind.Core.Block, global::Nethermind.Consensus.Processing.ProcessingOptions, global::Nethermind.Evm.Tracing.IBlockTracer, global::Nethermind.Core.Specs.IReleaseSpec, global::System.Threading.CancellationToken)", target := "(global::Nethermind.Core.Block block, global::Nethermind.Core.TxReceipt[] receipts)", position := 4807, cfgBlock := 21 },
  { id := "postValidation.accountChanges", path := "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", owner := "void global::Nethermind.Consensus.Processing.BlockProcessor.PostValidation(global::Nethermind.Core.Block, global::Nethermind.Core.Block, global::Nethermind.Core.TxReceipt[], global::Nethermind.Consensus.Processing.ProcessingOptions)", target := "global::Nethermind.Core.Collections.ArrayPoolList<global::Nethermind.Core.AddressAsKey> global::Nethermind.Core.Block.AccountChanges", position := 5866, cfgBlock := 1 },
  { id := "postValidation.executionRequests", path := "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", owner := "void global::Nethermind.Consensus.Processing.BlockProcessor.PostValidation(global::Nethermind.Core.Block, global::Nethermind.Core.Block, global::Nethermind.Core.TxReceipt[], global::Nethermind.Consensus.Processing.ProcessingOptions)", target := "byte[][] global::Nethermind.Core.Block.ExecutionRequests", position := 5937, cfgBlock := 1 },
  { id := "postValidation.generatedBlockAccessList", path := "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", owner := "void global::Nethermind.Consensus.Processing.BlockProcessor.PostValidation(global::Nethermind.Core.Block, global::Nethermind.Core.Block, global::Nethermind.Core.TxReceipt[], global::Nethermind.Consensus.Processing.ProcessingOptions)", target := "global::Nethermind.Core.BlockAccessLists.GeneratedBlockAccessList global::Nethermind.Core.Block.GeneratedBlockAccessList", position := 6014, cfgBlock := 1 },
  { id := "postValidation.encodedBlockAccessList-fallback", path := "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", owner := "void global::Nethermind.Consensus.Processing.BlockProcessor.PostValidation(global::Nethermind.Core.Block, global::Nethermind.Core.Block, global::Nethermind.Core.TxReceipt[], global::Nethermind.Consensus.Processing.ProcessingOptions)", target := "byte[] global::Nethermind.Core.Block.EncodedBlockAccessList", position := 6105, cfgBlock := 2 },
  { id := "validator.generatedBlockAccessList-observation", path := "src/Nethermind/Nethermind.Consensus/Validators/BlockValidator.cs", owner := "bool global::Nethermind.Consensus.Validators.BlockValidator.ValidateProcessedBlock(global::Nethermind.Core.Block, global::Nethermind.Core.TxReceipt[], global::Nethermind.Core.Block, out string)", target := "global::Nethermind.Core.BlockAccessLists.GeneratedBlockAccessList global::Nethermind.Core.Block.GeneratedBlockAccessList", position := 11414, cfgBlock := 38 },
  { id := "options.NoValidation", path := "src/Nethermind/Nethermind.Consensus/Processing/ProcessingOptions.cs", owner := "global::Nethermind.Consensus.Processing.ProcessingOptions", target := "global::Nethermind.Consensus.Processing.ProcessingOptions.NoValidation", position := 803, cfgBlock := -1 },
  { id := "options.StoreReceipts", path := "src/Nethermind/Nethermind.Consensus/Processing/ProcessingOptions.cs", owner := "global::Nethermind.Consensus.Processing.ProcessingOptions", target := "global::Nethermind.Consensus.Processing.ProcessingOptions.StoreReceipts", position := 672, cfgBlock := -1 }]

inductive EscapeSite where
| unsupportedBoundary | validator | rejectionCleanup | postValidation | receiptStorage
  deriving DecidableEq, Repr

inductive Outcome where
| completed | rejected | escaped (site : EscapeSite)
  deriving DecidableEq, Repr

structure ExecutionArtifacts where
  accountChanges : Option Nat
  executionRequests : Option Nat
  generatedBlockAccessList : Option Nat
  encodedBlockAccessList : Option Nat
  deriving DecidableEq, Repr

structure ProcessBlockResult where
  normalReturn : Bool
  processedBlock : Nat
  receipts : Nat
  deriving DecidableEq, Repr

structure ValidatorObservation where
  ran : Bool
  normalReturn : Bool
  accepted : Bool
  proposedGeneratedBlockAccessListAfter : Option Nat
  deriving DecidableEq, Repr

structure PublicationInput where
  suggestedBlock : Nat
  processBlock : ProcessBlockResult
  suggestedArtifacts : ExecutionArtifacts
  processedArtifacts : ExecutionArtifacts
  exactBase : Bool
  standardSequential : Bool
  balEnabled : Bool
  parallelExecutionEnabled : Bool
  noValidation : Bool
  storeReceipts : Bool
  validator : ValidatorObservation
  rejectionCleanupNormalReturn : Bool
  postValidationNormalReturn : Bool
  insertDeferredNormalReturn : Bool
  noAdditionalBoundaryEffects : Bool
  deriving DecidableEq, Repr

structure PublicationState where
  suggestedBlock : Nat
  suggestedArtifacts : ExecutionArtifacts
  processedBlock : Nat
  receipts : Nat
  accountChangesDisposed : Bool
  deriving DecidableEq, Repr

inductive Event where
| processBlockReturned
| validationSkipped
| validatorObserved (proposedGeneratedBlockAccessList : Option Nat)
| validatorAccepted | validatorRejected
| accountChangesDisposed | invalidBlockException
| accountChangesPublished | executionRequestsPublished | generatedBlockAccessListPublished
| encodedBlockAccessListPublished (usedFallback : Bool)
| insertDeferred | returnedProcessedBlockAndReceipts
| escaped (site : EscapeSite)
  deriving DecidableEq, Repr

structure PublicationResult where
  outcome : Outcome
  state : Option PublicationState
  returnedTuple : Option (Nat × Nat)
  events : List Event
  deriving DecidableEq, Repr

def boundaryValid (input : PublicationInput) : Bool :=
  input.exactBase && input.standardSequential && !input.balEnabled &&
  !input.parallelExecutionEnabled && input.processBlock.normalReturn &&
  input.noAdditionalBoundaryEffects

def validatorContractValid (input : PublicationInput) : Bool :=
  if input.noValidation then
    !input.validator.ran &&
    input.validator.proposedGeneratedBlockAccessListAfter == input.suggestedArtifacts.generatedBlockAccessList
  else input.validator.ran

def initialState (input : PublicationInput) : PublicationState :=
  { suggestedBlock := input.suggestedBlock
    suggestedArtifacts := input.suggestedArtifacts
    processedBlock := input.processBlock.processedBlock
    receipts := input.processBlock.receipts
    accountChangesDisposed := false }

def copyPostValidation (input : PublicationInput) : ExecutionArtifacts :=
  { accountChanges := input.processedArtifacts.accountChanges
    executionRequests := input.processedArtifacts.executionRequests
    generatedBlockAccessList := input.processedArtifacts.generatedBlockAccessList
    encodedBlockAccessList := input.processedArtifacts.encodedBlockAccessList.orElse
      (fun _ => input.suggestedArtifacts.encodedBlockAccessList) }

def validationEvents (input : PublicationInput) : List Event :=
  if input.noValidation then [.validationSkipped]
  else [.validatorObserved input.validator.proposedGeneratedBlockAccessListAfter,
    (if input.validator.accepted then .validatorAccepted else .validatorRejected)]

def copyEvents (input : PublicationInput) : List Event :=
  [.accountChangesPublished, .executionRequestsPublished, .generatedBlockAccessListPublished,
    .encodedBlockAccessListPublished input.processedArtifacts.encodedBlockAccessList.isNone]

def escape (site : EscapeSite) (eventPrefix : List Event) : PublicationResult :=
  { outcome := .escaped site, state := none, returnedTuple := none,
    events := eventPrefix ++ [.escaped site] }

def run (input : PublicationInput) : PublicationResult :=
  if !boundaryValid input then escape .unsupportedBoundary []
  else if !validatorContractValid input then escape .unsupportedBoundary [.processBlockReturned]
  else if !input.noValidation && !input.validator.normalReturn then
    escape .validator [.processBlockReturned]
  else
    let validated := [.processBlockReturned] ++ validationEvents input
    if !input.noValidation && !input.validator.accepted then
      if !input.rejectionCleanupNormalReturn then escape .rejectionCleanup validated
      else
        { outcome := .rejected
          state := some { initialState input with
            suggestedArtifacts := { input.suggestedArtifacts with
              generatedBlockAccessList := input.validator.proposedGeneratedBlockAccessListAfter }
            accountChangesDisposed := true }
          returnedTuple := none
          events := validated ++ [.accountChangesDisposed, .invalidBlockException] }
    else if !input.postValidationNormalReturn then escape .postValidation validated
    else
      let published := validated ++ copyEvents input
      if input.storeReceipts && !input.insertDeferredNormalReturn then
        escape .receiptStorage (published ++ [.insertDeferred])
      else
        { outcome := .completed
          state := some { initialState input with suggestedArtifacts := copyPostValidation input }
          returnedTuple := some (input.processBlock.processedBlock, input.processBlock.receipts)
          events := published ++ (if input.storeReceipts then [.insertDeferred] else []) ++
            [.returnedProcessedBlockAndReceipts] }

end SequentialBlockPostTransactionFinalizationExtractor.Generated.ProcessOneValidatedPublication
