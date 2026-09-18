-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import EvmFrameMachineExtractor.Specification.FrameMachineState
import EvmFrameMachineExtractor.Refinement.StageARouting
import PrecompileFrameExtractor.Refinement.PrecompileFrameStageB
import PrecompileFullFrameExtractor.Refinement.PrecompileFullFrame
import Eip803x.Refinement.StateGasTransition
import Eip803x.Refinement.PrecompileGasPricing

/-!
Stage D's vocabulary is deliberately the `ExecuteTransaction` loop boundary, not a
whole transaction processor. In particular, a full precompile is selected from the
current frame's `IsPrecompile`/`CodeInfo.IsPrecompile` state. The standard-mainnet
STATICCALL fast path is instead an event inside bytecode opcode execution: it happens
before a child precompile frame is rented and therefore is never a frame subject.

The adapter values are external production/refinement boundaries. The theorem uses
them only on states admitted by its caller; it does not quantify an execution contract
over arbitrary syntactic `Machine` values.
-/

namespace EvmFrameControlSettlementExtractor

open Eip803x
open Eip803x.Evm.FrameMachineState

inductive DispatchSubject where
  | bytecode
  | fullPrecompile
  deriving DecidableEq, Repr

/- `RunDispatchLoop` polls cancellation once before dispatch and after a successful
complete 1024-opcode batch with a successor. This tag represents only those driver
polls and deliberately carries the local dispatch count and successor, rather than
treating `Machine.opcodeCount` as the private loop counter. A cancellation thrown by
a tracer callback outside the loop is represented as `escapedInvocation`, not here. -/
inductive OpcodeCancellationBoundary where
  | beforeFirstOpcode
  | afterCompleteBatch (completedOpcodeCount successorPc : Nat)
  deriving DecidableEq, Repr

/- This is the inline STATICCALL event nested in an opcode body. It is not an
`InvocationResult`: execution remains in the bytecode frame and later yields the
ordinary frame result. -/
inductive DirectInlineStaticPrecompileOutcome where
  | succeeded
  | insufficientGas
  | executionFailure (reason : String)
  deriving DecidableEq, Repr

inductive InvocationResult where
  | returned (step : MachineStep)
  | fullPrecompileSuccess (step : MachineStep)
  | fullPrecompileOutOfGas
  | fullPrecompileReturnedFailure (reason : Option String)
  | fullPrecompileManagedException (reason : Option String)
  | thrownEvm (kind : ExceptionKind)
  | thrownOverflow
  | escapedInvocation (reason : String)
  | cancelled (boundary : OpcodeCancellationBoundary) (reason : String)
  deriving Repr

structure BytecodeFrameOutcome where
  invocation : InvocationResult
  directInlineStaticPrecompile : Option DirectInlineStaticPrecompileOutcome
  deriving Repr

structure DriverInvocation where
  invocation : InvocationResult
  directInlineStaticPrecompile : Option DirectInlineStaticPrecompileOutcome
  deriving Repr

def machineAfterInvocation (machine : Machine) : InvocationResult → Machine
  | .returned step
  | .fullPrecompileSuccess step => step.machine
  | .fullPrecompileOutOfGas
  | .fullPrecompileReturnedFailure _
  | .fullPrecompileManagedException _
  | .thrownEvm _
  | .thrownOverflow
  | .escapedInvocation _
  | .cancelled _ _ => machine

def cancellationReasonOf : InvocationResult → String
  | .cancelled _ reason => reason
  | _ => "cancelledInvocation"

def escapedReasonOf : InvocationResult → String
  | .escapedInvocation reason => reason
  | _ => "escapedInvocation"

