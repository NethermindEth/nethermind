-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import PrecompileFrameExtractor.Generated.PrecompileFrameStageB
import PrecompileFrameExtractor.Specification.PrecompileFrameStageBReference

namespace Eip803x.PrecompileFrame.StageB.Refinement

namespace G
abbrev GasState := Eip803x.PrecompileFrame.StageB.Generated.GasState
abbrev LeafOracleResult := Eip803x.PrecompileFrame.StageB.Generated.LeafOracleResult
abbrev LeafOracle := Eip803x.PrecompileFrame.StageB.Generated.LeafOracle
abbrev Status := Eip803x.PrecompileFrame.StageB.Generated.Status
abbrev Result := Eip803x.PrecompileFrame.StageB.Generated.Result
abbrev Completion := Eip803x.PrecompileFrame.StageB.Generated.Completion
abbrev Effect := Eip803x.PrecompileFrame.StageB.Generated.Effect
abbrev Input := Eip803x.PrecompileFrame.StageB.Generated.Input
abbrev Outcome := Eip803x.PrecompileFrame.StageB.Generated.Outcome
abbrev PricingResult := Eip803x.PrecompileFrame.StageB.Generated.PricingResult
abbrev uint64Max := Eip803x.PrecompileFrame.StageB.Generated.uint64Max
abbrev directEligible := Eip803x.PrecompileFrame.StageB.Generated.directEligible
abbrev cancellationBoundary := Eip803x.PrecompileFrame.StageB.Generated.cancellationBoundary
abbrev clearExecutionGas := Eip803x.PrecompileFrame.StageB.Generated.clearExecutionGas
abbrev createChild := Eip803x.PrecompileFrame.StageB.Generated.createChild
abbrev restoreChildStateGasOnHalt :=
  Eip803x.PrecompileFrame.StageB.Generated.restoreChildStateGasOnHalt
abbrev refundChildGas := Eip803x.PrecompileFrame.StageB.Generated.refundChildGas
abbrev price := Eip803x.PrecompileFrame.StageB.Generated.price
abbrev canReachBoundary := Eip803x.PrecompileFrame.StageB.Generated.canReachBoundary
abbrev executeBody := Eip803x.PrecompileFrame.StageB.Generated.executeBody
abbrev applyBoundaryCancellation :=
  Eip803x.PrecompileFrame.StageB.Generated.applyBoundaryCancellation
abbrev executePrecompile := Eip803x.PrecompileFrame.StageB.Generated.executePrecompile
end G

namespace R
abbrev GasState := Eip803x.PrecompileFrame.StageB.Reference.GasState
abbrev LeafOracleResult := Eip803x.PrecompileFrame.StageB.Reference.LeafOracleResult
abbrev LeafOracle := Eip803x.PrecompileFrame.StageB.Reference.LeafOracle
abbrev Status := Eip803x.PrecompileFrame.StageB.Reference.Status
abbrev Result := Eip803x.PrecompileFrame.StageB.Reference.Result
abbrev Completion := Eip803x.PrecompileFrame.StageB.Reference.Completion
abbrev Effect := Eip803x.PrecompileFrame.StageB.Reference.Effect
abbrev Input := Eip803x.PrecompileFrame.StageB.Reference.Input
abbrev Outcome := Eip803x.PrecompileFrame.StageB.Reference.Outcome
abbrev ChildEntry := Eip803x.PrecompileFrame.StageB.Reference.ChildEntry
abbrev PricingResult := Eip803x.PrecompileFrame.StageB.Reference.PricingResult
abbrev uint64Max := Eip803x.PrecompileFrame.StageB.Reference.uint64Max
abbrev directEligible := Eip803x.PrecompileFrame.StageB.Reference.directEligible
abbrev cancellationBoundary := Eip803x.PrecompileFrame.StageB.Reference.cancellationBoundary
abbrev clearExecutionGas := Eip803x.PrecompileFrame.StageB.Reference.clearExecutionGas
abbrev createChild := Eip803x.PrecompileFrame.StageB.Reference.createChild
abbrev restoreChildStateGasOnHalt :=
  Eip803x.PrecompileFrame.StageB.Reference.restoreChildStateGasOnHalt
abbrev refundChildGas := Eip803x.PrecompileFrame.StageB.Reference.refundChildGas
abbrev price := Eip803x.PrecompileFrame.StageB.Reference.price
abbrev canReachBoundary := Eip803x.PrecompileFrame.StageB.Reference.canReachBoundary
abbrev executeBody := Eip803x.PrecompileFrame.StageB.Reference.executeBody
abbrev applyBoundaryCancellation :=
  Eip803x.PrecompileFrame.StageB.Reference.applyBoundaryCancellation
