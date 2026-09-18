// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;

namespace Nethermind.Evm.Lean.SequentialBlockPostTransactionFinalizationExtractor;

internal static class LeanEmitter
{
    internal static byte[] Emit(IrDocument document)
    {
        Extractor.ValidateIrForEmitter(document);
        StringBuilder output = new(Template.Length + 4096);
        output.Append(Template);
        output.AppendLine();
        output.AppendLine("-- Source-bound typed identities admitted by the extractor:");
        foreach (AnchorIdentity anchor in document.Anchors)
        {
            output.Append("-- ").Append(anchor.Id).AppendLine();
        }

        output.AppendLine("-- Typed commit identities:");
        foreach (CommitIdentity commit in document.Commits)
        {
            output.Append("-- ").Append(commit.Event).Append(": ").Append(commit.Id)
                .Append(" @ ").Append(commit.InvocationBinding.CanonicalSyntax)
                .Append(" -> ").Append(commit.StateProviderCall).AppendLine();
        }

        output.AppendLine("-- Complete source-local preservation closure (external-hook premises do not replace these checks):");
        output.Append("-- ").Append(document.EntryPreservation.SymbolId).Append(" executable-body=")
            .Append(document.EntryPreservation.SyntaxSha256).AppendLine();
        foreach (HelperPreservationIdentity helper in document.HelperPreservation)
        {
            output.Append("-- ").Append(helper.SymbolId).Append(" @ ").Append(helper.Path)
                .Append(" body=").Append(helper.SyntaxSha256).AppendLine();
        }

        output.Append("-- Instrumentation wrapper: ").Append(document.Instrumentation.Wrapper.Path)
            .Append(" source=").Append(document.Instrumentation.Wrapper.Sha256).AppendLine();
        foreach (InstrumentationSinkIdentity sink in document.Instrumentation.Sinks)
        {
            output.Append("-- ").Append(sink.OwnerSymbol).Append(" -> ").Append(sink.TypeSymbol)
                .Append(" declaration=").Append(sink.SyntaxSha256).AppendLine();
        }

        output.AppendLine("-- Typed selected-arm header assignments:");
        foreach (HeaderAssignmentIdentity assignment in document.HeaderAssignments)
        {
            output.Append("-- ").Append(assignment.Id).Append(" [").Append(assignment.SelectedArm)
                .Append("] ").Append(assignment.TargetCanonical).Append(" = ")
                .Append(assignment.ValueCanonical).AppendLine();
            foreach (string excluded in assignment.ExcludedWriteCanonicals)
            {
                output.Append("-- ").Append(assignment.Id).Append(" excludes ").Append(excluded).AppendLine();
            }
        }

        output.AppendLine("-- Ordered source observables:");
        foreach (StepIdentity step in document.Steps)
        {
            output.Append("-- ").Append(step.Ordinal).Append(": ").Append(step.Id)
                .Append(" [").Append(step.Branch).Append("] ").Append(step.Observable).AppendLine();
        }

        output.Append("-- Compiler closure: ").Append(document.CompilerClosure.InventoryPath)
            .Append(" count=").Append(document.CompilerClosure.Count)
            .Append(" aggregate=").Append(document.CompilerClosure.AggregateSha256)
            .Append(" inventory=").Append(document.CompilerClosure.InventorySha256).AppendLine();
        output.AppendLine("-- Source-entry adapter: static-layout-only; runtime identity and guard premises remain explicit.");
        foreach (SourceEntryAdapterIdentity adapter in document.SourceEntryAdapters)
        {
            output.Append("-- ").Append(adapter.Id).Append(" claim=").Append(adapter.Claim)
                .Append(" exactBase=").Append(adapter.ExactBaseReceiver)
                .Append(" executor=").Append(adapter.SelectedStandardExecutor)
                .Append(" bal=").Append(adapter.BalPremise)
                .Append(" background=").Append(adapter.BackgroundPremise)
                .Append(" mainThread=").Append(adapter.MainThreadPremise)
                .Append(" stateRoot=").Append(adapter.StateRootPremise)
                .Append(" spec=").Append(adapter.SpecPremise)
                .Append(" transactionsExecutedNormalReturn=").Append(adapter.TransactionsExecutedNormalReturnPremise)
                .Append(" postTransactionCommitNormalReturn=").Append(adapter.PostTransactionCommitNormalReturnPremise)
                .Append(" transactionsExecutedEvent=").Append(adapter.TransactionsExecutedEventSymbol)
                .Append(" transactionsExecutedBinding=").Append(adapter.TransactionsExecutedBinding.CanonicalSyntax)
                .Append(" postTransactionCommitBinding=").Append(adapter.PostTransactionCommitBinding.CanonicalSyntax)
                .Append(" stateRootWrite=").Append(adapter.StateRootWriteBinding.CanonicalSyntax)
                .Append(" stateRootValue=").Append(adapter.StateRootValueBinding.CanonicalSyntax)
                .Append(" accountChangesWrite=").Append(adapter.AccountChangesWriteBinding.CanonicalSyntax)
                .Append(" accountChangesValue=").Append(adapter.AccountChangesValueBinding.CanonicalSyntax)
                .Append(" receiptsReferences=").Append(adapter.ReceiptsReferenceCount)
                .Append(" receiptsWrites=").Append(adapter.ReceiptsWriteCount).AppendLine();
            output.Append("-- Header identity: ").Append(adapter.HeaderBinding.CanonicalSyntax)
                .Append(" <- ").Append(adapter.HeaderValueBinding.SymbolId).AppendLine();
            foreach (PreservedValueIdentity identity in adapter.PreservedValues)
            {
                output.Append("-- Preserved value: ").Append(identity.Name).Append(" symbol=").Append(identity.SymbolId)
                    .Append(" definition=").Append(identity.DefinitionCanonical)
                    .Append(" reads=").Append(identity.ReadContexts.Length).AppendLine();
            }
        }

        output.Append("-- Task flow: ").Append(document.TaskFlow.Variable)
            .Append(" localSymbol=").Append(document.TaskFlow.LocalSymbol)
            .Append(" backgroundAssignments=").Append(document.TaskFlow.BackgroundAssignments)
            .Append(" synchronousAssignments=").Append(document.TaskFlow.SynchronousAssignments)
            .Append(" nullPreserved=").Append(document.TaskFlow.NullPreservedInSynchronousArm)
            .Append(" backgroundResultExcluded=").Append(document.TaskFlow.BackgroundResultExcluded)
            .Append(" finallyObservationExcluded=").Append(document.TaskFlow.FinallyObservationExcluded).AppendLine();
        return Encoding.UTF8.GetBytes(output.ToString().Replace("\r\n", "\n"));
    }

    private const string Template = """
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
""";
}
