-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import SimpleTransferCompletionExtractor.StageB.FeeHelper
import SimpleTransferCompletionExtractor.StageB.PayFees.Generated.PayFees
import Eip803x.Word256

namespace SimpleTransferCompletionExtractor.StageB.PayFees

open SimpleTransferCompletionExtractor.StageB.Runtime

abbrev Word := Eip803x.UInt256

/-- Values observed at the named source reads, not a claim that external objects are immutable. -/
structure Observations where
  receiver : Generated.Receiver
  premium : Word
  paid : UInt64
  effectivePrice : Word
  blobFee : Word
  destroyProbeBeneficiary : String
  beneficiaryArgument : String
  beneficiaryWorld : String
  baseFeeAfterBeneficiary : Word
  freeAfterBeneficiary : Bool
  eip1559AfterBeneficiary : Bool
  supportsBlobsAfterBeneficiary : Bool
  blobCollectorAfterBeneficiary : Bool
  collectorGuard : Option String
  collectorArgument : String
  collectorWorld : String
  tracingAfterCollector : Bool
  tracerIdentity : String
  specIdentity : String
  emptyDestroyList : Bool

inductive Read where
  | word (point : String) (value : Word)
  | paid (value : UInt64)
  | address (point : String) (value : String)
  | reference (point : String) (value : String)
  | flag (point : String) (value : Bool)
  | collector (value : Option String)
  deriving DecidableEq, Repr

inductive Effect where
  | creditAndCreate (world address : String) (amount : Nat) (spec : String)
  | reportFees (tracer : String) (premium burnt : Nat)
  deriving DecidableEq, Repr

inductive Event where
  | read (value : Read)
  | effect (value : Effect)
  | returnedVoid
  deriving DecidableEq, Repr

def premiumAmount (o : Observations) : Nat := o.premium.val * o.paid.toNat % (2^256)
def cappedBase (o : Observations) : Nat := min o.baseFeeAfterBeneficiary.val o.effectivePrice.val
def baseAmount (o : Observations) : Nat :=
  if o.freeAfterBeneficiary then 0 else cappedBase o * o.paid.toNat % (2^256)
def collectedAmount (o : Observations) : Nat :=
  if o.supportsBlobsAfterBeneficiary && o.blobCollectorAfterBeneficiary then
    ((if o.eip1559AfterBeneficiary then baseAmount o else 0) + o.blobFee.val) % (2^256)
  else if o.eip1559AfterBeneficiary then baseAmount o else 0
def burntAmount (o : Observations) : Nat := (baseAmount o + o.blobFee.val) % (2^256)

def collectorEnabled (o : Observations) (amount : Nat) : Bool := o.collectorGuard.isSome && amount != 0

def effects (o : Observations) : List Effect :=
  [.creditAndCreate o.beneficiaryWorld o.beneficiaryArgument (premiumAmount o) o.specIdentity] ++
  (if collectorEnabled o (collectedAmount o) then
    [.creditAndCreate o.collectorWorld o.collectorArgument (collectedAmount o) o.specIdentity] else []) ++
  (if o.tracingAfterCollector then [.reportFees o.tracerIdentity (premiumAmount o) (burntAmount o)] else [])

def reads (o : Observations) : List Read :=
  [.word "premiumPerGas" o.premium, .paid o.paid,
   .address "destroyProbe.header.GasBeneficiary" o.destroyProbeBeneficiary,
   .flag "substate.DestroyListContains" false,
   .reference "beneficiary.WorldState" o.beneficiaryWorld,
   .address "beneficiary.header.GasBeneficiary" o.beneficiaryArgument,
   .word "header.BaseFeePerGas" o.baseFeeAfterBeneficiary, .word "effectiveGasPrice" o.effectivePrice,
   .flag "tx.IsFree" o.freeAfterBeneficiary,
   .flag "spec.IsEip1559Enabled" o.eip1559AfterBeneficiary,
   .flag "tx.SupportsBlobs" o.supportsBlobsAfterBeneficiary] ++
  (if o.supportsBlobsAfterBeneficiary then [.flag "spec.IsEip4844FeeCollectorEnabled" o.blobCollectorAfterBeneficiary] else []) ++
  (if o.supportsBlobsAfterBeneficiary && o.blobCollectorAfterBeneficiary then [.word "collector.blobBaseFee" o.blobFee] else []) ++
  [.collector o.collectorGuard] ++
  (if collectorEnabled o (collectedAmount o) then
    [.reference "collector.WorldState" o.collectorWorld, .address "collector.spec.FeeCollector" o.collectorArgument] else []) ++
  [.flag "tracer.IsTracingFees" o.tracingAfterCollector] ++
  (if o.tracingAfterCollector then [.word "report.blobBaseFee" o.blobFee] else [])