abbrev executePrecompile := Eip803x.PrecompileFrame.StageB.Reference.executePrecompile
end R

@[reducible]
def toReferenceGas (gas : G.GasState) : R.GasState :=
  { gasLeft := gas.gasLeft
    stateReservoir := gas.stateReservoir
    stateGasUsed := gas.stateGasUsed
    stateGasSpill := gas.stateGasSpill
    stateGasSpillRefunded := gas.stateGasSpillRefunded }

def toReferenceLeafResult : G.LeafOracleResult -> R.LeafOracleResult
  | .success output => .success output
  | .returnedFailure => .returnedFailure
  | .managedException => .managedException

def toReferenceOracle (oracle : G.LeafOracle) : R.LeafOracle :=
  fun address input => toReferenceLeafResult (oracle address input)

def toReferenceStatus : G.Status -> R.Status
  | .notStarted => .notStarted
  | .declined => .declined
  | .inputMemoryOutOfGas => .inputMemoryOutOfGas
  | .pricingOverflow => .pricingOverflow
  | .pricingOutOfGas => .pricingOutOfGas
  | .returnedFailure => .returnedFailure
  | .managedException => .managedException
  | .outputCopyOutOfGas => .outputCopyOutOfGas
  | .success => .success

def toReferenceResult : G.Result -> R.Result
  | .cancelled => .cancelled
  | .declined => .declined
  | .outOfGas => .outOfGas
  | .stackFailure => .stackFailure
  | .stackSuccess => .stackSuccess

def toReferenceCompletion : G.Completion -> R.Completion
  | .returned => .returned
  | .cancelledBeforeDispatch => .cancelledBeforeDispatch
  | .cancelledAtBoundary => .cancelledAtBoundary

def toReferenceEffect : G.Effect -> R.Effect
  | .preDispatchCancellationPoll => .preDispatchCancellationPoll
  | .inputLoad => .inputLoad
  | .childCreate => .childCreate
  | .pricing => .pricing
  | .runOracle => .runOracle
  | .executionGasClear => .executionGasClear
  | .stateGasRestore => .stateGasRestore
  | .accountTouch => .accountTouch
  | .childRefund => .childRefund
  | .returnDataClear => .returnDataClear
  | .returnDataSet => .returnDataSet
  | .outputCopy => .outputCopy
  | .returnOutOfGas => .returnOutOfGas
  | .stackFailure => .stackFailure
  | .stackSuccess => .stackSuccess
  | .boundaryCancellationPoll => .boundaryCancellationPoll

@[reducible]
def toReferenceInput (input : G.Input) : R.Input :=
  { address := input.address
    callData := input.callData
    instructionTracing := input.instructionTracing
    actionTracing := input.actionTracing
    isRipemd160 := input.isRipemd160
    cancelable := input.cancelable
    cancelledBeforeDispatch := input.cancelledBeforeDispatch
    completedWithoutException := input.completedWithoutException
    opcodeCount := input.opcodeCount
    cancelledAtBoundary := input.cancelledAtBoundary
    nextProgramCounter := input.nextProgramCounter
    codeLength := input.codeLength
    inputMemoryValid := input.inputMemoryValid
    parentGas := toReferenceGas input.parentGas
    forwardedGas := input.forwardedGas
    baseCost := input.baseCost
    dataCost := input.dataCost
    outputLength := input.outputLength
    outputCopyValid := input.outputCopyValid
    priorReturnData := input.priorReturnData }

@[reducible]
def toReferenceOutcome (outcome : G.Outcome) : R.Outcome :=
  { status := toReferenceStatus outcome.status
    result := toReferenceResult outcome.result
    completion := toReferenceCompletion outcome.completion
    parentGas := toReferenceGas outcome.parentGas
    childGasAtExit := outcome.childGasAtExit.map toReferenceGas
    returnData := outcome.returnData
    copiedOutput := outcome.copiedOutput
    outputWritten := outcome.outputWritten
    stackValue := outcome.stackValue
    accountTouched := outcome.accountTouched
    oracleInvoked := outcome.oracleInvoked
    effects := outcome.effects.map toReferenceEffect }

def toReferencePricing : G.PricingResult -> R.PricingResult
  | .overflow gas => .overflow (toReferenceGas gas)
  | .outOfGas gas => .outOfGas (toReferenceGas gas)
  | .success gas => .success (toReferenceGas gas)

