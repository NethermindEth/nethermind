-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Production

namespace Eip803x

structure TransitionState where
  gasLeft : Nat
  stateReservoir : Int
  stateGasUsed : Int
  stateGasSpill : Int
  stateGasSpillRefunded : Int
  deriving DecidableEq, Repr

inductive Transition where
  | refund
  | repayStateGasSpill
  | restoreChildStateGas
  | restoreChildStateGasOnHalt
  | revertRefundToHalt
  | refundStateGas
  | discardStateGas
  | addStateGasRefundToReservoir
  | removeStateGasRefundFromReservoir
  deriving DecidableEq, Repr

inductive TransitionOutcome where
  | success
  | exception
  deriving DecidableEq, Repr

structure TransitionExpected where
  outcome : TransitionOutcome
  state : TransitionState
  unappliedAmount : Int
  error : Option String
  deriving DecidableEq, Repr

structure TransitionVector where
  name : String
  transition : Transition
  parent : TransitionState
  child : TransitionState
  amount : Int
  stateGasFloor : Int
  trackSpillRefund : Bool
  expected : TransitionExpected
  deriving DecidableEq, Repr

namespace TransitionVector

private def uint64Modulus : Nat := 18446744073709551616
private def uint64Max : Nat := 18446744073709551615
private def int64MaxNat : Nat := 9223372036854775807
private def int64Modulus : Int := 18446744073709551616
private def int64SignBit : Int := 9223372036854775808
private def int64Min : Int := -9223372036854775808
private def int64Max : Int := 9223372036854775807

private def state (gasLeft : Nat) (stateReservoir stateGasUsed stateGasSpill stateGasSpillRefunded : Int) : TransitionState :=
  { gasLeft, stateReservoir, stateGasUsed, stateGasSpill, stateGasSpillRefunded }

private def zeroState : TransitionState := state 0 0 0 0 0

private def normalizeUInt64 (value : Nat) : Nat := value % uint64Modulus

private def wrapUInt64 (value : Int) : Nat :=
  Int.toNat (value % int64Modulus)

private def wrapInt64 (value : Int) : Int :=
  let residue := value % int64Modulus
  if residue < int64SignBit then residue else residue - int64Modulus

private def addUInt64 (left right : Nat) : Nat :=
  wrapUInt64 ((left : Int) + (right : Int))

private def addInt64 (left right : Int) : Int := wrapInt64 (left + right)

private def subInt64 (left right : Int) : Int := wrapInt64 (left - right)

private def int64ToUInt64 (value : Int) : Nat := wrapUInt64 value

private def normalizeState (value : TransitionState) : TransitionState :=
  { gasLeft := normalizeUInt64 value.gasLeft
    stateReservoir := wrapInt64 value.stateReservoir
    stateGasUsed := wrapInt64 value.stateGasUsed
    stateGasSpill := wrapInt64 value.stateGasSpill
    stateGasSpillRefunded := wrapInt64 value.stateGasSpillRefunded }

private def positivePart (value : Int) : Int := if value > 0 then value else 0

private def unrefundedSpill (stateGasSpill stateGasSpillRefunded : Int) : Int :=
  positivePart (subInt64 stateGasSpill stateGasSpillRefunded)

private def success (state : TransitionState) (unappliedAmount : Int := 0) : TransitionExpected :=
  { outcome := .success, state, unappliedAmount, error := none }

private def exception (state : TransitionState) : TransitionExpected :=
  { outcome := .exception, state, unappliedAmount := 0, error := some "ArgumentException" }

private def clampToRefundAmount (stateReservoir amount : Int) : Int :=
  if stateReservoir <= 0 then 0
  else if stateReservoir >= amount then amount
  else stateReservoir

