-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import SimpleTransferCompletionExtractor.StageB.Semantics
import SimpleTransferCompletionExtractor.StageB.FeeHelperSource
import SimpleTransferCompletionExtractor.StageB.Leaf.Generated.EffectiveBlockGas
import Eip803x.Refinement.BlockReceiptGasAccounting
import Eip803x.Refinement.SystemTransactionRouting

namespace SimpleTransferCompletionExtractor.StageB.FeeHelper

open SimpleTransferCompletionExtractor.StageB.Runtime

namespace Accounting
abbrev uint64Max := Eip803x.Generated.BlockReceiptGasAccountingKernel.uint64Max
abbrev addUInt64 := Eip803x.Generated.BlockReceiptGasAccountingKernel.addUInt64
abbrev combineBlockGas := Eip803x.Generated.BlockReceiptGasAccountingKernel.combineBlockGas
end Accounting

namespace Routing
abbrev Options := Eip803x.Generated.SystemTransactionRoutingKernel.Options
abbrev participatesInNormalBlockCounters := Eip803x.Generated.SystemTransactionRoutingKernel.participatesInNormalBlockCounters
end Routing

namespace Completion
abbrev GasConsumed := SimpleTransferCompletionExtractor.StageB.Leaf.Generated.GasConsumed
abbrev effectiveBlockGas := SimpleTransferCompletionExtractor.StageB.Leaf.Generated.effectiveBlockGas
end Completion

/-- External observations of the only mutable reference-object fields in this residual. -/
structure Objects where
  processorIdentity : String
  headerIdentity : String
  parallel : Bool
  execution : Nat
  state : Nat
  headerGasUsed : Nat
  deriving DecidableEq, Repr

def Objects.Valid (objects : Objects) : Prop :=
  objects.execution ≤ Accounting.uint64Max ∧ objects.state ≤ Accounting.uint64Max ∧
    objects.headerGasUsed ≤ Accounting.uint64Max

structure Observations where
  objects : Objects
  options : UInt32
  eip8037 : Bool
  spent : Completion.GasConsumed
  values : Nat → Runtime.Value

def skipValidation (options : UInt32) : Bool := (options &&& 4) == 4

def routingOptions (options : UInt32) : Routing.Options :=
  { commit := (options &&& 1) == 1, restore := (options &&& 2) == 2,
    skipValidation := skipValidation options, warmup := (options &&& 8) == 8,
    buildUp := (options &&& 16) == 16 }

def participates (observed : Observations) : Bool :=
  Routing.participatesInNormalBlockCounters (routingOptions observed.options) observed.objects.parallel

theorem routing_is_exact_bit_gate (observed : Observations) :
    participates observed = (!((observed.options &&& 4) == 4) && !observed.objects.parallel) := rfl

def Pinned (observed : Observations) : Prop :=
  skipValidation observed.options = false ∧ observed.objects.parallel = false ∧ observed.eip8037 = true

def Observations.Valid (observed : Observations) : Prop :=
  observed.objects.Valid ∧ observed.spent.spentGas ≤ Accounting.uint64Max ∧
    observed.spent.blockGas ≤ Accounting.uint64Max ∧ observed.spent.blockStateGas ≤ Accounting.uint64Max

/-- A finite, explicit observation derivation, not a claim about the CLR heap. -/
inductive FieldsObserved : Runtime.Value → List String → Runtime.Value → Prop where
  | root (value) : FieldsObserved value [] value
  | field (typeName fields name names value result)
      (found : fields.find? (fun item => item.1 == name) = some (name, value))
      (rest : FieldsObserved value names result) :
      FieldsObserved (.struct typeName fields) (name :: names) result

inductive OperandObserved (machine : Machine) : Operand → Runtime.Value → Prop where
  | immediate (value) : OperandObserved machine (.immediate value) value
  | direct (location : Location) (cell : Cell) (value)
      (found : machine.cells.find? (fun candidate => candidate.id == location.root) = some cell)
      (direct : cell.alias = none) (fields : FieldsObserved cell.value location.fields value) :
      OperandObserved machine (.location location) value
  | alias (location target : Location) (cell : Cell) (value)
      (found : machine.cells.find? (fun candidate => candidate.id == location.root) = some cell)
      (alias : cell.alias = some target)
      (observed : OperandObserved machine (.location { target with fields := target.fields ++ location.fields }) value) :
      OperandObserved machine (.location location) value

def parameterLocation (firstCell index : Nat) (readonly : Bool := true) : Location :=
  { root := firstCell + index, fields := [], readOnly := readonly, provenance := (parameter index).symbol }

