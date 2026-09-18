-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Generated.MemoryControlOpcodeKernel
import MemoryControlOpcodeExtractor.Specification.MemoryControlExecution

/-!
Refinement of source-derived opcode metadata into the companion specification.
This proves exact identity of the admitted dispatch metadata. It does not turn
Roslyn syntax into C# semantics or discharge the stated stack/memory providers.
-/

namespace MemoryControlOpcodeExtractor.Refinement

namespace G
abbrev Opcode := Eip803x.Generated.MemoryControlOpcodeKernel.Opcode
abbrev GasClass := Eip803x.Generated.MemoryControlOpcodeKernel.GasClass
abbrev DynamicGas := Eip803x.Generated.MemoryControlOpcodeKernel.DynamicGas
abbrev MemoryAccess := Eip803x.Generated.MemoryControlOpcodeKernel.MemoryAccess
abbrev ExitKind := Eip803x.Generated.MemoryControlOpcodeKernel.ExitKind
abbrev Activation := Eip803x.Generated.MemoryControlOpcodeKernel.Activation
abbrev descriptor := Eip803x.Generated.MemoryControlOpcodeKernel.descriptor
abbrev allOpcodes := Eip803x.Generated.MemoryControlOpcodeKernel.allOpcodes
abbrev allDispatchTables := Eip803x.Generated.MemoryControlOpcodeKernel.allDispatchTables
abbrev specializations := Eip803x.Generated.MemoryControlOpcodeKernel.specializations
abbrev tracingFlag := Eip803x.Generated.MemoryControlOpcodeKernel.tracingFlag
abbrev cancelableFlag := Eip803x.Generated.MemoryControlOpcodeKernel.cancelableFlag
abbrev closedHandlerBody := Eip803x.Generated.MemoryControlOpcodeKernel.closedHandlerBody
abbrev expectedClosedRoot := Eip803x.Generated.MemoryControlOpcodeKernel.expectedClosedRoot
abbrev copyWordGas := Eip803x.Generated.MemoryControlOpcodeKernel.copyWordGas
abbrev memoryLinearGas := Eip803x.Generated.MemoryControlOpcodeKernel.memoryLinearGas
abbrev activeOnAmsterdam := Eip803x.Generated.MemoryControlOpcodeKernel.activeOnAmsterdam
abbrev forkLineage := Eip803x.Generated.MemoryControlOpcodeKernel.forkLineage
end G

namespace C
abbrev GasClass := MemoryControlOpcodeExtractor.Specification.GasClass
abbrev DynamicGas := MemoryControlOpcodeExtractor.Specification.DynamicGas
abbrev MemoryAccess := MemoryControlOpcodeExtractor.Specification.MemoryAccess
abbrev ExitKind := MemoryControlOpcodeExtractor.Specification.ExitKind
abbrev Activation := MemoryControlOpcodeExtractor.Specification.Activation
abbrev gasClass := MemoryControlOpcodeExtractor.Specification.gasClass
abbrev dynamicGas := MemoryControlOpcodeExtractor.Specification.dynamicGas
abbrev memoryAccess := MemoryControlOpcodeExtractor.Specification.memoryAccess
abbrev stackShape := MemoryControlOpcodeExtractor.Specification.stackShape
abbrev exitKind := MemoryControlOpcodeExtractor.Specification.exitKind
abbrev activation := MemoryControlOpcodeExtractor.Specification.activation
abbrev amsterdamSchedule := MemoryControlOpcodeExtractor.Specification.Schedule.amsterdam
abbrev fixedCost := MemoryControlOpcodeExtractor.Specification.fixedCost
end C

open Eip803x.Evm.MemoryStackControl

def toSpecOpcode : G.Opcode → Opcode
  | .stop => .stop
  | .calldataload => .calldataload
  | .calldatacopy => .calldatacopy
  | .codecopy => .codecopy
  | .returndatacopy => .returndatacopy
  | .pop => .pop
  | .mload => .mload
  | .mstore => .mstore
  | .mstore8 => .mstore8
  | .jump => .jump
  | .jumpi => .jumpi
  | .pc => .pc
  | .msize => .msize
  | .gas => .gas
  | .jumpdest => .jumpdest
  | .mcopy => .mcopy
  | .push0 => .push 0
  | .push1 => .push 1
  | .push2 => .push 2
  | .push3 => .push 3
  | .push4 => .push 4
  | .push5 => .push 5
  | .push6 => .push 6
  | .push7 => .push 7
  | .push8 => .push 8
  | .push9 => .push 9
  | .push10 => .push 10
  | .push11 => .push 11
  | .push12 => .push 12
  | .push13 => .push 13
  | .push14 => .push 14
  | .push15 => .push 15
  | .push16 => .push 16
  | .push17 => .push 17
  | .push18 => .push 18
  | .push19 => .push 19
  | .push20 => .push 20
  | .push21 => .push 21
  | .push22 => .push 22
  | .push23 => .push 23
  | .push24 => .push 24
  | .push25 => .push 25
  | .push26 => .push 26
  | .push27 => .push 27
  | .push28 => .push 28
  | .push29 => .push 29
  | .push30 => .push 30
  | .push31 => .push 31
  | .push32 => .push 32
  | .dup1 => .dup 1
  | .dup2 => .dup 2
  | .dup3 => .dup 3
  | .dup4 => .dup 4
  | .dup5 => .dup 5
  | .dup6 => .dup 6
  | .dup7 => .dup 7
  | .dup8 => .dup 8
  | .dup9 => .dup 9
  | .dup10 => .dup 10
  | .dup11 => .dup 11
  | .dup12 => .dup 12
  | .dup13 => .dup 13
  | .dup14 => .dup 14
  | .dup15 => .dup 15
  | .dup16 => .dup 16
  | .swap1 => .swap 1
  | .swap2 => .swap 2
  | .swap3 => .swap 3
  | .swap4 => .swap 4
  | .swap5 => .swap 5
  | .swap6 => .swap 6
  | .swap7 => .swap 7
  | .swap8 => .swap 8
  | .swap9 => .swap 9
  | .swap10 => .swap 10
  | .swap11 => .swap 11
  | .swap12 => .swap 12
  | .swap13 => .swap 13
  | .swap14 => .swap 14
  | .swap15 => .swap 15
  | .swap16 => .swap 16
  | .return => .return
  | .revert => .revert
  | .invalid => .invalid