/- The shared result carrier keeps settlement adapters uniform, but its outcome
domains remain source-specific. `ExecuteCall` may yield an ordinary continuation
or child suspension; `ExecutePrecompile` finishes its current non-CREATE frame
and then enters the VM's ordinary top/nested settlement. These predicates are
required only of the corresponding admitted dispatch adapter. -/
def isBytecodeInvocation : InvocationResult → Prop
  | .returned _ | .thrownEvm _ | .thrownOverflow | .escapedInvocation _ | .cancelled _ _ => True
  | .fullPrecompileSuccess _ | .fullPrecompileOutOfGas | .fullPrecompileReturnedFailure _ |
      .fullPrecompileManagedException _ => False

def isFullPrecompileInvocation (machine : Machine) : InvocationResult → Prop
  | .fullPrecompileSuccess step =>
      ∃ result, step.result = .halt result ∧ result.exit = .success ∧
        result.precompileSuccess = some true ∧
        machine.current.executionType.isCreate = false
  | .fullPrecompileOutOfGas | .fullPrecompileReturnedFailure _ |
      .fullPrecompileManagedException _ | .escapedInvocation _ =>
        machine.current.executionType.isCreate = false
  | .returned _ | .thrownEvm _ | .thrownOverflow | .cancelled _ _ => False

inductive CodeDepositOutcome where
  | deposited
  | invalidCode
  | outOfGas
  deriving DecidableEq, Repr

inductive SettlementRoute where
  | noSettlement
  | continued
  | suspend
  | regularSuccessNested
  | topLevelSuccess
  | createSuccessNested
  | revertNested
  | revertTop
  | exceptionTop
  | exceptionNested
  | codeDepositInvalidNested
  | codeDepositOutOfGasNested
  | fullPrecompileOutOfGasNested
  | fullPrecompileOutOfGasTop
  | fullPrecompileReturnedFailureNested
  | fullPrecompileReturnedFailureTop
  | fullPrecompileManagedExceptionNested
  | fullPrecompileManagedExceptionTop
  | cancelled
  | escaped
  deriving DecidableEq, Repr

inductive ShellTermination where
  | completed
  | suspended
  | cancelled
  | escaped
  | incomplete
  deriving DecidableEq, Repr

/- This records ownership at the VM boundary rather than asserting that
`ExecuteTransaction` disposes the top-level state or restores world state. The
transaction processor/call scope owns those operations outside this package. -/
structure DisposalObservation where
  activeChildFramesDisposed : Bool
  stateStackDrained : Bool
  topLevelVmStateOwnedByCaller : Bool
  topLevelPooledMemoryOwnedByCaller : Bool
  externalWorldRollbackOwnedByCaller : Bool
  deriving DecidableEq, Repr

def DisposalObservation.notObserved : DisposalObservation :=
  { activeChildFramesDisposed := false
    stateStackDrained := false
    topLevelVmStateOwnedByCaller := false
    topLevelPooledMemoryOwnedByCaller := false
    externalWorldRollbackOwnedByCaller := false }

structure ShellResult where
  termination : ShellTermination
  route : SettlementRoute
  machine : Machine
  result : Option FrameResult
  reason : Option String
  status : Option String
  directInlineStaticPrecompile : Option DirectInlineStaticPrecompileOutcome
  disposal : DisposalObservation
  deriving Repr

structure SettlementAdapters where
  continueStep : InvocationResult → Machine → ShellResult
  suspendStep : InvocationResult → Machine → ShellResult
  regularSuccessNested : InvocationResult → Machine → ShellResult
  topLevelSuccess : InvocationResult → Machine → ShellResult
  classifyNestedCreateCodeDeposit : InvocationResult → Machine → CodeDepositOutcome
  createSuccessNested : InvocationResult → Machine → ShellResult
  codeDepositInvalidNested : InvocationResult → Machine → ShellResult
  codeDepositOutOfGasNested : InvocationResult → Machine → ShellResult
  revertNested : InvocationResult → Machine → ShellResult
  revertTop : InvocationResult → Machine → ShellResult
  exceptionTop : InvocationResult → Machine → ShellResult
  exceptionNested : InvocationResult → Machine → ShellResult
  fullPrecompileOutOfGasNested : InvocationResult → Machine → ShellResult
  fullPrecompileOutOfGasTop : InvocationResult → Machine → ShellResult
  fullPrecompileReturnedFailureNested : InvocationResult → Machine → ShellResult
  fullPrecompileReturnedFailureTop : InvocationResult → Machine → ShellResult
  fullPrecompileManagedExceptionNested : InvocationResult → Machine → ShellResult
  fullPrecompileManagedExceptionTop : InvocationResult → Machine → ShellResult

