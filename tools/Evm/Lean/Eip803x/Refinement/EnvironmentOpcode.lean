-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Generated.EnvironmentOpcodeKernel
import Eip803x.Evm.EnvironmentStack

namespace Eip803x.Refinement.EnvironmentOpcode

namespace G
export Eip803x.Generated.EnvironmentOpcodeKernel
  (Context Opcode DispatchTable Specialization ValueResult Outcome State Status lookupBlockHash allOpcodes
   allDispatchTables specializations opcodeByte fixedGas tracingFlag cancelableFlag closedHandlerRoute
   expectedClosedRoot
   semanticPops semanticPushes dispatchStackInputs dispatchStackGrowth handlerRoute activationGate
   blockHash blobHash value applyValue executePaid execute executeAmsterdam advancePc withGas
   contextAvailable)
end G

namespace R
export Eip803x.Evm.Environment
  (Context ContextWellFormed Opcode Result allOpcodes lookupBlockHash blockHash blobHash execute)
end R

namespace RS
export Eip803x.Evm.EnvironmentStack
  (Outcome applyValueResult pushWord DispatchTable fixedGas semanticPops contextAvailable
   MachineState MachineStatus MachineOutcome withGas advancePc applyValueResultToState
   executePaidState executeState executeAmsterdam)
end RS

open Eip803x.Evm

def toReferenceContext (context : G.Context) : R.Context :=
  { executingAccount := context.executingAccount
    origin := context.origin
    caller := context.caller
    callValue := context.callValue
    calldataSize := context.calldataSize
    codeSize := context.codeSize
    returnDataSize := context.returnDataSize
    gasPrice := context.gasPrice
    coinbase := context.coinbase
    timestamp := context.timestamp
    number := context.number
    prevRandao := context.prevRandao
    gasLimit := context.gasLimit
    chainId := context.chainId
    selfBalance := context.selfBalance
    baseFee := context.baseFee
    blobVersionedHashes := context.blobVersionedHashes
    excessBlobGas := context.excessBlobGas
    blobBaseFee := context.blobBaseFee
    slotNumber := context.slotNumber
    blockHashes := context.blockHashes }

def toReferenceOpcode : G.Opcode → R.Opcode
  | .address => .address
  | .origin => .origin
  | .caller => .caller
  | .callvalue => .callvalue
  | .calldatasize => .calldatasize
  | .codesize => .codesize
  | .gasprice => .gasprice
  | .returndatasize => .returndatasize
  | .blockhash => .blockhash
  | .coinbase => .coinbase
  | .timestamp => .timestamp
  | .number => .number
  | .prevrandao => .prevrandao
  | .gaslimit => .gaslimit
  | .chainid => .chainid
  | .selfbalance => .selfbalance
  | .basefee => .basefee
  | .blobhash => .blobhash
  | .blobbasefee => .blobbasefee
  | .slotnum => .slotnum

def toReferenceResult : G.ValueResult → R.Result
  | .word value => .word value
  | .stackUnderflow => .stackUnderflow
  | .badInstruction => .badInstruction

def toReferenceDispatchTable : G.DispatchTable → RS.DispatchTable
  | .noTrace => .noTrace
  | .noTraceCancelable => .noTraceCancelable
  | .traced => .traced
  | .tracedCancelable => .tracedCancelable

def toReferenceMachineState (state : G.State) : RS.MachineState :=
  { gasLeft := state.gasLeft, pc := state.pc, stack := state.stack }

def toReferenceMachineStatus : G.Status → RS.MachineStatus
  | .ok => .ok
  | .outOfGas => .outOfGas
  | .stackUnderflow => .stackUnderflow
  | .stackOverflow => .stackOverflow
  | .badInstruction => .badInstruction

def toReferenceMachineOutcome (outcome : G.Outcome) : RS.MachineOutcome :=
  { status := toReferenceMachineStatus outcome.status
    state := toReferenceMachineState outcome.state }