private def reference (vector : TransitionVector) : TransitionExpected :=
  let parent := normalizeState vector.parent
  let child := normalizeState vector.child
  let amount := wrapInt64 vector.amount
  let stateGasFloor := wrapInt64 vector.stateGasFloor
  match vector.transition with
  | .refund =>
      success
        { gasLeft := addUInt64 parent.gasLeft child.gasLeft
          stateReservoir := addInt64 parent.stateReservoir child.stateReservoir
          stateGasUsed := addInt64 parent.stateGasUsed child.stateGasUsed
          stateGasSpill := addInt64 parent.stateGasSpill child.stateGasSpill
          stateGasSpillRefunded := addInt64 parent.stateGasSpillRefunded child.stateGasSpillRefunded }
  | .repayStateGasSpill =>
      let repayment := min parent.stateReservoir
        (unrefundedSpill parent.stateGasSpill parent.stateGasSpillRefunded)
      if repayment <= 0 then
        success parent
      else
        success
          { gasLeft := addUInt64 parent.gasLeft (int64ToUInt64 repayment)
            stateReservoir := subInt64 parent.stateReservoir repayment
            stateGasUsed := parent.stateGasUsed
            stateGasSpill := parent.stateGasSpill
            stateGasSpillRefunded := addInt64 parent.stateGasSpillRefunded repayment }
  | .restoreChildStateGas =>
      let childNetSpill := unrefundedSpill child.stateGasSpill child.stateGasSpillRefunded
      success
        { gasLeft := addUInt64 parent.gasLeft (int64ToUInt64 childNetSpill)
          stateReservoir := subInt64 (addInt64 (addInt64 parent.stateReservoir child.stateReservoir)
            child.stateGasUsed) childNetSpill
          stateGasUsed := parent.stateGasUsed
          stateGasSpill := parent.stateGasSpill
          stateGasSpillRefunded := parent.stateGasSpillRefunded }
  | .restoreChildStateGasOnHalt =>
      let childNetSpill := unrefundedSpill child.stateGasSpill child.stateGasSpillRefunded
      success
        { gasLeft := parent.gasLeft
          stateReservoir := subInt64 (addInt64 (addInt64 parent.stateReservoir child.stateReservoir)
            child.stateGasUsed) childNetSpill
          stateGasUsed := parent.stateGasUsed
          stateGasSpill := parent.stateGasSpill
          stateGasSpillRefunded := parent.stateGasSpillRefunded }
  | .revertRefundToHalt =>
      let childNetSpill := unrefundedSpill child.stateGasSpill child.stateGasSpillRefunded
      success
        { gasLeft := parent.gasLeft
          stateReservoir := subInt64 (addInt64 parent.stateReservoir child.stateGasUsed) childNetSpill
          stateGasUsed := subInt64 parent.stateGasUsed child.stateGasUsed
          stateGasSpill := subInt64 parent.stateGasSpill child.stateGasSpill
          stateGasSpillRefunded := subInt64 parent.stateGasSpillRefunded child.stateGasSpillRefunded }
  | .refundStateGas =>
      let refundableStateGas := positivePart (subInt64 parent.stateGasUsed stateGasFloor)
      let appliedRefund := min amount refundableStateGas
      let toGasLeft := if vector.trackSpillRefund then
        min appliedRefund (unrefundedSpill parent.stateGasSpill parent.stateGasSpillRefunded)
      else
        0
      success
        { gasLeft := addUInt64 parent.gasLeft (int64ToUInt64 toGasLeft)
          stateReservoir := addInt64 parent.stateReservoir (subInt64 appliedRefund toGasLeft)
          stateGasUsed := subInt64 parent.stateGasUsed appliedRefund
          stateGasSpill := parent.stateGasSpill
          stateGasSpillRefunded := if vector.trackSpillRefund then
            addInt64 parent.stateGasSpillRefunded toGasLeft
          else
            parent.stateGasSpillRefunded }
  | .discardStateGas =>
      let discardableStateGas := positivePart (subInt64 parent.stateGasUsed stateGasFloor)
      let appliedRefund := min amount discardableStateGas
      success
        { gasLeft := parent.gasLeft
          stateReservoir := parent.stateReservoir
          stateGasUsed := subInt64 parent.stateGasUsed appliedRefund
          stateGasSpill := parent.stateGasSpill
          stateGasSpillRefunded := parent.stateGasSpillRefunded }
        (subInt64 amount appliedRefund)
  | .addStateGasRefundToReservoir =>
      let toGasLeft := if vector.trackSpillRefund then
        min amount (unrefundedSpill parent.stateGasSpill parent.stateGasSpillRefunded)
      else
        0
      success
        { gasLeft := addUInt64 parent.gasLeft (int64ToUInt64 toGasLeft)
          stateReservoir := addInt64 parent.stateReservoir (subInt64 amount toGasLeft)
          stateGasUsed := parent.stateGasUsed
          stateGasSpill := parent.stateGasSpill
          stateGasSpillRefunded := if vector.trackSpillRefund then
            addInt64 parent.stateGasSpillRefunded toGasLeft
          else
            parent.stateGasSpillRefunded }
  | .removeStateGasRefundFromReservoir =>
      if vector.amount < 0 then
        exception parent
      else
        let fromReservoir := clampToRefundAmount parent.stateReservoir amount
        let remainingAmount := subInt64 amount fromReservoir
        let nextStateReservoir := subInt64 parent.stateReservoir fromReservoir
        if remainingAmount <= 0 then
          success
            { gasLeft := parent.gasLeft
              stateReservoir := nextStateReservoir
              stateGasUsed := parent.stateGasUsed
              stateGasSpill := parent.stateGasSpill
              stateGasSpillRefunded := parent.stateGasSpillRefunded }
        else
          let fromUsed := min remainingAmount parent.stateGasUsed
          success
            { gasLeft := parent.gasLeft
              stateReservoir := subInt64 nextStateReservoir (subInt64 remainingAmount fromUsed)
              stateGasUsed := subInt64 parent.stateGasUsed fromUsed
              stateGasSpill := parent.stateGasSpill
              stateGasSpillRefunded := parent.stateGasSpillRefunded }