structure PreparationAdapters where
  prepareFresh : Machine → Machine
  prepareContinuation : Machine → Machine
  clearReturnData : Machine → Machine

structure DispatchAdapters where
  executeBytecodeFrame : Machine → BytecodeFrameOutcome
  executeFullPrecompileFrame : Machine → InvocationResult
  directInlineStaticPrecompileEligible : Machine → Bool

/-! The generated driver and independently organized reference consume the same
canonical leaf record.  Their control-agreement theorem therefore checks frame
selection, ordering, and routing without postulating pointwise equality between
two copies of every leaf.  A production implementation is related to these
leaves separately by `ProductionLeafSimulation` in the refinement layer. -/
structure CanonicalLeaves where
  preparation : PreparationAdapters
  dispatch : DispatchAdapters
  settlement : SettlementAdapters
  cleanup : ShellResult → DisposalObservation × Machine

structure Input where
  machine : Machine
  deriving Repr

/- `VmState.IsContinuation` is a binary production discriminant. `running` is
available in the shared frame-machine carrier for lower-level packages, but is
not an admitted `ExecuteTransaction` loop-boundary state. -/
def isDriverLoopPhase (machine : Machine) : Prop :=
  machine.current.phase = .fresh ∨ machine.current.phase = .continuation

/- Match the production branch, which tests `VmState.IsTopLevel`; the relationship
between that flag and an empty frame stack is an admitted-state invariant, not a
replacement classification rule. -/
def topLevel (machine : Machine) : Bool := machine.current.isTopLevel

def subjectOf (machine : Machine) : DispatchSubject :=
  match machine.current.kind with
  | .bytecode => .bytecode
  | .precompile _ => .fullPrecompile

theorem full_subject_selects_current_precompile (machine : Machine) :
    subjectOf machine = .fullPrecompile → ∃ address, machine.current.kind = .precompile address := by
  cases kind : machine.current.kind with
  | bytecode => simp [subjectOf, kind]
  | precompile address => exact fun _ => ⟨address, rfl⟩

def currentGasObservation (machine : Machine) : GasState := machine.current.gas.gas

structure JournalObservation where
  durable : Nat
  reversible : Nat
  accessedAccounts : Nat
  accessedStorage : Nat
  logs : List Nat
  destroyList : List Nat
  deriving DecidableEq, Repr

def currentJournalObservation (machine : Machine) : JournalObservation :=
  { durable := machine.current.world.durable
    reversible := machine.current.world.reversible
    accessedAccounts := machine.current.world.accessedAccounts
    accessedStorage := machine.current.world.accessedStorage
    logs := machine.current.world.logs
    destroyList := machine.current.world.destroyList }

structure Observation where
  termination : ShellTermination
  route : SettlementRoute
  reason : Option String
  status : Option String
  exit : Option FrameExit
  precompileSuccess : Option Bool
  returnedData : List Byte
  output : List Byte
  outputDestination : Nat
  outputLength : Nat
  previousCallResult : PreviousCallResult
  previousCallOutputDestination : Nat
  previousCallOutputLength : Nat
  shouldRestoreRipemdTouch : Bool
  world : WorldToken
  journal : JournalObservation
  gasLeft : Nat
  stateReservoir : Nat
  stateFromGasLeft : Nat
  stateUsed : Nat
  refundCounter : Int
  stateGasBaseline : Nat
  stateUsedBaseline : Nat
  refundCounterBaseline : Int
  initialStateGasUsed : Nat
  stateGasRefundAdvanced : Nat
  executionRefund : Int
  tracing : List String
  transactionTrace : List String
  controlTrace : List String
  directInlineStaticPrecompile : Option DirectInlineStaticPrecompileOutcome
  disposal : DisposalObservation
  deriving DecidableEq, Repr