theorem direct_eligible_refines (input : G.Input) :
    G.directEligible input = R.directEligible (toReferenceInput input) := by
  rfl

theorem cancellation_boundary_refines (input : G.Input) :
    G.cancellationBoundary input = R.cancellationBoundary (toReferenceInput input) := by
  rfl

theorem clear_execution_gas_refines (gas : G.GasState) :
    toReferenceGas (G.clearExecutionGas gas) = R.clearExecutionGas (toReferenceGas gas) := by
  rfl

theorem create_child_refines (gas : G.GasState) (forwarded : Nat) :
    toReferenceGas (G.createChild gas forwarded).parent =
        (R.createChild (toReferenceGas gas) forwarded).parent /\
      toReferenceGas (G.createChild gas forwarded).child =
        (R.createChild (toReferenceGas gas) forwarded).child := by
  exact And.intro rfl rfl

theorem restore_child_state_gas_on_halt_refines (parent child : G.GasState) :
    toReferenceGas (G.restoreChildStateGasOnHalt parent child) =
      R.restoreChildStateGasOnHalt (toReferenceGas parent) (toReferenceGas child) := by
  rfl

theorem refund_child_gas_refines (parent child : G.GasState) :
    toReferenceGas (G.refundChildGas parent child) =
      R.refundChildGas (toReferenceGas parent) (toReferenceGas child) := by
  rfl

theorem pricing_refines (baseCost dataCost : Nat) (gas : G.GasState) :
    toReferencePricing (G.price baseCost dataCost gas) =
      R.price baseCost dataCost (toReferenceGas gas) := by
  by_cases hData : dataCost ≤ 2 ^ 64 - 1
  · by_cases hBase : baseCost ≤ 2 ^ 64 - 1 - dataCost
    · by_cases hGas : baseCost + dataCost ≤ gas.gasLeft
      · simp [G.price, R.price,
          Eip803x.PrecompileFrame.StageB.Generated.price,
          Eip803x.PrecompileFrame.StageB.Reference.price,
          Eip803x.PrecompileFrame.StageB.Generated.uint64Max,
          Eip803x.PrecompileFrame.StageB.Reference.uint64Max,
          hData, hBase, hGas, toReferencePricing, toReferenceGas]
      · simp [G.price, R.price,
          Eip803x.PrecompileFrame.StageB.Generated.price,
          Eip803x.PrecompileFrame.StageB.Reference.price,
          Eip803x.PrecompileFrame.StageB.Generated.uint64Max,
          Eip803x.PrecompileFrame.StageB.Reference.uint64Max,
          hData, hBase, hGas, toReferencePricing, toReferenceGas]
    · simp [G.price, R.price,
        Eip803x.PrecompileFrame.StageB.Generated.price,
        Eip803x.PrecompileFrame.StageB.Reference.price,
        Eip803x.PrecompileFrame.StageB.Generated.uint64Max,
        Eip803x.PrecompileFrame.StageB.Reference.uint64Max,
        hData, hBase, toReferencePricing, toReferenceGas]
  · simp [G.price, R.price,
      Eip803x.PrecompileFrame.StageB.Generated.price,
      Eip803x.PrecompileFrame.StageB.Reference.price,
      Eip803x.PrecompileFrame.StageB.Generated.uint64Max,
      Eip803x.PrecompileFrame.StageB.Reference.uint64Max,
      hData, toReferencePricing, toReferenceGas]

theorem can_reach_boundary_refines (status : G.Status) :
    G.canReachBoundary status = R.canReachBoundary (toReferenceStatus status) := by
  cases status <;> rfl