private def transitionName : Transition → String
  | .refund => "refund"
  | .repayStateGasSpill => "repayStateGasSpill"
  | .restoreChildStateGas => "restoreChildStateGas"
  | .restoreChildStateGasOnHalt => "restoreChildStateGasOnHalt"
  | .revertRefundToHalt => "revertRefundToHalt"
  | .refundStateGas => "refundStateGas"
  | .discardStateGas => "discardStateGas"
  | .addStateGasRefundToReservoir => "addStateGasRefundToReservoir"
  | .removeStateGasRefundFromReservoir => "removeStateGasRefundFromReservoir"

private def outcomeName : TransitionOutcome → String
  | .success => "success"
  | .exception => "exception"

private def decimalNat (value : Nat) : String := s!"{value}"
private def decimalInt (value : Int) : String := s!"{value}"
private def boolName : Bool → String
  | true => "true"
  | false => "false"

private def renderStateFields (state : TransitionState) : String :=
  "\"gasLeft\":\"" ++ decimalNat state.gasLeft ++
    "\",\"stateReservoir\":\"" ++ decimalInt state.stateReservoir ++
    "\",\"stateGasUsed\":\"" ++ decimalInt state.stateGasUsed ++
    "\",\"stateGasSpill\":\"" ++ decimalInt state.stateGasSpill ++
    "\",\"stateGasSpillRefunded\":\"" ++ decimalInt state.stateGasSpillRefunded ++ "\""

private def renderChildFields (state : TransitionState) : String :=
  "\"childGasLeft\":\"" ++ decimalNat state.gasLeft ++
    "\",\"childStateReservoir\":\"" ++ decimalInt state.stateReservoir ++
    "\",\"childStateGasUsed\":\"" ++ decimalInt state.stateGasUsed ++
    "\",\"childStateGasSpill\":\"" ++ decimalInt state.stateGasSpill ++
    "\",\"childStateGasSpillRefunded\":\"" ++ decimalInt state.stateGasSpillRefunded ++ "\""

private def renderChildStateFields (state : TransitionState) : String :=
  "\"childStateReservoir\":\"" ++ decimalInt state.stateReservoir ++
    "\",\"childStateGasUsed\":\"" ++ decimalInt state.stateGasUsed ++
    "\",\"childStateGasSpill\":\"" ++ decimalInt state.stateGasSpill ++
    "\",\"childStateGasSpillRefunded\":\"" ++ decimalInt state.stateGasSpillRefunded ++ "\""

private def renderExpected (vector : TransitionVector) : String :=
  let expected := vector.expected
  let error := match expected.error with
    | none => ""
    | some value => ",\"error\":\"" ++ value ++ "\""
  "{\"schemaVersion\":\"1\",\"operation\":\"state-transition\",\"transition\":\"" ++
    transitionName vector.transition ++ "\",\"outcome\":\"" ++ outcomeName expected.outcome ++
    "\",\"state\":{" ++ renderStateFields expected.state ++
    ",\"unappliedAmount\":\"" ++ decimalInt expected.unappliedAmount ++ "\"}" ++ error ++ "}"

