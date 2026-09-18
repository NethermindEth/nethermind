-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Generated.AccountReadOpcodeKernel
import Eip803x.Evm.AccountReadStack

namespace Eip803x.Refinement.AccountReadOpcode

namespace G
export Eip803x.Generated.AccountReadOpcodeKernel
  (Schedule AccountView Provider Opcode Status State Outcome Specialization DispatchTable allOpcodes
   allDispatchTables specializations opcodeByte instruction handlerRoute valueRule effectOrder stackInputs stackOutputs
   popsBeforeGas chargesSecondRead tracingFlag cancelableFlag closedHandlerRoute expectedClosedRoot
   sourceInterface sourceImplementation sourceClosedVm sourceGasPolicy sourceWorldState sourceCodeRepository
   sourcePrecompileProvider sourceCodeCache sourceFork sourceDispatchRoot sourceBuildSelection
   amsterdamSchedule addressOfWord accessIsCold accessCharge execute)
end G

namespace R
export Eip803x.Evm.AccountReadStack
  (Schedule AccountView Provider Opcode Status State Outcome execute)
end R

open Eip803x
open Eip803x.Evm

def toReferenceSchedule (schedule : G.Schedule) : R.Schedule :=
  { accountReadBase := schedule.accountReadBase
    coldAccountAccess := schedule.coldAccountAccess
    warmAccess := schedule.warmAccess
    copyWord := schedule.copyWord
    memory := schedule.memory
    veryLow := schedule.veryLow }

def toReferenceView (view : G.AccountView) : R.AccountView :=
  view

def toReferenceProvider (provider : G.Provider) : R.Provider :=
  provider

def toReferenceOpcode : G.Opcode → R.Opcode
  | .balance => .balance
  | .extCodeSize => .extCodeSize
  | .extCodeCopy => .extCodeCopy
  | .extCodeHash => .extCodeHash

def toReferenceStatus (status : G.Status) : R.Status :=
  status

def toReferenceState (state : G.State) : R.State :=
  state

def toReferenceOutcome (outcome : G.Outcome) : R.Outcome :=
  outcome

def specializationRefines (specialization : G.Specialization) : Bool :=
  specialization.tracingFlag == G.tracingFlag specialization.table &&
  specialization.cancelableFlag == G.cancelableFlag specialization.table &&
  specialization.closedRoot == G.expectedClosedRoot specialization.opcode specialization.table &&
  !(specialization.closedRoot.contains "TTracingInst") &&
  !(specialization.closedRoot.contains "TGasPolicy") &&
  !(specialization.closedRoot.contains "Eip2929") &&
  !(specialization.closedRoot.contains "Eip8038>")

theorem opcode_bytes_are_exact :
    G.allOpcodes.map G.opcodeByte = [0x31, 0x3b, 0x3c, 0x3f] := by
  native_decide

theorem standard_mainnet_services_are_exact :
    G.sourceInterface = "IVirtualMachine" ∧
    G.sourceImplementation = "EthereumVirtualMachine" ∧
    G.sourceClosedVm = "VirtualMachine<EthereumGasPolicy>" ∧
    G.sourceGasPolicy = "EthereumGasPolicy" ∧
    G.sourceWorldState = "WorldState" ∧
    G.sourceCodeRepository = "CacheCodeInfoRepository" ∧
    G.sourcePrecompileProvider = "EthereumPrecompileProvider" ∧
    G.sourceCodeCache = "StaticCodeCache.Instance" ∧
    G.sourceFork = "Amsterdam" := by
  native_decide

theorem opcode_bytes_are_unique :
    (G.allOpcodes.map G.opcodeByte).Nodup := by
  native_decide

theorem descriptor_stack_effects_are_exact :
    G.allOpcodes.map (fun opcode => (G.stackInputs opcode, G.stackOutputs opcode)) =
      [(1, 1), (1, 1), (4, 0), (1, 1)] := by
  native_decide