def gasClassToSpec : G.GasClass → C.GasClass
  | .zero => .zero
  | .base => .base
  | .veryLow => .veryLow
  | .mid => .mid
  | .high => .high
  | .jumpdest => .jumpdest

def dynamicGasToSpec : G.DynamicGas → C.DynamicGas
  | .none => .none
  | .copyWords => .copyWords

def memoryAccessToSpec : G.MemoryAccess → C.MemoryAccess
  | .none => .none
  | .word32 => .word32
  | .byte1 => .byte1
  | .copyDestination => .copyDestination
  | .memoryCopy => .memoryCopy
  | .returnRange => .returnRange

def exitKindToSpec : G.ExitKind → C.ExitKind
  | .stop => .stop
  | .continue => .continues
  | .jump => .jump
  | .conditionalJump => .conditionalJump
  | .returnData => .returnData
  | .revertData => .revertData
  | .invalid => .invalid

def activationToSpec : G.Activation → C.Activation
  | .unconditional => .unconditional
  | .eip140 => .eip140
  | .eip211 => .eip211
  | .eip3855 => .eip3855
  | .eip5656 => .eip5656

def descriptorRefines (opcode : G.Opcode) : Bool :=
  let descriptor := G.descriptor opcode
  decide (
    decodeByte (byte descriptor.opcodeByte) = toSpecOpcode opcode ∧
    C.gasClass (toSpecOpcode opcode) = some (gasClassToSpec descriptor.gasClass) ∧
    C.fixedCost C.amsterdamSchedule (toSpecOpcode opcode) = some descriptor.fixedGas ∧
    C.dynamicGas (toSpecOpcode opcode) = some (dynamicGasToSpec descriptor.dynamicGas) ∧
    C.memoryAccess (toSpecOpcode opcode) = some (memoryAccessToSpec descriptor.memoryAccess) ∧
    C.stackShape (toSpecOpcode opcode) = some (descriptor.stackInputs, descriptor.stackGrowth) ∧
    C.exitKind (toSpecOpcode opcode) = some (exitKindToSpec descriptor.exitKind) ∧
    C.activation (toSpecOpcode opcode) = some (activationToSpec descriptor.activation))

def specializationRefines (specialization : Eip803x.Generated.MemoryControlOpcodeKernel.Specialization) : Bool :=
  specialization.tracingFlag == G.tracingFlag specialization.table &&
  specialization.cancelableFlag == G.cancelableFlag specialization.table &&
  specialization.closedRoot == G.expectedClosedRoot specialization.opcode specialization.table &&
  !(specialization.closedRoot.contains "TTracingInst") &&
  !(specialization.closedRoot.contains "TGasPolicy")

theorem opcode_count : G.allOpcodes.length = 84 := by native_decide

theorem specialization_count : G.specializations.length = 336 := by native_decide

theorem specialization_keys_are_exact :
    G.specializations.map (fun specialization => (specialization.opcode, specialization.table)) =
      G.allOpcodes.flatMap (fun opcode => G.allDispatchTables.map fun table => (opcode, table)) := by
  native_decide

theorem specialization_keys_are_unique :
    (G.specializations.map fun specialization => (specialization.opcode, specialization.table)).Nodup := by
  native_decide

theorem every_source_specialization_is_exact :
    G.specializations.all specializationRefines = true := by
  native_decide

theorem opcode_bytes_are_unique : (G.allOpcodes.map fun opcode => (G.descriptor opcode).opcodeByte).Nodup := by
  native_decide

theorem every_source_descriptor_refines_companion : G.allOpcodes.all descriptorRefines = true := by
  native_decide

theorem source_copy_word_gas_refines_companion :
    G.copyWordGas = C.amsterdamSchedule.copyWord := by
  rfl

theorem source_memory_linear_gas_refines_companion :
    G.memoryLinearGas = C.amsterdamSchedule.memory.linear := by
  rfl

theorem every_selected_byte_decodes_to_the_reference :
    G.allOpcodes.all (fun opcode => decide (
      decodeByte (byte (G.descriptor opcode).opcodeByte) = toSpecOpcode opcode)) = true := by
  native_decide

theorem every_selected_opcode_is_active_on_amsterdam :
    G.allOpcodes.all G.activeOnAmsterdam = true := by
  native_decide

theorem source_fork_lineage_is_exact :
    G.forkLineage =
      ["Olympic", "Frontier", "Homestead", "Dao", "TangerineWhistle", "SpuriousDragon",
       "Byzantium", "Constantinople", "ConstantinopleFix", "Istanbul", "MuirGlacier", "Berlin",
       "London", "ArrowGlacier", "GrayGlacier", "Paris", "Shanghai", "Cancun", "Prague", "Osaka",
       "BPO1", "BPO2", "Amsterdam"] := by
  rfl

end MemoryControlOpcodeExtractor.Refinement