def observe (result : ShellResult) : Observation :=
  let frame := result.machine.current
  let output := match result.result with
    | some frameResult => frameResult.output
    | none => frame.output
  let status := match result.status, result.result with
    | some value, _ => some value
    | none, some frameResult => frameResult.substateError
    | none, none => none
  { termination := result.termination
    route := result.route
    reason := result.reason
    status := status
    exit := result.result.map (fun frameResult => frameResult.exit)
    precompileSuccess := match result.result with
      | some frameResult => frameResult.precompileSuccess
      | none => none
    returnedData := result.machine.returnDataBuffer
    output := output
    outputDestination := frame.outputDestination
    outputLength := frame.outputLength
    previousCallResult := result.machine.previousCallResult
    previousCallOutputDestination := result.machine.previousCallOutputDestination
    previousCallOutputLength := result.machine.previousCallOutputLength
    shouldRestoreRipemdTouch := result.machine.shouldRestoreRipemdTouch
    world := frame.world
    journal := currentJournalObservation result.machine
    gasLeft := frame.gas.gas.gasLeft
    stateReservoir := frame.gas.gas.stateReservoir
    stateFromGasLeft := frame.gas.gas.stateFromGasLeft
    stateUsed := frame.gas.gas.stateUsed
    refundCounter := frame.gas.gas.refundCounter
    stateGasBaseline := frame.gas.stateGasBaseline
    stateUsedBaseline := frame.gas.stateUsedBaseline
    refundCounterBaseline := frame.gas.refundCounterBaseline
    initialStateGasUsed := frame.initialStateGasUsed
    stateGasRefundAdvanced := frame.stateGasRefundAdvanced
    executionRefund := frame.refund
    tracing := frame.trace
    transactionTrace := result.machine.transactionTrace
    controlTrace := result.machine.controlTrace
    directInlineStaticPrecompile := result.directInlineStaticPrecompile
    disposal := result.disposal }

/- The imported theorems are named independently so an adapter bridge can require
the theorem it consumes. This avoids treating a `#check` or a file hash as a
refinement proof. -/
def StageARoutingTheorem : Prop :=
  EvmFrameMachineExtractor.Specification.StageARouting.allRouteLookupsAgree = true

theorem stage_a_routing_theorem_is_constructible : StageARoutingTheorem :=
  EvmFrameMachineExtractor.Refinement.StageARouting.generated_route_lookup_refines_independent_spec

def StageBInlineTheorem : Prop :=
  ∀ (oracle : Eip803x.PrecompileFrame.StageB.Refinement.G.LeafOracle)
    (input : Eip803x.PrecompileFrame.StageB.Refinement.G.Input),
    Eip803x.PrecompileFrame.StageB.Refinement.toReferenceOutcome
        (Eip803x.PrecompileFrame.StageB.Refinement.G.executePrecompile oracle input) =
      Eip803x.PrecompileFrame.StageB.Refinement.R.executePrecompile
        (Eip803x.PrecompileFrame.StageB.Refinement.toReferenceOracle oracle)
        (Eip803x.PrecompileFrame.StageB.Refinement.toReferenceInput input)

theorem stage_b_inline_theorem_is_constructible : StageBInlineTheorem :=
  Eip803x.PrecompileFrame.StageB.Refinement.source_execute_precompile_refines_reference