def projectNonGasOutcome (outcome : G.Outcome) : Option RS.Outcome :=
  match outcome.status with
  | .ok => some { status := .ok, stack := outcome.state.stack }
  | .stackUnderflow => some { status := .stackUnderflow, stack := outcome.state.stack }
  | .stackOverflow => some { status := .stackOverflow, stack := outcome.state.stack }
  | .badInstruction => some { status := .badInstruction, stack := outcome.state.stack }
  | .outOfGas => none

def specializationRefines (specialization : G.Specialization) : Bool :=
  specialization.tracingFlag == G.tracingFlag specialization.table &&
  specialization.cancelableFlag == G.cancelableFlag specialization.table &&
  specialization.closedRoot == G.expectedClosedRoot specialization.opcode specialization.table &&
  !(specialization.closedRoot.contains "TTracingInst") &&
  !(specialization.closedRoot.contains "TGasPolicy")

/--
The extracted context fields are representations of the concrete C# values.
The two functions name the production blob-base-fee calculator and block-hash
provider at the adapter boundary; neither provider is implemented by this slice.
-/
structure ProductionAdapterPremises
    (calculateBlobBaseFee : Nat → UInt256)
    (productionBlockHash : Nat → Option UInt256)
    (context : G.Context) : Prop where
  wellFormed : R.ContextWellFormed (toReferenceContext context)
  blobBaseFee : ∀ excess, context.excessBlobGas = some excess →
    context.blobBaseFee = calculateBlobBaseFee excess
  blockHashProvider : ∀ number, number < context.number →
    productionBlockHash number =
      if context.number ≤ number + 256 then G.lookupBlockHash number context.blockHashes else none

theorem opcode_bytes_are_exact :
    G.allOpcodes.map G.opcodeByte =
      [0x30, 0x32, 0x33, 0x34, 0x36, 0x38, 0x3a, 0x3d, 0x40, 0x41,
       0x42, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x4a, 0x4b] := by
  native_decide

theorem opcode_bytes_are_unique :
    G.allOpcodes.map G.opcodeByte |>.Nodup := by
  native_decide

theorem specialization_count : G.specializations.length = 80 := by
  native_decide

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

theorem extracted_opcode_coverage_is_reference_complete :
    G.allOpcodes.map toReferenceOpcode = R.allOpcodes := by
  native_decide

theorem fixed_gas_is_exact :
    G.allOpcodes.map G.fixedGas =
      [2, 2, 2, 2, 2, 2, 2, 2, 20, 2, 2, 2, 2, 2, 2, 5, 2, 3, 2, 2] := by
  native_decide

theorem semantic_stack_effects_are_exact :
    G.allOpcodes.map (fun opcode => (G.semanticPops opcode, G.semanticPushes opcode)) =
      [(0, 1), (0, 1), (0, 1), (0, 1), (0, 1), (0, 1), (0, 1), (0, 1),
       (1, 1), (0, 1), (0, 1), (0, 1), (0, 1), (0, 1), (0, 1), (0, 1),
       (0, 1), (1, 1), (0, 1), (0, 1)] := by
  native_decide

theorem checked_dispatch_stack_effects_are_exact :
    G.allOpcodes.map (fun opcode => (G.dispatchStackInputs opcode, G.dispatchStackGrowth opcode)) =
      [(0, 1), (0, 1), (0, 1), (0, 1), (0, 1), (0, 1), (0, 1), (0, 1),
       (0, 0), (0, 1), (0, 1), (0, 1), (0, 1), (0, 1), (0, 1), (0, 1),
       (0, 1), (0, 0), (0, 0), (0, 0)] := by
  native_decide