def gasValue (gas : Completion.GasConsumed) : Runtime.Value :=
  .struct gasConsumedType [("SpentGas", .unsigned gas.spentGas), ("OperationGas", .unsigned gas.operationGas),
    ("BlockGas", .unsigned gas.blockGas), ("BlockStateGas", .unsigned gas.blockStateGas),
    ("MaxUsedGas", .unsigned gas.maxUsedGas), ("GasRefund", .unsigned gas.gasRefund)]

/-- The accepted entry machine and separately supplied object observations are joined only here. -/
structure EntryWitness (machine : Machine) (observed : Observations) where
  input : Runtime.Input
  before : Machine
  caller : RuntimeFrame
  callerCells : String → CellId
  callerValues : String → Runtime.Value
  tail : List Runtime.Task
  callerFuel : Nat
  freshFrame : ∀ old ∈ before.frames, old.id ≠ before.nextFrame
  callerFresh : caller.id ≠ before.nextFrame
  freshCells : ∀ cell ∈ before.cells, cell.id < before.nextCell
  bounded : callerFuel + 1 ≤ 9223372036854775807
  accepted : machine = Runtime.generatedPostRefundFeeHelperEntered before caller
    (.reference processorType observed.objects.processorIdentity) callerCells callerValues tail callerFuel
  parameters : ∀ i < 11, OperandObserved machine (.location (parameterLocation before.nextCell i)) (observed.values i)
  header : observed.values 1 = .reference "global::Nethermind.Core.BlockHeader" observed.objects.headerIdentity
  spec : observed.values 2 = .reference "global::Nethermind.Core.Specs.IReleaseSpec" "spec"
  fork : observed.eip8037 = input.spec.eip8037Enabled
  options : observed.values 4 = .enum "global::Nethermind.Evm.TransactionProcessing.ExecutionOptions"
    observed.options.toInt32.toInt
  gas : observed.values 6 = gasValue observed.spent
  valid : observed.Valid

inductive Phase where
  | entry | routing | fork | execution | state | header | receiver | argument (index : Nat) | applyPayFees
  deriving DecidableEq, Repr

inductive Write where
  | execution (identity : String) (value : Nat)
  | state (identity : String) (value : Nat)
  | header (identity : String) (value : Nat)
  deriving DecidableEq, Repr

structure Residual where
  entryMachine : Machine
  firstCell : CellId
  observed : Observations
  objects : Objects
  phase : Phase
  operandsRev : List Operand
  writes : List Write
  fuel : Nat

def start (machine : Machine) (firstCell : CellId) (observed : Observations) (fuel : Nat) : Residual :=
  { entryMachine := machine, firstCell, observed, objects := observed.objects,
    phase := .entry, operandsRev := [], writes := [], fuel }

def embed (machine : Machine) (observed : Observations) (witness : EntryWitness machine observed)
    (fuel : Nat) : Residual := start machine witness.before.nextCell observed fuel

def payArgument (residual : Residual) (index : Nat) : Operand :=
  match index with
  | 0 | 1 | 2 | 3 => .immediate (residual.observed.values index)
  | 4 => .location (parameterLocation residual.firstCell 5)
  | 5 => .immediate (.unsigned residual.observed.spent.spentGas)
  | 6 => .location (parameterLocation residual.firstCell 7)
  | 7 => .location (parameterLocation residual.firstCell 8)
  | 8 => .location (parameterLocation residual.firstCell 9)
  | _ => .immediate (residual.observed.values 10)

inductive Stop where | fuel | outside | pending deriving DecidableEq, Repr

/-- One specialized source-boundary transition; this is not a generic Runtime.step or C# tick. -/
def step (residual : Residual) : Except Stop Residual := do
  if residual.phase == .applyPayFees then throw .pending
  if residual.fuel == 0 then throw .fuel
  let residual := { residual with fuel := residual.fuel - 1 }
  match residual.phase with
  | .entry => pure { residual with phase := .routing }
  | .routing =>
      if participates residual.observed then pure { residual with phase := .fork } else throw .outside
  | .fork =>
      if residual.observed.eip8037 then pure { residual with phase := .execution } else throw .outside
  | .execution =>
      let value := Accounting.addUInt64 residual.objects.execution (Completion.effectiveBlockGas residual.observed.spent)
      pure { residual with
        phase := .state, objects := { residual.objects with execution := value },
        writes := residual.writes ++ [.execution residual.objects.processorIdentity value] }
  | .state =>
      let value := Accounting.addUInt64 residual.objects.state residual.observed.spent.blockStateGas
      pure { residual with
        phase := .header, objects := { residual.objects with state := value },
        writes := residual.writes ++ [.state residual.objects.processorIdentity value] }
  | .header =>
      let value := Accounting.combineBlockGas residual.objects.execution residual.objects.state
      pure { residual with
        phase := .receiver, objects := { residual.objects with headerGasUsed := value },
        writes := residual.writes ++ [.header residual.objects.headerIdentity value] }
  | .receiver => pure { residual with
      phase := .argument 0,
      operandsRev := .immediate (.reference processorType residual.objects.processorIdentity) :: residual.operandsRev }
  | .argument index =>
      if index < 10 then pure { residual with
        phase := (if index == 9 then .applyPayFees else .argument (index + 1)),
        operandsRev := payArgument residual index :: residual.operandsRev } else throw .outside
  | .applyPayFees => throw .pending