private theorem execute_body_refines (oracle : G.LeafOracle) (input : G.Input) :
    toReferenceOutcome (G.executeBody oracle input) =
      R.executeBody (toReferenceOracle oracle) (toReferenceInput input) := by
  have hDirect := direct_eligible_refines input
  change Eip803x.PrecompileFrame.StageB.Generated.directEligible input =
    Eip803x.PrecompileFrame.StageB.Reference.directEligible (toReferenceInput input) at hDirect
  unfold G.executeBody R.executeBody
    Eip803x.PrecompileFrame.StageB.Generated.executeBody
    Eip803x.PrecompileFrame.StageB.Reference.executeBody
    Eip803x.PrecompileFrame.StageB.Reference.decideBody
    Eip803x.PrecompileFrame.StageB.Reference.renderBodyDecision
    Eip803x.PrecompileFrame.StageB.Reference.renderSuccessfulLeaf
  rw [← hDirect]
  split <;> rename_i hEligible
  · simp [toReferenceOutcome, toReferenceGas, toReferenceStatus,
      toReferenceResult, toReferenceCompletion]
  · cases hMemory : input.inputMemoryValid
    · simp [hMemory, toReferenceOutcome, toReferenceGas,
        toReferenceStatus, toReferenceResult, toReferenceCompletion, toReferenceEffect]
    · simp only [Bool.not_true, Bool.false_eq_true, if_false]
      have hChild := create_child_refines input.parentGas input.forwardedGas
      change toReferenceGas
              (Eip803x.PrecompileFrame.StageB.Generated.createChild
                input.parentGas input.forwardedGas).parent =
            (Eip803x.PrecompileFrame.StageB.Reference.createChild
              (toReferenceGas input.parentGas) input.forwardedGas).parent ∧
          toReferenceGas
              (Eip803x.PrecompileFrame.StageB.Generated.createChild
                input.parentGas input.forwardedGas).child =
            (Eip803x.PrecompileFrame.StageB.Reference.createChild
              (toReferenceGas input.parentGas) input.forwardedGas).child at hChild
      generalize hEntry :
        Eip803x.PrecompileFrame.StageB.Generated.createChild
          input.parentGas input.forwardedGas = entry at hChild ⊢
      cases entry with
      | mk parent child =>
        change toReferenceGas parent =
              (Eip803x.PrecompileFrame.StageB.Reference.createChild
                (toReferenceGas input.parentGas) input.forwardedGas).parent ∧
            toReferenceGas child =
              (Eip803x.PrecompileFrame.StageB.Reference.createChild
                (toReferenceGas input.parentGas) input.forwardedGas).child at hChild
        have hRefEntry :
            Eip803x.PrecompileFrame.StageB.Reference.createChild
                (toReferenceGas input.parentGas) input.forwardedGas =
              { parent := toReferenceGas parent, child := toReferenceGas child } := by
          generalize hReferenceEntry :
            Eip803x.PrecompileFrame.StageB.Reference.createChild
              (toReferenceGas input.parentGas) input.forwardedGas = referenceEntry at hChild ⊢
          cases referenceEntry with
          | mk referenceParent referenceChild =>
            change toReferenceGas parent = referenceParent ∧
              toReferenceGas child = referenceChild at hChild
            cases hChild.1
            cases hChild.2
            rfl
        rw [hRefEntry]
        have hPrice := pricing_refines input.baseCost input.dataCost child
        change toReferencePricing
            (Eip803x.PrecompileFrame.StageB.Generated.price
              input.baseCost input.dataCost child) =
          Eip803x.PrecompileFrame.StageB.Reference.price
            input.baseCost input.dataCost (toReferenceGas child) at hPrice
        generalize hp : Eip803x.PrecompileFrame.StageB.Generated.price
          input.baseCost input.dataCost child = priced at hPrice ⊢
        cases priced with
        | overflow failedGas =>
          simp [toReferencePricing] at hPrice
          rw [← hPrice]
          simp [hMemory, toReferenceOutcome, restore_child_state_gas_on_halt_refines,
            toReferenceStatus, toReferenceResult, toReferenceCompletion, toReferenceEffect]
        | outOfGas failedGas =>
          simp [toReferencePricing] at hPrice
          rw [← hPrice]
          simp [hMemory, toReferenceOutcome, restore_child_state_gas_on_halt_refines,
            toReferenceStatus, toReferenceResult, toReferenceCompletion, toReferenceEffect]
        | success pricedGas =>
          simp [toReferencePricing] at hPrice
          rw [← hPrice]
          generalize ho : oracle input.address input.callData = oracleResult
          cases oracleResult with
          | returnedFailure =>
            simp [hMemory, toReferenceOracle, ho, toReferenceLeafResult, toReferenceOutcome,
              clear_execution_gas_refines, restore_child_state_gas_on_halt_refines,
              toReferenceStatus, toReferenceResult, toReferenceCompletion, toReferenceEffect]
          | managedException =>
            simp [hMemory, toReferenceOracle, ho, toReferenceLeafResult, toReferenceOutcome,
              clear_execution_gas_refines, restore_child_state_gas_on_halt_refines,
              toReferenceStatus, toReferenceResult, toReferenceCompletion, toReferenceEffect]
          | success output =>
            simp [toReferenceOracle, ho, toReferenceLeafResult]
            split <;> rename_i hCopy
            · simp [hMemory, toReferenceOutcome, refund_child_gas_refines,
                toReferenceStatus, toReferenceResult, toReferenceCompletion, toReferenceEffect]
              simpa [and_assoc] using hCopy
            · simp [hMemory, toReferenceOutcome, refund_child_gas_refines,
                toReferenceStatus, toReferenceResult, toReferenceCompletion, toReferenceEffect]
              by_cases hLength : input.outputLength = 0
              · simp [hLength]
              by_cases hOutput : output = []
              · simp [hOutput]
              cases hValid : input.outputCopyValid <;> simp_all [toReferenceEffect]