def StageCFullTheorem : Prop :=
  ∀ (front referenceFront : Eip803x.PrecompileFullFrame.FrontOperations)
    (semantics : Eip803x.Evm.FrameMachineState.Semantics)
    (oracle referenceOracle : Eip803x.PrecompileFullFrame.LeafOracle)
    (entry : Eip803x.PrecompileFullFrame.Entry),
    Eip803x.PrecompileFullFrame.FrontAgrees front referenceFront →
    Eip803x.PrecompileFullFrame.OracleAgrees oracle referenceOracle →
    entry.Admitted →
    Eip803x.PrecompileFullFrame.Refinement.FrontPreservesParents front →
    Eip803x.PrecompileFullFrame.Refinement.TopAdaptersValid front →
      Eip803x.PrecompileFullFrame.Generated.fullFrame front semantics.settlement oracle entry =
        Eip803x.PrecompileFullFrame.Reference.fullFrame referenceFront semantics referenceOracle entry

theorem stage_c_full_theorem_is_constructible : StageCFullTheorem :=
  Eip803x.PrecompileFullFrame.Refinement.source_full_frame_refines_reference

def StateGasRefundTheorem : Prop :=
  ∀ {parent child : Eip803x.ProductionGasState},
    Eip803x.Refinement.StateGasTransition.Spec.refundNoOverflow parent child →
    Eip803x.Refinement.StateGasTransition.generatedResultToSpec
        (Eip803x.Generated.StateGasTransitionKernel.refund
          parent.gasLeft parent.stateReservoir parent.stateGasUsed parent.stateGasSpill
          parent.stateGasSpillRefunded child.gasLeft child.stateReservoir child.stateGasUsed
          child.stateGasSpill child.stateGasSpillRefunded) =
      Eip803x.Refinement.StateGasTransition.Spec.refund parent child

theorem state_gas_refund_theorem_is_constructible : StateGasRefundTheorem :=
  Eip803x.Refinement.StateGasTransition.generated_refund_matches_spec

def PrecompilePricingTheorem : Prop :=
  ∀ (gas : Eip803x.ProductionGasState) (baseCost dataCost : Nat),
    gas.gasLeft ≤ Eip803x.Generated.PrecompileGasPricingKernel.uint64Max →
    baseCost ≤ Eip803x.Generated.PrecompileGasPricingKernel.uint64Max →
    dataCost ≤ Eip803x.Generated.PrecompileGasPricingKernel.uint64Max →
    Eip803x.Refinement.PrecompileGasPricing.toWrapperPricingResult gas
        (Eip803x.Generated.PrecompileGasPricingKernel.tryConsume
          gas.gasLeft baseCost dataCost) =
      Eip803x.Precompiles.Wrapper.tryConsumePrecompileGas
        { base := baseCost, data := dataCost } gas

theorem precompile_pricing_theorem_is_constructible : PrecompilePricingTheorem :=
  Eip803x.Refinement.PrecompileGasPricing.tryConsume_refines_wrapper

/- Oracle values are data-bearing source boundaries, not proof bundles. -/
structure ExternalOracles where
  opcodeFrame : Machine → BytecodeFrameOutcome
  fullPrecompileFrame : Machine → InvocationResult