theorem handler_routes_are_exact :
    G.allOpcodes.map G.handlerRoute =
      [ "EnvAddressOpcode<EvmInstructions.OpAddress<TGasPolicy>,TTracingInst>"
      , "Env32BytesOpcode<EvmInstructions.OpOrigin<TGasPolicy>,TTracingInst>"
      , "EnvAddressOpcode<EvmInstructions.OpCaller<TGasPolicy>,TTracingInst>"
      , "EnvUInt256Opcode<EvmInstructions.OpCallValue<TGasPolicy>,TTracingInst>"
      , "EnvUInt32Opcode<EvmInstructions.OpCallDataSize<TGasPolicy>,TTracingInst>"
      , "CodeSizeOpcode<TTracingInst>"
      , "BlkUInt256Opcode<EvmInstructions.OpGasPrice<TGasPolicy>,TTracingInst>"
      , "ReturnDataSizeOpcode<TTracingInst>"
      , "BlockHashOpcode<TTracingInst>"
      , "BlkAddressOpcode<EvmInstructions.OpCoinbase<TGasPolicy>,TTracingInst>"
      , "BlkUInt64Opcode<EvmInstructions.OpTimestamp<TGasPolicy>,TTracingInst>"
      , "BlkUInt64Opcode<EvmInstructions.OpNumber<TGasPolicy>,TTracingInst>"
      , "PrevRandaoOpcode<TTracingInst>"
      , "BlkUInt64Opcode<EvmInstructions.OpGasLimit<TGasPolicy>,TTracingInst>"
      , "Env32BytesOpcode<EvmInstructions.OpChainId<TGasPolicy>,TTracingInst>"
      , "SelfBalanceOpcode<TTracingInst>"
      , "BlkUInt256Opcode<EvmInstructions.OpBaseFee<TGasPolicy>,TTracingInst>"
      , "BlobHashOpcode<TTracingInst>"
      , "BlobBaseFeeOpcode<TTracingInst>"
      , "SlotNumOpcode<TTracingInst>" ] := by
  native_decide

theorem activation_gates_are_exact :
    G.allOpcodes.map G.activationGate =
      [ "unconditional", "unconditional", "unconditional", "unconditional"
      , "unconditional", "unconditional", "unconditional", "spec.ReturnDataOpcodesEnabled"
      , "unconditional", "unconditional", "unconditional", "unconditional"
      , "unconditional", "unconditional", "spec.ChainIdOpcodeEnabled"
      , "spec.SelfBalanceOpcodeEnabled", "spec.BaseFeeEnabled", "spec.IsEip4844Enabled"
      , "spec.BlobBaseFeeEnabled", "spec.IsEip7843Enabled" ] := by
  native_decide

theorem lookupBlockHash_refines (number : Nat) (entries : List (Nat × UInt256)) :
    G.lookupBlockHash number entries = R.lookupBlockHash number entries := by
  induction entries with
  | nil => rfl
  | cons head tail ih =>
      rcases head with ⟨candidate, hash⟩
      simp only [G.lookupBlockHash, R.lookupBlockHash]
      split <;> simp_all

theorem blockHash_refines (context : G.Context) (query : UInt256) :
    G.blockHash context query = R.blockHash (toReferenceContext context) query := by
  by_cases hWidth : query.val < 2 ^ 64 <;>
    by_cases hPast : query.val < context.number <;>
      by_cases hRecent : context.number ≤ query.val + 256 <;>
        simp [G.blockHash, R.blockHash, toReferenceContext, hWidth, hPast, hRecent,
          lookupBlockHash_refines]

theorem blobHash_refines (context : G.Context) (query : UInt256) :
    G.blobHash context query = R.blobHash (toReferenceContext context) query := by
  rfl

theorem blockHash_matches_production_provider
    (calculateBlobBaseFee : Nat → UInt256)
    (productionBlockHash : Nat → Option UInt256)
    (context : G.Context)
    (adapter : ProductionAdapterPremises calculateBlobBaseFee productionBlockHash context)
    (query : UInt256) :
    G.blockHash context query =
      if query.val < 2 ^ 64 ∧ query.val < context.number then
        (productionBlockHash query.val).getD Word.zero
      else Word.zero := by
  by_cases hWidth : query.val < 2 ^ 64
  · by_cases hPast : query.val < context.number
    · have hProvider := adapter.blockHashProvider query.val hPast
      by_cases hRecent : context.number ≤ query.val + 256 <;>
        simp_all [G.blockHash] <;> omega
    · simp [G.blockHash, hWidth, hPast]
  · simp [G.blockHash, hWidth]