def run : Nat → Residual → Except Stop Residual
  | 0, residual => pure residual
  | count + 1, residual => do run count (← step residual)

def finalObjects (observed : Observations) : Objects :=
  let execution := Accounting.addUInt64 observed.objects.execution (Completion.effectiveBlockGas observed.spent)
  let state := Accounting.addUInt64 observed.objects.state observed.spent.blockStateGas
  { observed.objects with execution, state, headerGasUsed := Accounting.combineBlockGas execution state }

def frontier (initial : Residual) (fuel : Nat) : Residual :=
  let objects := finalObjects initial.observed
  { initial with
    objects, phase := .applyPayFees, fuel,
    operandsRev := ((List.range 10).map (payArgument initial)).reverse ++
      [.immediate (.reference processorType objects.processorIdentity)],
    writes := [.execution objects.processorIdentity objects.execution,
      .state objects.processorIdentity objects.state, .header objects.headerIdentity objects.headerGasUsed] }

set_option maxRecDepth 20000 in
set_option maxHeartbeats 4000000 in
theorem projected_residual_reaches_pending_payFees (machine : Machine) (firstCell : CellId)
    (observed : Observations) (fuel : Nat) (pinned : Pinned observed) :
    run 17 (start machine firstCell observed (fuel + 17)) =
      .ok (frontier (start machine firstCell observed (fuel + 17)) fuel) := by
  rcases pinned with ⟨skip, parallel, fork⟩
  simp [run, step, start, frontier, finalObjects, payArgument, participates, routingOptions,
    Routing.participatesInNormalBlockCounters,
    Eip803x.Generated.SystemTransactionRoutingKernel.participatesInNormalBlockCounters,
    skip, parallel, fork, List.range_succ,
    bind, Except.bind, pure, Except.pure]

theorem accepted_entry_reaches_pending_payFees (machine : Machine) (observed : Observations)
    (witness : EntryWitness machine observed) (fuel : Nat) (pinned : Pinned observed) :
    run 17 (embed machine observed witness (fuel + 17)) =
      .ok (frontier (embed machine observed witness (fuel + 17)) fuel) :=
  projected_residual_reaches_pending_payFees machine witness.before.nextCell observed fuel pinned

theorem entry_and_residual_compose (machine : Machine) (observed : Observations)
    (witness : EntryWitness machine observed) (fuel : Nat) (pinned : Pinned observed) :
    Runtime.steps Runtime.generatedPostRefundFeeHelperEntryProgram witness.input 2
      (Runtime.generatedExecuteSimpleTransferPostRefundCallFrontier false witness.before witness.caller
        (.reference processorType observed.objects.processorIdentity) witness.callerCells witness.callerValues
        witness.tail (witness.callerFuel + 1)) = .next machine ∧
    run 17 (embed machine observed witness (fuel + 17)) =
      .ok (frontier (embed machine observed witness (fuel + 17)) fuel) := by
  constructor
  · have entered := (Runtime.generated_postRefund_feeHelper_call_entry_exact witness.input witness.before witness.caller
      (.reference processorType observed.objects.processorIdentity) witness.callerCells witness.callerValues
      witness.tail witness.callerFuel witness.freshFrame witness.callerFresh witness.freshCells witness.bounded).2.1
    exact entered.trans (congrArg Runtime.StepResult.next witness.accepted.symm)
  · exact accepted_entry_reaches_pending_payFees machine observed witness fuel pinned