/- `SourceAdapterBindings` is an explicit assumption interface for one canonical
leaf record. It carries identity-checked named Stage A--C, gas, and pricing
theorem witnesses and assumes that its canonical bytecode/full-frame dispatch
leaves equal caller-supplied oracle values on admitted states. It does not
discharge semantic adapter composition or prove that production C# bodies
implement those leaves; both remain open. -/
structure SourceAdapterBindings (leaves : CanonicalLeaves) (admitted : Machine → Prop)
    (oracles : ExternalOracles) : Prop where
  stageARoutingTheorem : StageARoutingTheorem
  stageBInlineTheorem : StageBInlineTheorem
  stageCFullTheorem : StageCFullTheorem
  stateGasRefundTheorem : StateGasRefundTheorem
  precompilePricingTheorem : PrecompilePricingTheorem
  opcodeFrameBound : StageARoutingTheorem → StageBInlineTheorem →
    ∀ machine, admitted machine → subjectOf machine = .bytecode →
      leaves.dispatch.executeBytecodeFrame machine = oracles.opcodeFrame machine
  fullPrecompileFrameBound : StageCFullTheorem → PrecompilePricingTheorem →
    ∀ machine, admitted machine → subjectOf machine = .fullPrecompile →
      leaves.dispatch.executeFullPrecompileFrame machine = oracles.fullPrecompileFrame machine
  bytecodeOutcomeHasBytecodeDomain : ∀ machine, admitted machine → subjectOf machine = .bytecode →
    isBytecodeInvocation (leaves.dispatch.executeBytecodeFrame machine).invocation
  fullPrecompileOutcomeCompletesCurrentFrame : ∀ machine, admitted machine →
    subjectOf machine = .fullPrecompile →
      isFullPrecompileInvocation machine (leaves.dispatch.executeFullPrecompileFrame machine)
  directInlineOnlyFromBytecodeOpcode : ∀ machine, admitted machine → subjectOf machine = .bytecode →
    (leaves.dispatch.executeBytecodeFrame machine).directInlineStaticPrecompile ≠ none →
      machine.current.kind = .bytecode ∧
        leaves.dispatch.directInlineStaticPrecompileEligible machine = true
  bytecodeCancellationUsesDispatchLoopBoundary : ∀ machine, admitted machine → subjectOf machine = .bytecode → ∀ boundary reason,
    (leaves.dispatch.executeBytecodeFrame machine).invocation = .cancelled boundary reason →
      machine.current.dispatchTable.cancelable = true ∧ reason ≠ "" ∧
        match boundary with
        | .beforeFirstOpcode => machine.current.pc < machine.current.code.length
        | .afterCompleteBatch completedOpcodeCount successorPc =>
            0 < completedOpcodeCount ∧ completedOpcodeCount % 1024 = 0 ∧
              successorPc < machine.current.code.length
  fullPrecompileDoesNotYieldDriverCancellation : ∀ machine, admitted machine → ∀ boundary reason,
    subjectOf machine = .fullPrecompile →
      leaves.dispatch.executeFullPrecompileFrame machine ≠ .cancelled boundary reason

def FrameFieldBounds (machine : Machine) : Prop :=
  machine.current.opcodeCount < 2 ^ 64 ∧
  machine.current.outputLength < 2 ^ 64 ∧
  machine.current.gas.gas.gasLeft < 2 ^ 64 ∧
  machine.current.gas.gas.stateReservoir < 2 ^ 64 ∧
  machine.current.gas.gas.stateFromGasLeft < 2 ^ 64 ∧
  machine.current.gas.gas.stateUsed < 2 ^ 64 ∧
  machine.current.gas.stateGasBaseline < 2 ^ 64 ∧
  machine.current.gas.stateUsedBaseline < 2 ^ 64 ∧
  machine.current.initialStateGasUsed < 2 ^ 64 ∧
  machine.current.stateGasRefundAdvanced < 2 ^ 64 ∧
  -(2 ^ 63 : Int) ≤ machine.current.gas.gas.refundCounter ∧
  machine.current.gas.gas.refundCounter < 2 ^ 63 ∧
    -(2 ^ 63 : Int) ≤ machine.current.gas.refundCounterBaseline ∧
    machine.current.gas.refundCounterBaseline < 2 ^ 63 ∧
    -(2 ^ 63 : Int) ≤ machine.current.refund ∧
    machine.current.refund < 2 ^ 63

/- Bounds apply to every state admitted by the one-step caller, including the
prepared and settled output states, rather than to one arbitrary universally quantified
machine or only the initial input. -/
structure FixedWidthFacts (admitted : Machine → Prop) : Prop where
  allAdmittedStatesFit : ∀ machine, admitted machine → FrameFieldBounds machine

end EvmFrameControlSettlementExtractor