structure State where
  source : FeeHelper.Residual
  observed : Observations
  todo : List Generated.Op
  premium : Nat
  capped : Nat
  base : Nat
  collected : Nat
  readLog : List Read
  effectLog : List Effect
  events : List Event
  returnedVoid : Bool
  fuel : Nat

inductive Stop where | fuel | outside | returned deriving DecidableEq, Repr

def start (source : FeeHelper.Residual) (observed : Observations) (fuel : Nat) : State :=
  { source, observed, todo := Generated.program, premium := 0, capped := 0, base := 0,
    collected := 0, readLog := [], effectLog := [], events := [], returnedVoid := false, fuel }

private def recordStage (before after : State) : State :=
  { after with events := before.events ++
      (after.readLog.drop before.readLog.length).map Event.read ++
      (after.effectLog.drop before.effectLog.length).map Event.effect ++
      (if after.returnedVoid then [.returnedVoid] else []) }

/-- Each operation is one projected source stage, not a generic-runtime microstep or CLR tick. -/
def step (state : State) : Except Stop State := do
  if state.returnedVoid then throw .returned
  if state.fuel == 0 then throw .fuel
  let op :: tail := state.todo | throw .outside
  let s := { state with todo := tail, fuel := state.fuel-1 }
  let o := s.observed
  match op with
  | .premium => pure (recordStage s { s with
      premium := Generated.priorityFee o.premium.val o.paid.toNat,
      readLog := s.readLog ++ [.word "premiumPerGas" o.premium, .paid o.paid] })
  | .destroyProbe =>
      if !o.emptyDestroyList then throw .outside
      pure (recordStage s { s with readLog := s.readLog ++ [.address "destroyProbe.header.GasBeneficiary" o.destroyProbeBeneficiary,
        .flag "substate.DestroyListContains" false] })
  | .beneficiary => pure (recordStage s { s with
      effectLog := s.effectLog ++ [.creditAndCreate o.beneficiaryWorld o.beneficiaryArgument s.premium o.specIdentity],
      readLog := s.readLog ++ [.reference "beneficiary.WorldState" o.beneficiaryWorld,
        .address "beneficiary.header.GasBeneficiary" o.beneficiaryArgument] })
  | .effectiveBase => pure (recordStage s { s with
      capped := Generated.effectiveBaseFee o.baseFeeAfterBeneficiary.val o.effectivePrice.val,
      readLog := s.readLog ++ [.word "header.BaseFeePerGas" o.baseFeeAfterBeneficiary, .word "effectiveGasPrice" o.effectivePrice] })
  | .baseFees => pure (recordStage s { s with
      base := Generated.baseFees o.freeAfterBeneficiary s.capped o.paid.toNat,
      readLog := s.readLog ++ [.flag "tx.IsFree" o.freeAfterBeneficiary] })
  | .initialCollector => pure (recordStage s { s with
      collected := if o.eip1559AfterBeneficiary then s.base else 0,
      readLog := s.readLog ++ [.flag "spec.IsEip1559Enabled" o.eip1559AfterBeneficiary] })
  | .blobCollector => pure (recordStage s { s with
      collected := if o.supportsBlobsAfterBeneficiary && o.blobCollectorAfterBeneficiary then
        (s.collected + o.blobFee.val) % Generated.wordModulus else s.collected,
      readLog := s.readLog ++ [.flag "tx.SupportsBlobs" o.supportsBlobsAfterBeneficiary] ++
        (if o.supportsBlobsAfterBeneficiary then [.flag "spec.IsEip4844FeeCollectorEnabled" o.blobCollectorAfterBeneficiary] else []) ++
        (if o.supportsBlobsAfterBeneficiary && o.blobCollectorAfterBeneficiary then [.word "collector.blobBaseFee" o.blobFee] else []) })
  | .collector => pure (recordStage s { s with
      effectLog := s.effectLog ++ (if collectorEnabled o s.collected then
        [.creditAndCreate o.collectorWorld o.collectorArgument s.collected o.specIdentity] else []),
      readLog := s.readLog ++ [.collector o.collectorGuard] ++ (if collectorEnabled o s.collected then
        [.reference "collector.WorldState" o.collectorWorld, .address "collector.spec.FeeCollector" o.collectorArgument] else []) })
  | .report => pure (recordStage s { s with
      effectLog := s.effectLog ++ (if o.tracingAfterCollector then
        [.reportFees o.tracerIdentity s.premium (Generated.reportedBurnt s.base o.blobFee.val)] else []),
      readLog := s.readLog ++ [.flag "tracer.IsTracingFees" o.tracingAfterCollector] ++
        (if o.tracingAfterCollector then [.word "report.blobBaseFee" o.blobFee] else []) })
  | .returnVoid => pure (recordStage s { s with returnedVoid := true })