theorem blobBaseFee_matches_production_calculator
    (calculateBlobBaseFee : Nat → UInt256)
    (productionBlockHash : Nat → Option UInt256)
    (context : G.Context)
    (adapter : ProductionAdapterPremises calculateBlobBaseFee productionBlockHash context)
    (excess : Nat) (hExcess : context.excessBlobGas = some excess) :
    context.blobBaseFee = calculateBlobBaseFee excess :=
  adapter.blobBaseFee excess hExcess

theorem extracted_value_selection_correct
    (calculateBlobBaseFee : Nat → UInt256)
    (productionBlockHash : Nat → Option UInt256)
    (context : G.Context)
    (adapter : ProductionAdapterPremises calculateBlobBaseFee productionBlockHash context)
    (opcode : G.Opcode) (argument : Option UInt256) :
    toReferenceResult (G.value context opcode argument) =
      R.execute ⟨toReferenceContext context, adapter.wellFormed⟩
        (toReferenceOpcode opcode) argument := by
  cases opcode <;> cases argument <;>
    simp [G.value, R.execute, toReferenceOpcode, toReferenceResult,
      toReferenceContext, blockHash_refines, blobHash_refines]
  all_goals
    cases hExcess : context.excessBlobGas <;>
      cases hSlot : context.slotNumber <;>
        rfl

theorem extracted_push_and_status_projection_correct
    (state : G.State) (result : G.ValueResult) :
    projectNonGasOutcome (G.applyValue state result) =
      some (RS.applyValueResult state.stack (toReferenceResult result)) := by
  cases result with
  | stackUnderflow => rfl
  | badInstruction => rfl
  | word value =>
      unfold G.applyValue RS.applyValueResult RS.pushWord projectNonGasOutcome toReferenceResult
      cases hPush : state.stack.push value <;> simp [hPush]

theorem fixedGas_refines (opcode : G.Opcode) :
    G.fixedGas opcode = RS.fixedGas (toReferenceOpcode opcode) := by
  cases opcode <;> rfl

theorem semanticPops_refines (opcode : G.Opcode) :
    G.semanticPops opcode = RS.semanticPops (toReferenceOpcode opcode) := by
  cases opcode <;> rfl

theorem contextAvailable_refines (context : G.Context) (opcode : G.Opcode) :
    G.contextAvailable context opcode =
      RS.contextAvailable (toReferenceContext context) (toReferenceOpcode opcode) := by
  cases opcode <;> rfl

theorem value_refines
    (context : G.Context)
    (wellFormed : R.ContextWellFormed (toReferenceContext context))
    (opcode : G.Opcode) (argument : Option UInt256) :
    toReferenceResult (G.value context opcode argument) =
      R.execute ⟨toReferenceContext context, wellFormed⟩
        (toReferenceOpcode opcode) argument := by
  cases opcode <;> cases argument <;>
    simp [G.value, R.execute, toReferenceOpcode, toReferenceResult,
      toReferenceContext, blockHash_refines, blobHash_refines]
  all_goals
    cases hExcess : context.excessBlobGas <;>
      cases hSlot : context.slotNumber <;>
        rfl

theorem applyValue_refines (state : G.State) (result : G.ValueResult) :
    toReferenceMachineOutcome (G.applyValue state result) =
      RS.applyValueResultToState (toReferenceMachineState state) (toReferenceResult result) := by
  cases result with
  | stackUnderflow =>
      simp [G.applyValue, RS.applyValueResultToState, toReferenceMachineOutcome,
        toReferenceMachineStatus, toReferenceMachineState, toReferenceResult]
  | badInstruction =>
      simp [G.applyValue, RS.applyValueResultToState, toReferenceMachineOutcome,
        toReferenceMachineStatus, toReferenceMachineState, toReferenceResult]
  | word value =>
      cases hPush : state.stack.push value <;>
        simp [G.applyValue, RS.applyValueResultToState, toReferenceMachineOutcome,
          toReferenceMachineStatus, toReferenceMachineState, toReferenceResult, hPush]

