-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import SimpleTransferCompletionExtractor.StageB.Control.Generated.InterpreterControl
import SimpleTransferCompletionExtractor.StageB.Control.Specification

namespace SimpleTransferCompletionExtractor.StageB.Control

def encodeTick (result : Spec.TickResult) : Generated.TickResult :=
  ⟨result.exhausted, result.remainingFuel⟩

theorem extracted_tick_correct (fuel : Nat) (bounded : fuel ≤ 9223372036854775807) :
    Generated.Tick fuel = encodeTick (Spec.tick fuel) := by
  cases fuel with
  | zero => rfl
  | succ fuel =>
      have lower : 0 ≤ (fuel : Int) + 9223372036854775808 := by omega
      have upper : (fuel : Int) + 9223372036854775808 < 18446744073709551616 := by omega
      simp [Generated.Tick, Generated.wrap64, encodeTick, Spec.tick,
        Int.emod_eq_of_lt lower upper]
      omega

def edge (present : Bool) (destination : Int) (returns : Bool) : Option Spec.Edge :=
  if present then some ⟨destination, returns⟩ else none

def encodeDecision : Spec.Decision → Generated.EdgeResult
  | .jump destination => ⟨0, destination⟩
  | .returnValue => ⟨1, -1⟩
  | .invalidCondition => ⟨2, -1⟩
  | .missingEdge => ⟨3, -1⟩
  | .invalidEdge => ⟨4, -1⟩

theorem extracted_selectEdge_correct (condition : String) (isBoolean boolean : Bool)
    (hasFallThrough : Bool) (fallThroughDestination : Int) (fallThroughReturns : Bool)
    (hasConditional : Bool) (conditionalDestination : Int) (conditionalReturns : Bool) :
    Generated.SelectEdge condition isBoolean boolean hasFallThrough fallThroughDestination
      fallThroughReturns hasConditional conditionalDestination conditionalReturns =
    encodeDecision (Spec.selectEdge condition (if isBoolean then some boolean else none)
      (edge hasFallThrough fallThroughDestination fallThroughReturns)
      (edge hasConditional conditionalDestination conditionalReturns)) := by
  cases isBoolean <;> cases boolean <;> cases hasFallThrough <;> cases fallThroughReturns <;>
    cases hasConditional <;> cases conditionalReturns <;>
    by_cases noneCondition : condition = "None" <;>
    by_cases trueCondition : condition = "WhenTrue" <;>
    by_cases falseCondition : condition = "WhenFalse" <;>
    simp_all [Generated.SelectEdge, Spec.selectEdge, Spec.finish, edge, encodeDecision]
  all_goals split <;> rfl

def encodeCursor (cursor : Spec.BlockCursor α) : Generated.BlockCursor α :=
  ⟨cursor.ordinal, cursor.carried⟩

def encodeFinishResult (result : Spec.FinishResult α) : Generated.FinishResult α :=
  ⟨result.kind, encodeCursor result.cursor⟩

theorem extracted_finishBlock_correct (exit : Int) (cursor : Spec.BlockCursor α)
    (condition : String) (isBoolean boolean : Bool)
    (hasFallThrough : Bool) (fallThroughDestination : Int) (fallThroughReturns : Bool)
    (hasConditional : Bool) (conditionalDestination : Int) (conditionalReturns : Bool) :
    Generated.FinishBlock exit (encodeCursor cursor) condition isBoolean boolean
      hasFallThrough fallThroughDestination fallThroughReturns
      hasConditional conditionalDestination conditionalReturns =
    encodeFinishResult (Spec.finishBlock exit cursor condition
      (if isBoolean then some boolean else none)
      (edge hasFallThrough fallThroughDestination fallThroughReturns)
      (edge hasConditional conditionalDestination conditionalReturns)) := by
  by_cases returns : exit = 1
  · simp [Generated.FinishBlock, Spec.finishBlock, Spec.returnExit, returns,
      encodeFinishResult, encodeCursor, Spec.returnTag]
  by_cases suspends : exit = 2
  · simp [Generated.FinishBlock, Spec.finishBlock, Spec.returnExit, Spec.suspendExit,
      suspends, encodeFinishResult, encodeCursor, Spec.missingSuspensionTag]
  · unfold Generated.FinishBlock Spec.finishBlock
    simp only [Spec.returnExit, Spec.suspendExit]
    rw [extracted_selectEdge_correct]
    generalize decisionEquation : Spec.selectEdge condition (if isBoolean then some boolean else none)
      (edge hasFallThrough fallThroughDestination fallThroughReturns)
      (edge hasConditional conditionalDestination conditionalReturns) = decision
    cases decision <;>
      simp [encodeDecision, encodeFinishResult, encodeCursor, Spec.decisionTag,
        Spec.jumpTag, Spec.returnTag, Spec.invalidConditionTag, Spec.missingEdgeTag,
        Spec.invalidEdgeTag, returns, suspends]

theorem extracted_selectLocalReturn_correct (constructing valueMode : Bool) :
    Generated.SelectLocalReturn constructing valueMode =
      Spec.localReturnTag (Spec.selectLocalReturn constructing valueMode) := by
  cases constructing <;> cases valueMode <;>
    rfl

theorem extracted_shouldReadTransparent_correct (preservesOperand valueMode : Bool) :
    Generated.ShouldReadTransparent preservesOperand valueMode =
      Spec.shouldReadTransparent preservesOperand valueMode := by
  cases preservesOperand <;> cases valueMode <;> rfl

end SimpleTransferCompletionExtractor.StageB.Control