private theorem apply_boundary_refines (input : G.Input) (outcome : G.Outcome) :
    toReferenceOutcome (G.applyBoundaryCancellation input outcome) =
      R.applyBoundaryCancellation (toReferenceInput input) (toReferenceOutcome outcome) := by
  have hBoundary := (cancellation_boundary_refines input).symm
  change Eip803x.PrecompileFrame.StageB.Reference.cancellationBoundary
      (toReferenceInput input) =
    Eip803x.PrecompileFrame.StageB.Generated.cancellationBoundary input at hBoundary
  cases hStatus : outcome.status <;>
    cases hCanBoundary : Eip803x.PrecompileFrame.StageB.Generated.cancellationBoundary input <;>
      cases hCancelled : input.cancelledAtBoundary <;>
        simp [G.applyBoundaryCancellation, R.applyBoundaryCancellation,
          Eip803x.PrecompileFrame.StageB.Generated.applyBoundaryCancellation,
          Eip803x.PrecompileFrame.StageB.Reference.applyBoundaryCancellation,
          Eip803x.PrecompileFrame.StageB.Generated.canReachBoundary,
          Eip803x.PrecompileFrame.StageB.Reference.canReachBoundary,
          hStatus, hCanBoundary, hCancelled, hBoundary,
          toReferenceOutcome, toReferenceGas,
          toReferenceStatus, toReferenceResult, toReferenceCompletion,
          toReferenceEffect]

private theorem after_dispatch_refines (oracle : G.LeafOracle) (input : G.Input) :
    toReferenceOutcome
        (G.applyBoundaryCancellation input (G.executeBody oracle input)) =
      R.applyBoundaryCancellation (toReferenceInput input)
        (R.executeBody (toReferenceOracle oracle) (toReferenceInput input)) := by
  calc
    toReferenceOutcome
        (G.applyBoundaryCancellation input (G.executeBody oracle input)) =
      R.applyBoundaryCancellation (toReferenceInput input)
        (toReferenceOutcome (G.executeBody oracle input)) :=
          apply_boundary_refines input (G.executeBody oracle input)
    _ = R.applyBoundaryCancellation (toReferenceInput input)
        (R.executeBody (toReferenceOracle oracle) (toReferenceInput input)) := by
          rw [execute_body_refines]

/--
Universal Stage B operational refinement. The theorem quantifies over the complete
typed source input and over the precompile leaf oracle; it does not prove the oracle.
-/
theorem source_execute_precompile_refines_reference
    (oracle : G.LeafOracle) (input : G.Input) :
    toReferenceOutcome (G.executePrecompile oracle input) =
      R.executePrecompile (toReferenceOracle oracle) (toReferenceInput input) := by
  cases hCancelable : input.cancelable <;>
    cases hCancelled : input.cancelledBeforeDispatch
  all_goals
    simp only [G.executePrecompile, R.executePrecompile,
      Eip803x.PrecompileFrame.StageB.Generated.executePrecompile,
      Eip803x.PrecompileFrame.StageB.Reference.executePrecompile,
      Eip803x.PrecompileFrame.StageB.Reference.decideDispatch,
      Eip803x.PrecompileFrame.StageB.Reference.renderDispatch,
      toReferenceInput, hCancelable, hCancelled, Bool.false_and,
      Bool.true_and, Bool.false_eq_true,
      if_false, if_true]
  · simpa only [toReferenceInput, hCancelable, hCancelled] using
      after_dispatch_refines oracle input
  · simpa only [toReferenceInput, hCancelable, hCancelled] using
      after_dispatch_refines oracle input
  · simpa only [toReferenceInput, hCancelable, hCancelled] using
      after_dispatch_refines oracle input
  · simp [toReferenceOutcome, toReferenceGas, toReferenceStatus,
      toReferenceResult, toReferenceCompletion, toReferenceEffect]

end Eip803x.PrecompileFrame.StageB.Refinement