theorem executePaid_refines
    (context : G.Context)
    (wellFormed : R.ContextWellFormed (toReferenceContext context))
    (opcode : G.Opcode) (state : G.State) :
    toReferenceMachineOutcome (G.executePaid context opcode state) =
      RS.executePaidState ⟨toReferenceContext context, wellFormed⟩
        (toReferenceOpcode opcode) (toReferenceMachineState state) := by
  unfold G.executePaid RS.executePaidState
  rw [semanticPops_refines]
  by_cases hPops : RS.semanticPops (toReferenceOpcode opcode) = 1
  · simp only [hPops, if_true]
    cases hPop : state.stack.pop with
    | none => simp [toReferenceMachineOutcome, toReferenceMachineStatus,
        toReferenceMachineState, hPop]
    | some pair =>
        rcases pair with ⟨argument, stack⟩
        simp only [toReferenceMachineState, hPop]
        rw [← value_refines context wellFormed]
        simpa [toReferenceMachineState] using
          applyValue_refines { state with stack } (G.value context opcode (some argument))
  · simp only [hPops, if_false]
    rw [← value_refines context wellFormed]
    exact applyValue_refines state (G.value context opcode none)

theorem execute_refines
    (context : G.Context)
    (wellFormed : R.ContextWellFormed (toReferenceContext context))
    (opcode : G.Opcode) (state : G.State) :
    toReferenceMachineOutcome (G.execute context opcode state) =
      RS.executeState ⟨toReferenceContext context, wellFormed⟩
        (toReferenceOpcode opcode) (toReferenceMachineState state) := by
  cases hAvailable : G.contextAvailable context opcode with
  | false =>
      have hReferenceAvailable :
          RS.contextAvailable (toReferenceContext context) (toReferenceOpcode opcode) = false := by
        rw [← contextAvailable_refines]
        exact hAvailable
      simp [G.execute, RS.executeState, hAvailable, hReferenceAvailable, G.advancePc,
        RS.advancePc, toReferenceMachineOutcome, toReferenceMachineStatus,
        toReferenceMachineState]
  | true =>
      have hReferenceAvailable :
          RS.contextAvailable (toReferenceContext context) (toReferenceOpcode opcode) = true := by
        rw [← contextAvailable_refines]
        exact hAvailable
      by_cases hGas : G.fixedGas opcode ≤ state.gasLeft
      · have hReferenceGas : RS.fixedGas (toReferenceOpcode opcode) ≤ state.gasLeft := by
          rwa [← fixedGas_refines]
        simpa [G.execute, RS.executeState, hAvailable, hReferenceAvailable, hGas,
          hReferenceGas, G.advancePc, RS.advancePc, G.withGas, RS.withGas,
          toReferenceMachineState, fixedGas_refines] using
          executePaid_refines context wellFormed opcode
            (G.withGas (G.advancePc state)
              ((G.advancePc state).gasLeft - G.fixedGas opcode))
      · have hReferenceGas : ¬RS.fixedGas (toReferenceOpcode opcode) ≤ state.gasLeft := by
          rwa [← fixedGas_refines]
        simp [G.execute, RS.executeState, hAvailable, hReferenceAvailable, hGas,
          hReferenceGas, G.advancePc, RS.advancePc, G.withGas, RS.withGas,
          toReferenceMachineOutcome, toReferenceMachineStatus, toReferenceMachineState]

/--
Closed Amsterdam operational refinement for all 20 admitted environment opcodes
and all four production dispatch-table specializations.  It covers success,
out-of-gas, stack underflow/overflow, missing-header bad-instruction, and every
provider result already present in the admitted context.  Exact trace callback
effects, cancellation scheduling, and construction of provider results are
separate adapter/composition obligations.
-/
theorem closed_amsterdam_refines
    (context : G.Context)
    (wellFormed : R.ContextWellFormed (toReferenceContext context))
    (table : G.DispatchTable) (opcode : G.Opcode) (state : G.State) :
    toReferenceMachineOutcome (G.executeAmsterdam table context opcode state) =
      RS.executeAmsterdam (toReferenceDispatchTable table)
        ⟨toReferenceContext context, wellFormed⟩
        (toReferenceOpcode opcode) (toReferenceMachineState state) := by
  cases table <;> exact execute_refines context wellFormed opcode state

