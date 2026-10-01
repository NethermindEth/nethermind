-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

namespace SimpleTransferCompletionExtractor.StageB.Control.Spec

structure TickResult where
  exhausted : Bool
  remainingFuel : Nat
  deriving DecidableEq, Repr

def tick : Nat → TickResult
  | 0 => ⟨true, 0⟩
  | fuel + 1 => ⟨false, fuel⟩

structure Edge where
  destination : Int
  returns : Bool

inductive Decision where
  | jump (destination : Int)
  | returnValue
  | invalidCondition
  | missingEdge
  | invalidEdge
  deriving DecidableEq, Repr

def finish : Option Edge → Decision
  | none => .missingEdge
  | some edge =>
      if edge.destination < 0 then
        if edge.returns then .returnValue else .invalidEdge
      else .jump edge.destination

/-- A condition requires a boolean even if its conditional successor is absent.
Unknown condition labels preserve the candidate interpreter's fallback behavior. -/
def selectEdge (condition : String) (value : Option Bool)
    (fallThrough conditional : Option Edge) : Decision :=
  if condition == "None" then finish fallThrough
  else match value with
    | none => .invalidCondition
    | some boolean =>
        if (condition == "WhenTrue" && boolean) || (condition == "WhenFalse" && !boolean) then
          finish (conditional.orElse fun _ => fallThrough)
        else finish fallThrough

def jumpTag : Int := 0
def returnTag : Int := 1
def invalidConditionTag : Int := 2
def missingEdgeTag : Int := 3
def invalidEdgeTag : Int := 4
def missingSuspensionTag : Int := 5

def branchExit : Int := 0
def returnExit : Int := 1
def suspendExit : Int := 2
def outsideExit : Int := 3

structure BlockCursor (α : Type) where
  ordinal : Int
  carried : α
  deriving DecidableEq, Repr

structure FinishResult (α : Type) where
  kind : Int
  cursor : BlockCursor α
  deriving DecidableEq, Repr

def decisionTag : Decision → Int
  | .jump _ => jumpTag
  | .returnValue => returnTag
  | .invalidCondition => invalidConditionTag
  | .missingEdge => missingEdgeTag
  | .invalidEdge => invalidEdgeTag

def finishBlock (exit : Int) (cursor : BlockCursor α) (condition : String)
    (value : Option Bool) (fallThrough conditional : Option Edge) : FinishResult α :=
  if exit == returnExit then ⟨returnTag, cursor⟩
  else if exit == suspendExit then ⟨missingSuspensionTag, cursor⟩
  else
    match selectEdge condition value fallThrough conditional with
    | .jump destination => ⟨jumpTag, { cursor with ordinal := destination }⟩
    | decision => ⟨decisionTag decision, cursor⟩

theorem finishBlock_jump (exit : Int) (cursor : BlockCursor α) (condition : String)
    (value : Option Bool) (fallThrough conditional : Option Edge)
    (jumps : (finishBlock exit cursor condition value fallThrough conditional).kind = jumpTag) :
    ∃ destination,
      selectEdge condition value fallThrough conditional = .jump destination ∧
      (finishBlock exit cursor condition value fallThrough conditional).cursor =
        { cursor with ordinal := destination } := by
  by_cases returns : exit = returnExit
  · simp [finishBlock, returnExit, returns, jumpTag, returnTag] at jumps
  by_cases suspends : exit = suspendExit
  · simp [finishBlock, returnExit, suspendExit, suspends, jumpTag, missingSuspensionTag] at jumps
  have notReturn : ¬exit = 1 := by simpa [returnExit] using returns
  have notSuspend : ¬exit = 2 := by simpa [suspendExit] using suspends
  generalize selected : selectEdge condition value fallThrough conditional = decision
  cases decision with
  | jump destination =>
      exact ⟨destination, rfl,
        by simp [finishBlock, returnExit, suspendExit, notReturn, notSuspend, selected]⟩
  | returnValue => simp [finishBlock, returns, suspends, selected, jumpTag, decisionTag, returnTag] at jumps
  | invalidCondition =>
      simp [finishBlock, returns, suspends, selected, jumpTag, decisionTag, invalidConditionTag] at jumps
  | missingEdge => simp [finishBlock, returns, suspends, selected, jumpTag, decisionTag, missingEdgeTag] at jumps
  | invalidEdge => simp [finishBlock, returns, suspends, selected, jumpTag, decisionTag, invalidEdgeTag] at jumps

theorem finishBlock_return (exit : Int) (cursor : BlockCursor α) (condition : String)
    (value : Option Bool) (fallThrough conditional : Option Edge)
    (returns : (finishBlock exit cursor condition value fallThrough conditional).kind = returnTag) :
    (finishBlock exit cursor condition value fallThrough conditional).cursor = cursor ∧
      (exit = returnExit ∨
        (exit ≠ suspendExit ∧
          selectEdge condition value fallThrough conditional = .returnValue)) := by
  by_cases direct : exit = returnExit
  · subst exit
    simp [finishBlock]
  by_cases suspends : exit = suspendExit
  · subst exit
    simp [finishBlock, returnExit, suspendExit, returnTag, missingSuspensionTag] at returns
  have notReturn : ¬exit = 1 := by simpa [returnExit] using direct
  have notSuspend : ¬exit = 2 := by simpa [suspendExit] using suspends
  generalize selected : selectEdge condition value fallThrough conditional = decision
  cases decision with
  | jump destination =>
      simp [finishBlock, direct, suspends, selected, jumpTag, returnTag] at returns
  | returnValue =>
      simp [finishBlock, notReturn, notSuspend, selected, returnExit, suspendExit]
  | invalidCondition =>
      simp [finishBlock, direct, suspends, selected, decisionTag,
        invalidConditionTag, returnTag] at returns
  | missingEdge =>
      simp [finishBlock, direct, suspends, selected, decisionTag,
        missingEdgeTag, returnTag] at returns
  | invalidEdge =>
      simp [finishBlock, direct, suspends, selected, decisionTag,
        invalidEdgeTag, returnTag] at returns

theorem finishBlock_return_cursor (exit : Int) (cursor : BlockCursor α) (condition : String)
    (value : Option Bool) (fallThrough conditional : Option Edge)
    (returns : (finishBlock exit cursor condition value fallThrough conditional).kind = returnTag) :
    (finishBlock exit cursor condition value fallThrough conditional).cursor = cursor :=
  (finishBlock_return exit cursor condition value fallThrough conditional returns).1

inductive LocalReturnDecision where
  | returnedValue
  | receiverValue
  | receiverLocation
  deriving DecidableEq, Repr

def selectLocalReturn : Bool → Bool → LocalReturnDecision
  | false, _ => .returnedValue
  | true, true => .receiverValue
  | true, false => .receiverLocation

def localReturnTag : LocalReturnDecision → Int
  | .returnedValue => 0
  | .receiverValue => 1
  | .receiverLocation => 2

def shouldReadTransparent : Bool → Bool → Bool
  | true, _ => false
  | false, valueMode => valueMode

end SimpleTransferCompletionExtractor.StageB.Control.Spec