def renderRequest (vector : TransitionVector) : String :=
  let operationFields := match vector.transition with
    | .refund => "," ++ renderChildFields vector.child
    | .restoreChildStateGas => "," ++ renderChildStateFields vector.child
    | .restoreChildStateGasOnHalt => "," ++ renderChildStateFields vector.child
    | .revertRefundToHalt => "," ++ renderChildStateFields vector.child
    | .refundStateGas => ",\"amount\":\"" ++ decimalInt vector.amount ++
        "\",\"stateGasFloor\":\"" ++ decimalInt vector.stateGasFloor ++
        "\",\"trackSpillRefund\":" ++ boolName vector.trackSpillRefund
    | .discardStateGas => ",\"amount\":\"" ++ decimalInt vector.amount ++
        "\",\"stateGasFloor\":\"" ++ decimalInt vector.stateGasFloor ++ "\""
    | .addStateGasRefundToReservoir => ",\"amount\":\"" ++ decimalInt vector.amount ++
        "\",\"trackSpillRefund\":" ++ boolName vector.trackSpillRefund
    | .removeStateGasRefundFromReservoir => ",\"amount\":\"" ++ decimalInt vector.amount ++ "\""
    | .repayStateGasSpill => ""
  "{\"schemaVersion\":\"1\",\"operation\":\"state-transition\",\"input\":{" ++
    "\"transition\":\"" ++ transitionName vector.transition ++ "\"," ++
    renderStateFields vector.parent ++ operationFields ++ "}}"

private def expectedSuccess (state : TransitionState) (unappliedAmount : Int := 0) : TransitionExpected :=
  success state unappliedAmount

private def make
    (name : String)
    (transition : Transition)
    (parent child : TransitionState)
    (amount stateGasFloor : Int)
    (trackSpillRefund : Bool)
    (expected : TransitionExpected) : TransitionVector :=
  { name, transition, parent, child, amount, stateGasFloor, trackSpillRefund, expected }