theorem executePaid_preserves_pc (context : G.Context) (opcode : G.Opcode) (state : G.State) :
    (G.executePaid context opcode state).state.pc = state.pc := by
  simp only [G.executePaid]
  split
  · cases hPop : state.stack.pop with
    | none => rfl
    | some pair =>
        rcases pair with ⟨argument, stack⟩
        simp only [G.applyValue]
        cases hValue : G.value context opcode (some argument) <;> simp
        case word value => cases stack.push value <;> rfl
  · simp only [G.applyValue]
    cases hValue : G.value context opcode none <;> simp
    case word value => cases state.stack.push value <;> rfl

theorem executePaid_preserves_gas (context : G.Context) (opcode : G.Opcode) (state : G.State) :
    (G.executePaid context opcode state).state.gasLeft = state.gasLeft := by
  simp only [G.executePaid]
  split
  · cases hPop : state.stack.pop with
    | none => rfl
    | some pair =>
        rcases pair with ⟨argument, stack⟩
        simp only [G.applyValue]
        cases hValue : G.value context opcode (some argument) <;> simp
        case word value => cases stack.push value <;> rfl
  · simp only [G.applyValue]
    cases hValue : G.value context opcode none <;> simp
    case word value => cases state.stack.push value <;> rfl

theorem extracted_pc_effect_is_one (context : G.Context) (opcode : G.Opcode) (state : G.State) :
    (G.execute context opcode state).state.pc = state.pc + 1 := by
  simp only [G.execute]
  split
  · rfl
  · split
    · simpa [G.advancePc, G.withGas] using
        executePaid_preserves_pc context opcode
          (G.withGas (G.advancePc state)
            ((G.advancePc state).gasLeft - G.fixedGas opcode))
    · rfl

theorem extracted_gas_effect_when_available
    (context : G.Context) (opcode : G.Opcode) (state : G.State)
    (hAvailable : G.contextAvailable context opcode = true) :
    (G.execute context opcode state).state.gasLeft =
      if G.fixedGas opcode ≤ state.gasLeft then state.gasLeft - G.fixedGas opcode else 0 := by
  by_cases hGas : G.fixedGas opcode ≤ state.gasLeft
  · simp [G.execute, G.advancePc, G.withGas, hAvailable, hGas,
      executePaid_preserves_gas]
  · simp [G.execute, G.advancePc, G.withGas, hAvailable, hGas]

theorem missing_blob_base_fee_is_bad_instruction_without_charge
    (context : G.Context) (state : G.State) (h : context.excessBlobGas = none) :
    G.execute context .blobbasefee state =
      { status := .badInstruction, state := G.advancePc state } := by
  simp [G.execute, G.contextAvailable, h]

theorem missing_slot_number_is_bad_instruction_without_charge
    (context : G.Context) (state : G.State) (h : context.slotNumber = none) :
    G.execute context .slotnum state =
      { status := .badInstruction, state := G.advancePc state } := by
  simp [G.execute, G.contextAvailable, h]

theorem indexed_opcodes_charge_before_stack_underflow
    (context : G.Context) (opcode : G.Opcode) (state : G.State)
    (hIndexed : G.semanticPops opcode = 1)
    (hEmpty : state.stack = Eip803x.Evm.MemoryStackControl.Stack.empty)
    (hGas : G.fixedGas opcode ≤ state.gasLeft) :
    G.execute context opcode state =
      { status := .stackUnderflow,
        state := { state with gasLeft := state.gasLeft - G.fixedGas opcode, pc := state.pc + 1 } } := by
  have hAvailable : G.contextAvailable context opcode = true := by
    cases opcode <;> simp_all [G.semanticPops, G.contextAvailable]
  simp [G.execute, hAvailable, hGas, G.executePaid, hIndexed, hEmpty,
    Eip803x.Evm.MemoryStackControl.Stack.empty,
    Eip803x.Evm.MemoryStackControl.Stack.pop, G.withGas, G.advancePc]

end Eip803x.Refinement.EnvironmentOpcode