set_option maxRecDepth 20000 in
set_option maxHeartbeats 4000000 in
theorem one_short_fuel (machine : Machine) (firstCell : CellId) (observed : Observations)
    (pinned : Pinned observed) :
    run 17 (start machine firstCell observed 16) = .error .fuel := by
  rcases pinned with ⟨skip, parallel, fork⟩
  simp [run, step, start, participates, routingOptions, Routing.participatesInNormalBlockCounters,
    Eip803x.Generated.SystemTransactionRoutingKernel.participatesInNormalBlockCounters,
    skip, parallel, fork, bind, Except.bind, pure, Except.pure] <;> rfl

def pendingInvocation (residual : Residual) : Option (SourceTerm × List Operand) :=
  if residual.phase == .applyPayFees then some (payFeesInvocation, residual.operandsRev.reverse) else none

theorem pending_frontier_exact (initial : Residual) (fuel : Nat) :
    pendingInvocation (frontier initial fuel) = some (payFeesInvocation,
      .immediate (.reference processorType initial.observed.objects.processorIdentity) ::
        (List.range 10).map (payArgument initial)) ∧
    (frontier initial fuel).entryMachine = initial.entryMachine ∧
    step (frontier initial fuel) = .error .pending := by
  simp [pendingInvocation, frontier, finalObjects, step] <;> rfl

def NoWrap (observed : Observations) : Prop :=
  observed.objects.execution + Completion.effectiveBlockGas observed.spent ≤ Accounting.uint64Max ∧
    observed.objects.state + observed.spent.blockStateGas ≤ Accounting.uint64Max

theorem natural_counter_refinement (observed : Observations)
    (valid : observed.Valid) (noWrap : NoWrap observed) :
    ((finalObjects observed).execution, (finalObjects observed).state, (finalObjects observed).headerGasUsed) =
      (observed.objects.execution + Completion.effectiveBlockGas observed.spent,
       observed.objects.state + observed.spent.blockStateGas,
       max (observed.objects.execution + Completion.effectiveBlockGas observed.spent)
         (observed.objects.state + observed.spent.blockStateGas)) := by
  have effectiveFits : Completion.effectiveBlockGas observed.spent ≤ Accounting.uint64Max := by
    unfold Completion.effectiveBlockGas SimpleTransferCompletionExtractor.StageB.Leaf.Generated.effectiveBlockGas
    split
    · exact valid.2.2.1
    · exact valid.2.1
  let previous : Eip803x.BlockReceiptGas.Totals :=
    { executionGas := observed.objects.execution, stateGas := observed.objects.state, receiptGas := 0 }
  let delta : Eip803x.BlockReceiptGas.Delta :=
    { executionGas := Completion.effectiveBlockGas observed.spent, stateGas := observed.spent.blockStateGas, paidGas := 0 }
  have result := Eip803x.Refinement.BlockReceiptGasAccounting.generatedAccumulate_refines_spec previous delta
    ⟨⟨valid.1.1, valid.1.2.1, Nat.zero_le _⟩, effectiveFits, valid.2.2.2, Nat.zero_le _,
      noWrap.1, noWrap.2, Nat.zero_le _⟩
  have projected := congrArg (fun value : Eip803x.BlockReceiptGas.Result =>
    (value.totals.executionGas, value.totals.stateGas, value.headerGasUsed)) result
  have executionFits : observed.objects.execution ≤ Eip803x.Generated.TransactionGasInitializationKernel.uint64Max := valid.1.1
  have stateFits : observed.objects.state ≤ Eip803x.Generated.TransactionGasInitializationKernel.uint64Max := valid.1.2.1
  have gasFits : observed.spent.blockStateGas ≤ Eip803x.Generated.TransactionGasInitializationKernel.uint64Max := valid.2.2.2
  have effectiveFits' : Completion.effectiveBlockGas observed.spent ≤ Eip803x.Generated.TransactionGasInitializationKernel.uint64Max := effectiveFits
  simpa [Eip803x.Refinement.BlockReceiptGasAccounting.toSpecResult,
    Eip803x.Generated.BlockReceiptGasAccountingKernel.accumulate,
    Eip803x.Generated.BlockReceiptGasAccountingKernel.accumulateNormalized,
    Eip803x.Generated.BlockReceiptGasAccountingKernel.fromTotalsNormalized,
    Eip803x.Generated.BlockReceiptGasAccountingKernel.normalizeUInt64,
    Eip803x.Generated.TransactionGasInitializationKernel.normalizeUInt64,
    Eip803x.BlockReceiptGas.accumulate, Eip803x.BlockReceiptGas.fromTotals,
    previous, delta, finalObjects, Accounting.addUInt64, Accounting.combineBlockGas,
    executionFits, stateFits, gasFits, effectiveFits'] using projected

end SimpleTransferCompletionExtractor.StageB.FeeHelper