theorem eip8038_second_reads_are_exact :
    G.allOpcodes.map G.chargesSecondRead = [false, true, true, false] := by
  native_decide

theorem precharge_pop_order_is_exact :
    G.allOpcodes.map G.popsBeforeGas = [false, false, true, false] := by
  native_decide

theorem effect_order_metadata_is_exact :
    G.allOpcodes.map G.effectOrder =
      [ "enter>baseGas>popAddress>warm>accessGas>getBalance>push"
      , "enter>baseGas>popAddress>warm>accessGas>secondWarmGas>accountRead>fusionOrCodeNoDelegation>push"
      , "enter>popAddress>popDestinationSourceLength>copyGas>warm>accessGas>secondWarmGas>zeroLengthRecordOrMemoryGas>accountRead>codeNoDelegation>zeroExtendedCopy"
      , "enter>baseGas>popAddress>warm>accessGas>isDead>getCodeHashIfLive>push" ] := by
  native_decide

theorem handler_routes_are_exact :
    G.allOpcodes.map G.handlerRoute =
      [ "BalanceOpcode<TTracingInst,EvmInstructions.AccessSpec<OnFlag,Eip8038On>>"
      , "ExtCodeSizeOpcode<TTracingInst,Eip8038On,OnFlag>"
      , "ExtCodeCopyOpcode<TTracingInst,Eip8038On,OnFlag>"
      , "ExtCodeHashOpcode<TTracingInst,EvmInstructions.AccessSpec<OnFlag,Eip8038On>>" ] := by
  native_decide

theorem amsterdam_schedule_is_exact :
    (G.amsterdamSchedule.accountReadBase, G.amsterdamSchedule.coldAccountAccess,
      G.amsterdamSchedule.warmAccess, G.amsterdamSchedule.copyWord,
      G.amsterdamSchedule.memory.linear, G.amsterdamSchedule.memory.quadraticDivisor,
      G.amsterdamSchedule.veryLow) = (0, 3000, 100, 3, 3, 512, 3) := by
  native_decide

theorem specialization_count : G.specializations.length = 16 := by
  native_decide

theorem specialization_keys_are_cartesian :
    G.specializations.map (fun specialization => (specialization.opcode, specialization.table)) =
      G.allOpcodes.flatMap (fun opcode => G.allDispatchTables.map fun table => (opcode, table)) := by
  native_decide

theorem specialization_keys_are_unique :
    (G.specializations.map fun specialization => (specialization.opcode, specialization.table)).Nodup := by
  native_decide

theorem every_closed_root_is_exact :
    G.specializations.all specializationRefines = true := by
  native_decide

theorem amsterdam_schedule_refines :
    toReferenceSchedule G.amsterdamSchedule =
      Eip803x.Evm.AccountReadStack.Schedule.amsterdam := by
  rfl

theorem access_classification_refines (provider : G.Provider) (state : G.State) (address : UInt256) :
    G.accessIsCold provider state address =
      Eip803x.Evm.AccountReadStack.accessIsCold (toReferenceProvider provider) (toReferenceState state) address := by
  rfl

theorem access_charge_refines (provider : G.Provider) (state : G.State) (address : UInt256) :
    G.accessCharge G.amsterdamSchedule provider state address =
      Eip803x.Evm.AccountReadStack.accountAccessCharge
        Eip803x.Evm.AccountReadStack.Schedule.amsterdam
        (toReferenceProvider provider) (toReferenceState state) address := by
  rfl