def run : Nat → State → Except Stop State
  | 0, state => .ok state
  | count+1, state => do run count (← step state)

def events (o : Observations) : List Event :=
  ((reads o).take 6).map Event.read ++
     [.effect (.creditAndCreate o.beneficiaryWorld o.beneficiaryArgument (premiumAmount o) o.specIdentity)] ++
     (((reads o).drop 6).take (6 + (if o.supportsBlobsAfterBeneficiary then 1 else 0) +
       (if o.supportsBlobsAfterBeneficiary && o.blobCollectorAfterBeneficiary then 1 else 0) +
       (if collectorEnabled o (collectedAmount o) then 2 else 0))).map Event.read ++
     (if collectorEnabled o (collectedAmount o) then
       [.effect (.creditAndCreate o.collectorWorld o.collectorArgument (collectedAmount o) o.specIdentity)] else []) ++
     [.read (.flag "tracer.IsTracingFees" o.tracingAfterCollector)] ++
     (if o.tracingAfterCollector then [.read (.word "report.blobBaseFee" o.blobFee),
       .effect (.reportFees o.tracerIdentity (premiumAmount o) (burntAmount o))] else []) ++ [.returnedVoid]

def completed (source : FeeHelper.Residual) (o : Observations) (fuel : Nat) : State :=
  { source, observed := o, todo := [], premium := premiumAmount o, capped := cappedBase o,
    base := baseAmount o, collected := collectedAmount o, readLog := reads o,
    effectLog := effects o, events := events o, returnedVoid := true, fuel }

theorem standard_dispatch_exact (receiver : Generated.Receiver) :
    Generated.resolve receiver = .standardBase ∧ Generated.systemOwner = .systemOverride ∧
    Generated.systemBodyEmpty = true := ⟨rfl, rfl, rfl⟩

theorem arithmetic_refines (o : Observations) :
    Generated.priorityFee o.premium.val o.paid.toNat = premiumAmount o ∧
    Generated.effectiveBaseFee o.baseFeeAfterBeneficiary.val o.effectivePrice.val = cappedBase o ∧
    Generated.baseFees o.freeAfterBeneficiary (cappedBase o) o.paid.toNat = baseAmount o ∧
    Generated.collectorFees o.eip1559AfterBeneficiary o.supportsBlobsAfterBeneficiary
      o.blobCollectorAfterBeneficiary (baseAmount o) o.blobFee.val = collectedAmount o ∧
    Generated.reportedBurnt (baseAmount o) o.blobFee.val = burntAmount o := by
  cases h : o.freeAfterBeneficiary <;>
    simp [Generated.priorityFee, Generated.effectiveBaseFee, Generated.baseFees,
      Generated.collectorFees, Generated.reportedBurnt, Generated.wordModulus,
      premiumAmount, cappedBase, baseAmount, collectedAmount, burntAmount, h]