private def all : List TransitionVector :=
  [ make "refund-success" .refund
      (state 10 20 30 40 50) (state 15 25 35 45 55) 0 0 false
      (expectedSuccess (state 25 45 65 85 105))
  , make "refund-machine-wrap" .refund
      (state uint64Max int64Max int64Max int64Max int64Max)
      (state 1 1 1 1 1) 0 0 false
      (expectedSuccess (state 0 int64Min int64Min int64Min int64Min))
  , make "repay-spill-priority" .repayStateGasSpill
      (state 100 8 9 10 3) zeroState 0 0 false
      (expectedSuccess (state 107 1 9 10 10))
  , make "repay-no-credit" .repayStateGasSpill
      (state 9 (-2) 7 10 12) zeroState 0 0 false
      (expectedSuccess (state 9 (-2) 7 10 12))
  , make "repay-machine-wrap" .repayStateGasSpill
      (state 0 int64Max 7 int64Min 1) zeroState 0 0 false
      (expectedSuccess (state int64MaxNat 0 7 int64Min int64Min))
  , make "restore-revert-spill-priority" .restoreChildStateGas
      (state 100 2 3 4 5) (state 30 7 11 17 6) 0 0 false
      (expectedSuccess (state 111 9 3 4 5))
  , make "restore-revert-machine-wrap" .restoreChildStateGas
      (state uint64Max int64Max 6 7 8) (state 0 1 1 1 0) 0 0 false
      (expectedSuccess (state 0 int64Min 6 7 8))
  , make "restore-exception-burns-spill" .restoreChildStateGasOnHalt
      (state 100 2 3 4 5) (state 30 7 11 17 6) 0 0 false
      (expectedSuccess (state 100 9 3 4 5))
  , make "restore-exception-machine-wrap" .restoreChildStateGasOnHalt
      (state uint64Max int64Max 6 7 8) (state 0 1 1 1 0) 0 0 false
      (expectedSuccess (state uint64Max int64Min 6 7 8))
  , make "revert-refund-to-halt" .revertRefundToHalt
      (state 100 20 30 40 50) (state 70 5 7 11 3) 0 0 false
      (expectedSuccess (state 100 19 23 29 47))
  , make "revert-refund-to-halt-machine-wrap" .revertRefundToHalt
      (state 5 int64Min int64Min int64Min int64Min) (state 0 0 int64Max 1 0) 0 0 false
      (expectedSuccess (state 5 (-2) 1 int64Max int64Min))
  , make "refund-state-spill-priority" .refundStateGas
      (state 100 0 200 100 20) zeroState 150 75 true
      (expectedSuccess (state 180 45 75 100 100))
  , make "refund-state-reservoir-only" .refundStateGas
      (state 100 0 200 100 20) zeroState 150 75 false
      (expectedSuccess (state 100 125 75 100 20))
  , make "refund-state-floor-cap" .refundStateGas
      (state 100 4 5 6 2) zeroState 100 5 true
      (expectedSuccess (state 100 4 5 6 2))
  , make "refund-state-maximum-amount" .refundStateGas
      (state 0 0 int64Max 0 0) zeroState int64Max 0 false
      (expectedSuccess (state 0 int64Max 0 0 0))
  , make "refund-state-signed-wrap" .refundStateGas
      (state uint64Max int64Max int64Min 1 0) zeroState 1 int64Max false
      (expectedSuccess (state uint64Max int64Min int64Max 1 0))
  , make "refund-state-negative-amount" .refundStateGas
      (state 100 5 80 20 8) zeroState (-1) 0 true
      (expectedSuccess (state 99 5 81 20 7))
  , make "discard-state-floor-cap" .discardStateGas
      (state 100 9 80 30 4) zeroState 50 40 false
      (expectedSuccess (state 100 9 40 30 4) 10)
  , make "discard-state-negative-amount" .discardStateGas
      (state 100 5 80 20 8) zeroState (-1) 0 false
      (expectedSuccess (state 100 5 81 20 8))
  , make "discard-state-negative-floor" .discardStateGas
      (state 100 9 5 0 0) zeroState 2 (-1) false
      (expectedSuccess (state 100 9 3 0 0))
  , make "add-refund-spill-priority" .addStateGasRefundToReservoir
      (state 100 5 80 20 8) zeroState 15 0 true
      (expectedSuccess (state 112 8 80 20 20))
  , make "add-refund-reservoir-only" .addStateGasRefundToReservoir
      (state 100 5 80 20 8) zeroState 15 0 false
      (expectedSuccess (state 100 20 80 20 8))
  , make "add-refund-negative-amount" .addStateGasRefundToReservoir
      (state 100 5 80 20 8) zeroState (-1) 0 true
      (expectedSuccess (state 99 5 80 20 7))
  , make "add-refund-machine-wrap" .addStateGasRefundToReservoir
      (state 0 0 0 int64Min 1) zeroState int64Max 0 true
      (expectedSuccess (state int64MaxNat 0 0 int64Min int64Min))
  , make "remove-refund-reservoir-before-used" .removeStateGasRefundFromReservoir
      (state 100 50 70 20 8) zeroState 60 0 false
      (expectedSuccess (state 100 0 60 20 8))
  , make "remove-refund-restores-debt" .removeStateGasRefundFromReservoir
      (state 100 (-10) 5 20 8) zeroState 20 0 false
      (expectedSuccess (state 100 (-25) 0 20 8))
  , make "remove-refund-machine-wrap" .removeStateGasRefundFromReservoir
      (state 100 int64Min int64Min 20 8) zeroState int64Max 0 false
      (expectedSuccess (state 100 (int64Min + 1) 0 20 8))
  , make "remove-refund-negative-amount-exception" .removeStateGasRefundFromReservoir
      (state 100 1 2 3 4) zeroState (-1) 0 false
      (exception (state 100 1 2 3 4))
  ]

def passes (vector : TransitionVector) : Bool :=
  reference vector == vector.expected

theorem all_pass : all.all passes = true := by
  decide

theorem row_count : all.length = 28 := by
  rfl

private def allTransitions : List Transition :=
  [ .refund
  , .repayStateGasSpill
  , .restoreChildStateGas
  , .restoreChildStateGasOnHalt
  , .revertRefundToHalt
  , .refundStateGas
  , .discardStateGas
  , .addStateGasRefundToReservoir
  , .removeStateGasRefundFromReservoir ]

private def covers (transition : Transition) : Bool :=
  all.any (fun vector => vector.transition == transition)

theorem every_transition_has_vector : allTransitions.all covers = true := by
  decide

def responses : List String := all.map renderExpected

def requests : List String := all.map renderRequest

end TransitionVector
end Eip803x
