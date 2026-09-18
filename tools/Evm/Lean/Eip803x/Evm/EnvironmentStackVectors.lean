-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Evm.EnvironmentStack
import Eip803x.Evm.EnvironmentVectors

namespace Eip803x
namespace Evm
namespace EnvironmentStack
namespace BoundaryVector

open Word
open MemoryStackControl

def w (value : Nat) : UInt256 := Word.ofNat value

def stack (words : List UInt256) : Stack := Stack.fromWords words

def observe (outcome : Outcome) : Status × List UInt256 :=
  (outcome.status, outcome.stack.words)

def baseContext : Environment.ValidContext := Environment.BoundaryVector.context

def noBlobFeeContext : Environment.ValidContext :=
  ⟨{ Environment.BoundaryVector.rawContext with excessBlobGas := none }, by native_decide⟩

def noSlotContext : Environment.ValidContext :=
  ⟨{ Environment.BoundaryVector.rawContext with slotNumber := none }, by native_decide⟩

theorem stack_effect_vectors :
    observe (execute baseContext .address Stack.empty) = (.ok, [w 0x11]) ∧
      observe (execute baseContext .blockhash (stack [w 999, w 7])) =
        (.ok, [w 0x999, w 7]) ∧
      observe (execute baseContext .blobhash (stack [w 2, w 7])) =
        (.ok, [Word.zero, w 7]) ∧
      observe (execute baseContext .blockhash Stack.empty) =
        (.stackUnderflow, []) ∧
      observe (execute baseContext .blobhash Stack.empty) =
        (.stackUnderflow, []) ∧
      observe (execute baseContext .address Stack.full) =
        (.stackOverflow, Stack.full.words) ∧
      observe (execute noBlobFeeContext .blobbasefee (stack [w 7])) =
        (.badInstruction, [w 7]) ∧
      observe (execute noSlotContext .slotnum (stack [w 8])) =
        (.badInstruction, [w 8]) := by
  native_decide

def successfulInput (opcode : Environment.Opcode) : Stack :=
  if Environment.requiresArgument opcode then stack [Word.zero] else Stack.empty

def hasSuccessfulStackPath (opcode : Environment.Opcode) : Bool :=
  decide ((execute baseContext opcode (successfulInput opcode)).status = .ok)

theorem every_context_opcode_has_a_successful_stack_path :
    Environment.allOpcodes.all hasSuccessfulStackPath = true := by
  native_decide

def restoringIndexedArgument (context : Environment.ValidContext)
    (opcode : Environment.Opcode) (input : Stack) : Outcome :=
  match input.pop with
  | none => { status := .stackUnderflow, stack := input }
  | some (_, tail) =>
    match Environment.execute context opcode with
    | .word value => pushWord tail value
    | .stackUnderflow => { status := .stackUnderflow, stack := input }
    | .badInstruction => { status := .badInstruction, stack := input }

theorem indexed_argument_mutation_checks :
    observe (restoringIndexedArgument baseContext .blockhash (stack [w 999, w 7])) ≠
        (.ok, [w 0x999, w 7]) ∧
      observe (restoringIndexedArgument baseContext .blobhash (stack [w 2, w 7])) ≠
        (.ok, [Word.zero, w 7]) := by
  native_decide

end BoundaryVector
end EnvironmentStack
end Evm
end Eip803x