theorem modular_outputs_bounded (o : Observations) :
    premiumAmount o < 2^256 ∧ baseAmount o < 2^256 ∧
    collectedAmount o < 2^256 ∧ burntAmount o < 2^256 := by
  have positive : 0 < 2^256 := by decide
  have baseFits : baseAmount o < 2^256 := by
    unfold baseAmount
    split
    · exact positive
    · exact Nat.mod_lt _ positive
  refine ⟨Nat.mod_lt _ positive, baseFits, ?_, Nat.mod_lt _ positive⟩
  unfold collectedAmount
  split
  · exact Nat.mod_lt _ positive
  · split
    · exact baseFits
    · exact positive

set_option maxRecDepth 20000 in
set_option maxHeartbeats 8000000 in
theorem projected_payFees_exact (source : FeeHelper.Residual) (o : Observations) (fuel : Nat)
    (empty : o.emptyDestroyList = true) :
    run 10 (start source o (fuel+10)) = .ok (completed source o fuel) := by
  cases free : o.freeAfterBeneficiary <;>
    cases blobs : o.supportsBlobsAfterBeneficiary <;>
    cases blobCollector : o.blobCollectorAfterBeneficiary <;>
    cases collector : collectorEnabled o (collectedAmount o) <;>
    cases tracing : o.tracingAfterCollector
  all_goals simp [collectedAmount, baseAmount, cappedBase, free, blobs, blobCollector] at collector
  all_goals
    simp [run, step, recordStage, start, completed, events, Generated.program, premiumAmount, cappedBase, baseAmount,
      collectedAmount, burntAmount, reads, effects, Generated.priorityFee, Generated.effectiveBaseFee,
      Generated.baseFees, Generated.reportedBurnt, Generated.wordModulus, empty, free, blobs, blobCollector, collector, tracing,
      bind, Except.bind, pure, Except.pure]

set_option maxRecDepth 20000 in
set_option maxHeartbeats 8000000 in
theorem one_short_fuel (source : FeeHelper.Residual) (o : Observations)
    (empty : o.emptyDestroyList = true) : run 10 (start source o 9) = .error .fuel := by
  simp [run, step, recordStage, start, Generated.program, empty, bind, Except.bind, pure, Except.pure] <;> rfl

/-- Each external effect is supplied in source order; no balance, creation, or provider law is assumed. -/
inductive EffectExecution {World : Type} (apply : World → Effect → World → Prop) :
    World → List Effect → World → Prop where
  | done (world) : EffectExecution apply world [] world
  | next (before middle after effect tail) (effectReturns : apply before effect middle)
      (rest : EffectExecution apply middle tail after) : EffectExecution apply before (effect :: tail) after

inductive EventExecution {World : Type} (observe : World → Read → Prop)
    (apply : World → Effect → World → Prop) : World → List Event → World → Prop where
  | done (world) : EventExecution observe apply world [] world
  | read (before after event tail) (observed : observe before event)
      (rest : EventExecution observe apply before tail after) :
      EventExecution observe apply before (.read event :: tail) after
  | effect (before middle after event tail) (applied : apply before event middle)
      (rest : EventExecution observe apply middle tail after) :
      EventExecution observe apply before (.effect event :: tail) after
  | returned (before after tail) (rest : EventExecution observe apply before tail after) :
      EventExecution observe apply before (.returnedVoid :: tail) after

def Event.effect? : Event → Option Effect
  | .effect value => some value
  | _ => none

private theorem read_events_have_no_effects (values : List Read) :
    (values.map Event.read).filterMap Event.effect? = [] := by
  simp [List.filterMap_map, Event.effect?]

theorem events_project_effects (o : Observations) : (events o).filterMap Event.effect? = effects o := by
  cases collector : collectorEnabled o (collectedAmount o) <;>
    cases tracing : o.tracingAfterCollector <;>
    simp only [events, effects, collector, tracing, Bool.false_eq_true,
      if_false, if_true, List.filterMap_append, read_events_have_no_effects,
      List.filterMap_cons, List.filterMap_nil, Event.effect?, List.nil_append, List.append_nil]

