-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import SimpleTransferCompletionExtractor.StageB.Semantics

namespace SimpleTransferCompletionExtractor.StageB.Runtime

/-- Evaluated copies and retained locations, excluding readonly expression temporaries. -/
structure EvaluatedParameter where
  parameter : Parameter
  value : Value
  alias : Option Location

def EvaluatedParameter.binding (argument : EvaluatedParameter) : Parameter × Operand × ArgumentMode :=
  (argument.parameter,
    (match argument.alias with | none => .immediate argument.value | some location => .location location),
    if argument.alias.isSome then .readOnlyLocation else .value)

def EvaluatedParameter.Valid (argument : EvaluatedParameter) : Prop :=
  argument.parameter.refKind = if argument.alias.isSome then .inRef else .none

def EvaluatedParameter.cell (argument : EvaluatedParameter) (id : CellId) (frame : FrameId) : Cell :=
  { id, frame, symbol := argument.parameter.symbol, provenance := argument.parameter.symbol,
    value := if argument.alias.isSome then parameterDefaultValue argument.parameter.typeName else argument.value,
    alias := argument.alias }

/-- Pure layout calculation for the actual generic binder, retaining its duplicate-symbol rule. -/
def evaluatedParameterLayout (before : Machine) (frame : RuntimeFrame) : List EvaluatedParameter → Machine
  | [] => { before with frames := frame :: before.frames }
  | argument :: rest =>
      evaluatedParameterLayout
        { before with
          cells := argument.cell before.nextCell frame.id :: before.cells,
          nextCell := before.nextCell+1 }
        { frame with
          cells := (argument.parameter.symbol, before.nextCell) ::
          frame.cells.filter (fun item => item.1 != argument.parameter.symbol) } rest

theorem evaluatedParameterLayout_exact (before : Machine) (frame : RuntimeFrame)
    (arguments : List EvaluatedParameter)
    (valid : ∀ argument ∈ arguments, argument.Valid)
    (fresh : ∀ old ∈ before.frames, old.id ≠ frame.id) :
    (arguments.map EvaluatedParameter.binding).foldlM (bindFrameParameter frame.id)
      { before with frames := frame :: before.frames } =
        .ok (evaluatedParameterLayout before frame arguments) := by
  induction arguments generalizing before frame with
  | nil => rfl
  | cons argument arguments induction =>
      simp only [List.map_cons, List.foldlM_cons]
      have first : bindFrameParameter frame.id {before with frames := frame :: before.frames} argument.binding =
          .ok {before with
            frames := {frame with
              cells := (argument.parameter.symbol, before.nextCell) ::
                frame.cells.filter (fun item => item.1 != argument.parameter.symbol)} :: before.frames,
            cells := argument.cell before.nextCell frame.id :: before.cells, nextCell := before.nextCell+1} := by
        cases aliasEq : argument.alias with
        | none =>
            simpa [EvaluatedParameter.binding, EvaluatedParameter.cell, aliasEq] using
              bindFrameParameter_copy_or_alias before frame argument.parameter argument.value none
                (by simpa [EvaluatedParameter.Valid, aliasEq] using valid argument (by simp)) fresh
        | some location =>
            simpa [EvaluatedParameter.binding, EvaluatedParameter.cell, aliasEq] using
              bindFrameParameter_copy_or_alias before frame argument.parameter argument.value (some location)
                (by simpa [EvaluatedParameter.Valid, aliasEq] using valid argument (by simp)) fresh
      rw [first]
      simp only [bind, Except.bind]
      exact induction
        {before with cells := argument.cell before.nextCell frame.id :: before.cells, nextCell := before.nextCell+1}
        {frame with cells := (argument.parameter.symbol, before.nextCell) ::
          frame.cells.filter (fun item => item.1 != argument.parameter.symbol)}
        (by intro arg member; exact valid arg (by simp [member])) fresh

theorem createFrame_evaluated_parameters_exact (before : Machine) (function : Function)
    (receiver : Operand) (arguments : List EvaluatedParameter)
    (valid : ∀ argument ∈ arguments, argument.Valid)
    (fresh : ∀ old ∈ before.frames, old.id ≠ before.nextFrame) :
    createFrame before function receiver (arguments.map EvaluatedParameter.binding) =
      .ok (evaluatedParameterLayout {before with nextFrame := before.nextFrame+1}
        {id := before.nextFrame, functionSymbol := function.signature.symbol, thisOperand := receiver,
         cells := [], captures := [], carried := .unit} arguments, before.nextFrame) := by
  unfold createFrame
  dsimp only
  rw [evaluatedParameterLayout_exact {before with nextFrame := before.nextFrame+1}
    {id := before.nextFrame, functionSymbol := function.signature.symbol, thisOperand := receiver,
     cells := [], captures := [], carried := .unit} arguments valid fresh]
  rfl

end SimpleTransferCompletionExtractor.StageB.Runtime