/-!
This theorem compares the generated abstract transition with the independently
handwritten abstract transition. Both machines intentionally reuse the proved
`MemoryGas.prepare` foundation for EXTCODECOPY memory expansion; admitted C#
provider and representation behavior remains outside this theorem.
-/
theorem generated_execution_refines_reference
    (provider : G.Provider)
    (instructionTracing : Bool)
    (code : List MemoryStackControl.Byte)
    (opcode : G.Opcode)
    (state : G.State) :
    toReferenceOutcome
        (G.execute G.amsterdamSchedule provider instructionTracing code opcode state) =
      R.execute Eip803x.Evm.AccountReadStack.Schedule.amsterdam
        (toReferenceProvider provider) instructionTracing code
        (toReferenceOpcode opcode) (toReferenceState state) := by
  cases opcode <;>
    simp [G.execute, R.execute, G.popsBeforeGas,
      Eip803x.Generated.AccountReadOpcodeKernel.executeRead,
      Eip803x.Generated.AccountReadOpcodeKernel.executeExtCodeCopy,
      Eip803x.Evm.AccountReadStack.executeRead,
      Eip803x.Evm.AccountReadStack.executeExtCodeCopy,
      Eip803x.Generated.AccountReadOpcodeKernel.charge,
      Eip803x.Evm.AccountReadStack.charge,
      Eip803x.Generated.AccountReadOpcodeKernel.enter,
      Eip803x.Evm.AccountReadStack.enter,
      Eip803x.Generated.AccountReadOpcodeKernel.amsterdamSchedule,
      Eip803x.Evm.AccountReadStack.Schedule.amsterdam,
      Eip803x.Generated.AccountReadOpcodeKernel.addressOfWord,
      Eip803x.Evm.AccountReadStack.addressOfWord,
      Eip803x.Evm.AccountReadStack.addressModulus,
      Eip803x.Generated.AccountReadOpcodeKernel.exhaust,
      Eip803x.Evm.AccountReadStack.exhaust,
      Eip803x.Generated.AccountReadOpcodeKernel.accessCharge,
      Eip803x.Evm.AccountReadStack.accountAccessCharge,
      Eip803x.Generated.AccountReadOpcodeKernel.accessIsCold,
      Eip803x.Evm.AccountReadStack.accessIsCold,
      Eip803x.Generated.AccountReadOpcodeKernel.chargesSecondRead,
      Eip803x.Evm.AccountReadStack.secondReadCharge,
      Eip803x.Generated.AccountReadOpcodeKernel.valueFor,
      Eip803x.Evm.AccountReadStack.valueFor,
      Eip803x.Generated.AccountReadOpcodeKernel.copyWords,
      Eip803x.Evm.AccountReadStack.copyWords,
      Eip803x.Evm.AccountReadStack.copyBytes,
      Eip803x.Generated.AccountReadOpcodeKernel.topIsZero,
      Eip803x.Evm.AccountReadStack.topIsZero,
      Eip803x.Generated.AccountReadOpcodeKernel.isFusionOpcode,
      Eip803x.Evm.AccountReadStack.isFusionOpcode,
      Eip803x.Generated.AccountReadOpcodeKernel.fusionResult,
      Eip803x.Evm.AccountReadStack.fusionResult,
      Eip803x.Generated.AccountReadOpcodeKernel.warmForAccess,
      Eip803x.Evm.AccountReadStack.warmForAccess,
      Eip803x.Generated.AccountReadOpcodeKernel.warmAccount,
      Eip803x.Evm.AccountReadStack.warmAccount,
      Eip803x.Generated.AccountReadOpcodeKernel.containsAddress,
      Eip803x.Evm.AccountReadStack.containsAddress,
      Eip803x.Generated.AccountReadOpcodeKernel.recordAccountRead,
      Eip803x.Evm.AccountReadStack.recordAccountRead,
      Eip803x.Generated.AccountReadOpcodeKernel.recordBytecodeRead,
      Eip803x.Evm.AccountReadStack.recordBytecodeRead,
      Eip803x.Generated.AccountReadOpcodeKernel.pushKnownRoom,
      Eip803x.Evm.AccountReadStack.pushKnownRoom,
      toReferenceProvider, toReferenceOpcode,
      toReferenceOutcome, toReferenceState] <;> rfl

end Eip803x.Refinement.AccountReadOpcode