theorem event_execution_preserves_effect_order {World : Type} (observe : World → Read → Prop)
    (apply : World → Effect → World → Prop) (initial final : World) (events : List Event)
    (witness : EventExecution observe apply initial events final) :
    EffectExecution apply initial (events.filterMap Event.effect?) final := by
  induction witness with
  | done world => exact .done world
  | read before after event tail observed rest induction => exact induction
  | effect before middle after event tail applied rest induction =>
      exact .next before middle after event _ applied induction
  | returned before after tail rest induction => exact induction

/-- The read witness includes repeated reads after effects; stability of readonly aliases is explicit. -/
structure Boundary (helper : FeeHelper.Residual) (o : Observations)
    (observes : Nat → Read → Prop) where
  machine : Machine
  helperObservations : FeeHelper.Observations
  entry : FeeHelper.EntryWitness machine helperObservations
  helperFuel : Nat
  helperPinned : FeeHelper.Pinned helperObservations
  pending : helper = FeeHelper.frontier (FeeHelper.embed machine helperObservations entry (helperFuel+17)) helperFuel
  empty : o.emptyDestroyList = true
  destroyListNullType : String
  substateDestroyListNull : FeeHelper.FieldsObserved (helperObservations.values 5) ["_destroyList"] (.null destroyListNullType)
  paid : o.paid.toNat = helperObservations.spent.spentGas
  premium : helperObservations.values 7 = .uint256 o.premium.val
  effective : helperObservations.values 8 = .uint256 o.effectivePrice.val
  blob : helperObservations.values 9 = .uint256 o.blobFee.val
  spec : helperObservations.values 2 = .reference "global::Nethermind.Core.Specs.IReleaseSpec" o.specIdentity
  tracer : helperObservations.values 3 = .reference "global::Nethermind.Evm.Tracing.ITxTracer" o.tracerIdentity
  readsObserved : ∀ index event, (reads o)[index]? = some event → observes index event
  ordinaryReceiverObserved : Runtime.Value → Generated.Receiver → Prop
  receiver : ordinaryReceiverObserved (.reference FeeHelper.processorType helperObservations.objects.processorIdentity) o.receiver
  callerPresent : machine.frames.find? (fun frame => frame.id == entry.caller.id) = some entry.caller

theorem constructed_simple_transfer_has_null_destroy_list (oog : Bool) :
    FeeHelper.FieldsObserved (Runtime.generatedExecuteSimpleTransferConstructedSubstate oog)
      ["_destroyList"] (.null "") := by
  apply FeeHelper.FieldsObserved.field
  · rfl
  · exact .root _

theorem pending_boundary_composes (helper : FeeHelper.Residual) (o : Observations)
    (observes : Nat → Read → Prop) (boundary : Boundary helper o observes) (fuel : Nat) :
    FeeHelper.run 17 (FeeHelper.embed boundary.machine boundary.helperObservations boundary.entry
      (boundary.helperFuel+17)) = .ok helper ∧
    run 10 (start helper o (fuel+10)) = .ok (completed helper o fuel) := by
  constructor
  · exact (FeeHelper.accepted_entry_reaches_pending_payFees boundary.machine boundary.helperObservations
      boundary.entry boundary.helperFuel boundary.helperPinned).trans (congrArg Except.ok boundary.pending.symm)
  · exact projected_payFees_exact helper o fuel boundary.empty

theorem projected_effects_exact {World : Type} (apply : World → Effect → World → Prop)
    (initial final : World) (source : FeeHelper.Residual) (o : Observations) (fuel : Nat)
    (witness : EffectExecution apply initial (effects o) final) :
    EffectExecution apply initial (completed source o fuel).effectLog final ∧
    (completed source o fuel).source = source ∧
    step (completed source o fuel) = .error .returned := by
  exact ⟨witness, rfl, by simp [step, completed]; rfl⟩

end SimpleTransferCompletionExtractor.StageB.PayFees
