-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import CallCreateOpcodeExtractor.Generated.CallCreateOpcodeKernel
import CallCreateOpcodeExtractor.Specification.CallCreateOperational
import Eip803x.Evm.CallCreateFrame
import Eip803x.Evm.SelfDestruct

namespace Eip803x.Generated.CallCreateOpcodeRefinement

open Eip803x.GasMachine
open Eip803x.Evm.MemoryStackControl.Stack
open Eip803x.Evm.CallCreateExecution
open Eip803x.Generated.CallCreateOpcodeKernel

theorem every_closed_root_admitted
    (opcode : Eip803x.Evm.CallCreateExecution.Opcode) (table : DispatchTable) :
    profileAdmitted opcode table = true := by
  cases opcode <;> cases table <;> native_decide

private theorem generated_call_matches_reference
    (schedule : Schedule) (table : DispatchTable) (kind : CallKind)
    (oracle : HandlerOracle) (state : MachineState) :
    runCall schedule table kind oracle state =
      Eip803x.Evm.CallCreateExecution.Reference.executeCall
        schedule table kind oracle state := by
  rfl

private theorem generated_create_matches_reference
    (schedule : Schedule) (table : DispatchTable) (kind : CreateKind)
    (oracle : HandlerOracle) (state : MachineState) :
    runCreate schedule table kind oracle state =
      Eip803x.Evm.CallCreateExecution.Reference.executeCreate
        schedule table kind oracle state := by
  rfl

private theorem generated_selfdestruct_matches_reference
    (schedule : Schedule) (table : DispatchTable)
    (oracle : HandlerOracle) (state : MachineState) :
    runSelfDestruct schedule table oracle state =
      Eip803x.Evm.CallCreateExecution.Reference.executeSelfDestruct
        schedule table oracle state := by
  rfl

private theorem generated_instruction_entry_matches_reference
    (table : DispatchTable) (opcode : Opcode) (state : MachineState) :
    beginInstruction table opcode state =
      Eip803x.Evm.CallCreateExecution.Reference.enterInstruction
        table opcode state := by
  rfl

private theorem generated_core_matches_reference
    (schedule : Schedule) (table : DispatchTable) (opcode : Opcode)
    (oracle : HandlerOracle) (state : MachineState) :
    executeCore schedule table opcode oracle state =
      Eip803x.Evm.CallCreateExecution.Reference.executeCore
        schedule table opcode oracle state := by
  cases opcode <;>
    simp only [executeCore,
      Eip803x.Evm.CallCreateExecution.Reference.executeCore, callKind,
      Eip803x.Evm.CallCreateExecution.Reference.asCallKind,
      generated_call_matches_reference, generated_create_matches_reference,
      generated_selfdestruct_matches_reference,
      generated_instruction_entry_matches_reference]

private theorem generated_failure_closure_matches_reference
    (table : DispatchTable) (result : Outcome) :
    closeOuterFailure table result =
      Eip803x.Evm.CallCreateExecution.Reference.closeFailure table result := by
  rfl

private theorem generated_resume_matches_reference
    (schedule : Schedule) (table : DispatchTable) (child : ChildOutcome)
    (state : MachineState) :
    resumeChild schedule table child state =
      Eip803x.Evm.CallCreateExecution.Reference.resumeChild
        schedule table child state := by
  rfl

theorem closed_amsterdam_refines
    (table : DispatchTable) (opcode : Opcode) (oracle : HandlerOracle)
    (state : MachineState) (child : ChildOutcome) :
    executeAmsterdam table opcode oracle state =
        Eip803x.Evm.CallCreateExecution.Reference.executeAmsterdam
          table opcode oracle state /\
      resumeChild amsterdamSchedule table child state =
        Eip803x.Evm.CallCreateExecution.Reference.resumeAmsterdam table child state := by
  constructor
  · simp only [executeAmsterdam, executeExtracted, every_closed_root_admitted,
      if_true, generated_core_matches_reference,
      generated_failure_closure_matches_reference,
      Eip803x.Evm.CallCreateExecution.Reference.executeAmsterdam]
    rfl
  · rw [generated_resume_matches_reference]
    rfl

theorem semantic_opcode_is_complete (opcode : Eip803x.Evm.CallCreateExecution.Opcode) :
    (descriptor opcode).bind (fun item => semanticOpcode item.operation) = some opcode := by
  cases opcode <;> native_decide

theorem create_trace_ownership_is_exact :
    (descriptor .create).map Descriptor.ownsPreChildTraceEnd = some true /\
      (descriptor .create2).map Descriptor.ownsPreChildTraceEnd = some true /\
      (descriptor .call).map Descriptor.ownsPreChildTraceEnd = some false /\
      (descriptor .selfdestruct).map Descriptor.ownsPreChildTraceEnd = some false := by
  native_decide

theorem dispatch_table_tracing_matches_reference (table : DispatchTable) :
    tableTracing table =
      Eip803x.Evm.CallCreateExecution.Reference.referenceTableTracing table := by
  cases table <;> rfl

theorem ripemd160_is_not_direct_precompile_eligible :
    canExecutePrecompileCallDirectly (Eip803x.Evm.Word.ofNat 3) = false := by
  native_decide

theorem call_address_normalization_matches_reference (word : UInt256) :
    addressOfWord word =
      Eip803x.Evm.CallCreateExecution.Reference.addressOfWord word := by
  rfl

theorem high_bit_ripemd160_alias_is_not_direct_precompile_eligible :
    canExecutePrecompileCallDirectly
        (Eip803x.Evm.Word.ofNat ((2 ^ 160) + 3)) = false := by
  native_decide

theorem every_call_kind_normalizes_high_bit_ripemd160_alias :
    let alias := Eip803x.Evm.Word.ofNat ((2 ^ 160) + 3)
    let gas := Eip803x.Evm.Word.ofNat 100
    let value := Eip803x.Evm.Word.ofNat 7
    let zero := Eip803x.Evm.Word.zero
    let callStack := Eip803x.Evm.MemoryStackControl.Stack.fromWords
      [gas, alias, value, zero, zero, zero, zero]
    let valuelessStack := Eip803x.Evm.MemoryStackControl.Stack.fromWords
      [gas, alias, zero, zero, zero, zero]
    (popCall .call value callStack).1.map (fun operands => operands.codeSource) =
        some (Eip803x.Evm.Word.ofNat 3) /\
      (popCall .callcode value callStack).1.map (fun operands => operands.codeSource) =
        some (Eip803x.Evm.Word.ofNat 3) /\
      (popCall .delegatecall value valuelessStack).1.map (fun operands => operands.codeSource) =
        some (Eip803x.Evm.Word.ofNat 3) /\
      (popCall .staticcall value valuelessStack).1.map (fun operands => operands.codeSource) =
        some (Eip803x.Evm.Word.ofNat 3) := by
  native_decide

theorem selfdestruct_high_bit_beneficiary_normalizes_before_effects :
    let alias := Eip803x.Evm.Word.ofNat ((2 ^ 160) + 3)
    let normalized := Eip803x.Evm.Word.ofNat 3
    addressOfWord alias = normalized /\
      Eip803x.Evm.CallCreateExecution.Reference.addressOfWord alias = normalized /\
      (forall schedule state,
        chargeAccess schedule (addressOfWord alias) state =
          chargeAccess schedule normalized state) /\
      (forall schedule state,
        Eip803x.Evm.CallCreateExecution.Reference.payAccess schedule
            (Eip803x.Evm.CallCreateExecution.Reference.addressOfWord alias) state =
          Eip803x.Evm.CallCreateExecution.Reference.payAccess schedule normalized state) /\
      (forall source value,
        TraceEvent.selfdestruct source (addressOfWord alias) value =
          TraceEvent.selfdestruct source normalized value) := by
  dsimp only
  have hGenerated :
      addressOfWord (Eip803x.Evm.Word.ofNat ((2 ^ 160) + 3)) =
        Eip803x.Evm.Word.ofNat 3 := by
    native_decide
  have hReference :
      Eip803x.Evm.CallCreateExecution.Reference.addressOfWord
          (Eip803x.Evm.Word.ofNat ((2 ^ 160) + 3)) =
        Eip803x.Evm.Word.ofNat 3 := by
    native_decide
  simp [hGenerated, hReference]

theorem direct_precompile_remaining_gas_is_bounded
    (entry : FrameEntry) (oracle : HandlerOracle)
    (h : inlinePrecompileGasConsistent entry oracle = true) :
    oracle.precompileGasRemaining <= entry.child.gas.gasLeft := by
  simpa [inlinePrecompileGasConsistent] using h

theorem create_deposit_phase_order_is_production_order :
    extractedCreateDepositOrder =
      Eip803x.Evm.CallCreateExecution.Reference.createDepositOrder := by
  rfl

theorem inconsistent_create_facts_fail_before_effects
    (schedule : Schedule) (table : DispatchTable) (kind : CreateKind)
    (operands : CreateOperands) (oracle : HandlerOracle) (state : MachineState)
    (h : createFactsConsistent oracle.facts = false) :
    afterCreateChecks schedule table kind operands oracle state = fail .oracleMismatch state := by
  simp [afterCreateChecks, h]

theorem inconsistent_child_gas_fails_before_merge
    (schedule : Schedule) (table : DispatchTable) (frame : ChildFrame)
    (child : ChildOutcome) (state : MachineState)
    (h : childGasConsistent frame child = false) :
    resumeChild schedule table child { state with stagedChild := some frame } =
      fail .oracleMismatch { state with stagedChild := some frame } := by
  simp [resumeChild, h]

theorem amsterdam_schedule_refines_handwritten_constants :
    amsterdamSchedule.callStipend =
        Eip803x.AccountPricing.AccountGasSchedule.amsterdam.callStipend /\
      amsterdamSchedule.warmAccess =
        Eip803x.AccountPricing.AccountGasSchedule.amsterdam.base.warmAccess /\
      amsterdamSchedule.coldAccess =
        Eip803x.AccountPricing.AccountGasSchedule.amsterdam.base.coldAccountAccess /\
      amsterdamSchedule.createAccess =
        Eip803x.AccountPricing.AccountGasSchedule.amsterdam.base.createAccess /\
      amsterdamSchedule.initCodeWord =
        Eip803x.AccountPricing.AccountGasSchedule.amsterdam.initCodeWordCost /\
      amsterdamSchedule.create2HashWord =
        Eip803x.AccountPricing.AccountGasSchedule.amsterdam.create2HashWordCost /\
      amsterdamSchedule.accountWrite =
        Eip803x.AccountPricing.AccountGasSchedule.amsterdam.base.accountWrite /\
      amsterdamSchedule.newAccountState =
        Eip803x.AccountPricing.AccountGasSchedule.amsterdam.base.newAccountGas /\
      amsterdamSchedule.codeDepositExecutionPerWord =
        Eip803x.AccountPricing.AccountGasSchedule.amsterdam.codeDepositExecutionWordCost /\
      amsterdamSchedule.codeDepositStatePerByte =
        Eip803x.AccountPricing.AccountGasSchedule.amsterdam.base.cpsb := by
  native_decide

def toReferenceCallKind : CallKind -> Eip803x.Evm.CallCreateFrame.CallKind
  | .call => .call
  | .callcode => .callcode
  | .delegatecall => .delegatecall
  | .staticcall => .staticcall

theorem call_value_rule_refines (kind : CallKind) (value : UInt256) :
    callHasTransfer kind value =
      Eip803x.Evm.CallCreateFrame.CallKind.hasTransfer (toReferenceCallKind kind) value.val := by
  cases kind <;> rfl

theorem callcode_never_charges_new_account_state (value : UInt256) (targetDead : Bool)
    (input : Eip803x.AccountPricing.CallCodeSituation) :
    callCreatesAccount .callcode value targetDead = false /\
      (Eip803x.AccountPricing.priceCallCode
        Eip803x.AccountPricing.AccountGasSchedule.amsterdam input).stateCharge = 0 := by
  simp [callCreatesAccount, Eip803x.AccountPricing.priceCallCode,
    Eip803x.AccountPricing.accountEffect]

theorem call_new_account_predicate_is_exact (value : UInt256) (targetDead : Bool) :
    callCreatesAccount .call value targetDead = (value.val != 0 && targetDead) := by
  simp [callCreatesAccount]

theorem create_entry_cost_is_handwritten_amsterdam_cost (kind : CreateKind) (words : Nat) :
    createCost amsterdamSchedule kind words =
      Eip803x.AccountPricing.AccountGasSchedule.amsterdam.base.createAccess +
        Eip803x.AccountPricing.AccountGasSchedule.amsterdam.initCodeWordCost * words +
        (if kind = .create2 then
          Eip803x.AccountPricing.AccountGasSchedule.amsterdam.create2HashWordCost * words else 0) := by
  cases kind <;> rfl

theorem child_success_gas_refines (parent childWorld : Eip803x.Evm.CallCreateFrame.WorldFrame)
    (entry : FrameEntry) (child : FrameGasState) :
    (Eip803x.Evm.CallCreateFrame.finishFrame parent entry childWorld child .success).gas =
      mergeSuccess entry child := by
  rfl

theorem child_revert_gas_refines (parent childWorld : Eip803x.Evm.CallCreateFrame.WorldFrame)
    (entry : FrameEntry) (child : FrameGasState) :
    (Eip803x.Evm.CallCreateFrame.finishFrame parent entry childWorld child .revert).gas =
      mergeRevert entry child := by
  rfl

theorem child_exception_gas_refines (parent childWorld : Eip803x.Evm.CallCreateFrame.WorldFrame)
    (entry : FrameEntry) (child : FrameGasState) :
    (Eip803x.Evm.CallCreateFrame.finishFrame parent entry childWorld child .exceptional).gas =
      mergeException entry child := by
  rfl

theorem failed_new_account_revert_refill_refines
    (parent childWorld : Eip803x.Evm.CallCreateFrame.WorldFrame)
    (entry : FrameEntry) (child : FrameGasState) (stateCharge : Nat) :
    (Eip803x.Evm.CallCreateFrame.completeCall
      parent entry childWorld child stateCharge .revert).outcome.gas =
        refillState stateCharge (mergeRevert entry child) := by
  rfl

theorem failed_new_account_exception_refill_refines
    (parent childWorld : Eip803x.Evm.CallCreateFrame.WorldFrame)
    (entry : FrameEntry) (child : FrameGasState) (stateCharge : Nat) :
    (Eip803x.Evm.CallCreateFrame.completeCall
      parent entry childWorld child stateCharge .exceptional).outcome.gas =
        refillState stateCharge (mergeException entry child) := by
  rfl

theorem generated_deposit_cost_is_handwritten_amsterdam_cost
    (output : List EvmByte) (hSize : output.length <= amsterdamSchedule.maxCodeSize) :
    depositCost amsterdamSchedule output =
      some (((output.length + 31) / 32) *
          Eip803x.AccountPricing.AccountGasSchedule.amsterdam.codeDepositExecutionWordCost,
        output.length * Eip803x.AccountPricing.AccountGasSchedule.amsterdam.base.cpsb) := by
  have hSize' : output.length <= 24576 := by
    simpa [amsterdamSchedule] using hSize
  simp [depositCost, amsterdamSchedule,
    Eip803x.AccountPricing.AccountGasSchedule.amsterdam,
    Eip803x.GasSchedule.amsterdam, hSize', Nat.mul_comm]

theorem handwritten_empty_create_completion_uses_child_success
    (parent childWorld : Eip803x.Evm.CallCreateFrame.WorldFrame)
    (entry : FrameEntry) (child : FrameGasState) (createStateCharge : Nat) :
    (Eip803x.Evm.CallCreateFrame.completeCreate
      Eip803x.AccountPricing.AccountGasSchedule.amsterdam parent entry childWorld child
      createStateCharge 0 .empty).outcome.gas = mergeSuccess entry child := by
  rfl

theorem handwritten_invalid_create_completion_burns_child_and_refills
    (parent childWorld : Eip803x.Evm.CallCreateFrame.WorldFrame)
    (entry : FrameEntry) (child : FrameGasState) (createStateCharge runtimeLength : Nat) :
    (Eip803x.Evm.CallCreateFrame.completeCreate
      Eip803x.AccountPricing.AccountGasSchedule.amsterdam parent entry childWorld child
      createStateCharge runtimeLength .invalid).outcome.gas =
        refillState createStateCharge (mergeException entry child) := by
  rfl

theorem handwritten_deposit_oog_create_completion_burns_child_and_refills
    (parent childWorld : Eip803x.Evm.CallCreateFrame.WorldFrame)
    (entry : FrameEntry) (child : FrameGasState) (createStateCharge runtimeLength : Nat) :
    (Eip803x.Evm.CallCreateFrame.completeCreate
      Eip803x.AccountPricing.AccountGasSchedule.amsterdam parent entry childWorld child
      createStateCharge runtimeLength .depositOutOfGas).outcome.gas =
        refillState createStateCharge (mergeException entry child) := by
  rfl

theorem generated_create_deposit_failure_gas_is_exception_merge_and_refill
    (schedule : Schedule) (frame : ChildFrame) (childGas : FrameGasState) :
    createDepositFailureGas schedule frame childGas =
      if frame.createStateCharged then
        refillState schedule.createState (mergeException frame.gasEntry childGas)
      else mergeException frame.gasEntry childGas := by
  rfl

theorem merge_before_repayment_then_repay_is_handwritten_success
    (entry : FrameEntry) (child : FrameGasState) :
    repayStateFromGasLeft (mergeBeforeStateSpillRepayment entry child) =
      mergeSuccess entry child := by
  rfl

theorem refunded_create_halt_is_handwritten_exception
    (entry : FrameEntry) (child : FrameGasState) :
    revertRefundedCreateToHalt entry child = mergeException entry child := by
  rfl

private theorem gasState_eq_of_fields (left right : GasState)
    (hGas : left.gasLeft = right.gasLeft)
    (hReservoir : left.stateReservoir = right.stateReservoir)
    (hFrom : left.stateFromGasLeft = right.stateFromGasLeft)
    (hUsed : left.stateUsed = right.stateUsed)
    (hRefund : left.refundCounter = right.refundCounter) : left = right := by
  cases left
  cases right
  simp_all

theorem successful_parent_deposit_matches_child_charge_before_repay
    (entry : FrameEntry) (child : FrameGasState) (executionCost stateCost : Nat)
    (childAfterExecution childAfterState parentAfterExecution parentAfterState : GasState)
    (hPausedReservoir : entry.pausedParent.stateReservoir = 0)
    (hChildExecution : chargeExecution executionCost child.gas = .ok childAfterExecution)
    (hChildState : chargeState stateCost childAfterExecution = .ok childAfterState)
    (hParentExecution :
      chargeExecution executionCost (mergeBeforeStateSpillRepayment entry child) =
        .ok parentAfterExecution)
    (hParentState : chargeState stateCost parentAfterExecution = .ok parentAfterState) :
    parentAfterState =
      mergeBeforeStateSpillRepayment entry { child with gas := childAfterState } := by
  simp only [chargeExecution] at hChildExecution hParentExecution
  split at hChildExecution <;> simp_all
  split at hParentExecution <;> simp_all
  simp only [chargeState] at hChildState hParentState
  split at hChildState <;> simp_all
  split at hParentState <;> simp_all
  subst_vars
  have hChildFields := chargedState_fields stateCost
    { child.gas with gasLeft := child.gas.gasLeft - executionCost }
  have hParentFields := chargedState_fields stateCost
    { gasLeft := entry.pausedParent.gasLeft + child.gas.gasLeft - executionCost
      stateReservoir := child.gas.stateReservoir
      stateFromGasLeft := entry.pausedParent.stateFromGasLeft + child.gas.stateFromGasLeft
      stateUsed := child.gas.stateUsed
      refundCounter := child.gas.refundCounter }
  simp only at hChildFields hParentFields
  rcases hChildFields with ⟨hcr, hcg, hcf, hcu, hcc⟩
  rcases hParentFields with ⟨hpr, hpg, hpf, hpu, hpc⟩
  apply gasState_eq_of_fields <;>
    simp_all [mergeBeforeStateSpillRepayment, stateChargeFromGasLeft,
      stateChargeFromReservoir] <;> omega

theorem successful_parent_deposit_observes_handwritten_child_deposit
    (entry : FrameEntry) (child : FrameGasState) (executionCost stateCost : Nat)
    (childAfterExecution childAfterState parentAfterExecution parentAfterState : GasState)
    (hPausedReservoir : entry.pausedParent.stateReservoir = 0)
    (hChildExecution : chargeExecution executionCost child.gas = .ok childAfterExecution)
    (hChildState : chargeState stateCost childAfterExecution = .ok childAfterState)
    (hParentExecution :
      chargeExecution executionCost (mergeBeforeStateSpillRepayment entry child) =
        .ok parentAfterExecution)
    (hParentState : chargeState stateCost parentAfterExecution = .ok parentAfterState) :
    repayStateFromGasLeft parentAfterState =
      mergeSuccess entry { child with gas := childAfterState } := by
  rw [successful_parent_deposit_matches_child_charge_before_repay entry child
    executionCost stateCost childAfterExecution childAfterState parentAfterExecution
    parentAfterState hPausedReservoir hChildExecution hChildState hParentExecution hParentState]
  exact merge_before_repayment_then_repay_is_handwritten_success entry
    { child with gas := childAfterState }

theorem generated_failure_world_restores_snapshot (frame : ChildFrame) (state : MachineState) :
    (restoreFailureWorld frame state).world = frame.snapshot /\
      (restoreFailureWorld frame state).journal = frame.journalSnapshot /\
      (restoreFailureWorld frame state).returnData = [] /\
      (restoreFailureWorld frame state).stagedChild = none := by
  simp [restoreFailureWorld]

theorem call_child_success_merge_matches_handwritten
    (parent childWorld : Eip803x.Evm.CallCreateFrame.WorldFrame)
    (entry : FrameEntry) (child : FrameGasState) :
    mergeSuccess entry child =
      (Eip803x.Evm.CallCreateFrame.completeCall
        parent entry childWorld child 0 .success).outcome.gas := by
  rfl

structure NonExecutionGas where
  stateReservoir : Nat
  stateFromGasLeft : Nat
  stateUsed : Nat
  refundCounter : Int
  deriving DecidableEq, Repr

def nonExecutionGas (state : MachineState) : NonExecutionGas :=
  { stateReservoir := state.gas.stateReservoir
    stateFromGasLeft := state.gas.stateFromGasLeft
    stateUsed := state.gas.stateUsed
    refundCounter := state.gas.refundCounter }

theorem debit_execution_preserves_nonexecution_gas (amount : Nat) (state after : MachineState)
    (h : debitExecution amount state = .paid after) :
    nonExecutionGas after = nonExecutionGas state := by
  unfold debitExecution at h
  cases hCharge : chargeExecution amount state.gas with
  | error error => simp [hCharge] at h
  | ok gas =>
    simp [hCharge] at h
    subst after
    unfold chargeExecution at hCharge
    split at hCharge
    · cases hCharge
      rfl
    · simp_all

theorem state_oog_retains_precharge_state (amount : Nat) (state after : MachineState)
    (h : debitState amount state = .outOfGas after) : after = state := by
  unfold debitState at h
  cases hCharge : chargeState amount state.gas with
  | error error => simp [hCharge] at h; exact h.symm
  | ok gas => simp [hCharge] at h

theorem dispatch_start_is_pc_first (table : DispatchTable)
    (opcode : Eip803x.Evm.CallCreateExecution.Opcode)
    (state : MachineState) :
    (beginInstruction table opcode state).pc = state.pc + 1 /\
      (beginInstruction table opcode state).opcodeCount = state.opcodeCount + 1 := by
  cases table <;> simp [beginInstruction, tableTracing, appendTrace]

theorem create_collision_result_event_has_no_second_finish (state : MachineState)
    (tail : EvmStack) (hPush : state.stack.push Eip803x.Evm.Word.zero = some tail) :
    ∃ payload, (pushZeroAfterOwnedFinish .traced state).state.trace =
      state.trace ++ [.stackPush payload] := by
  refine ⟨[0], ?_⟩
  simp [pushZeroAfterOwnedFinish, pushResult, hPush, appendTrace, tableTracing, fail]

theorem ordinary_result_event_precedes_instruction_finish (state : MachineState)
    (tail : EvmStack) (hPush : state.stack.push Eip803x.Evm.Word.zero = some tail) :
    ∃ payload gasLeft, (pushZero .traced state).state.trace =
      state.trace ++ [.stackPush payload, .instructionFinish gasLeft] := by
  refine ⟨[0], state.gas.gasLeft, ?_⟩
  simp [pushZero, pushResult, hPush, finishInstruction, appendTrace, fail,
    tableTracing, List.append_assoc]

theorem create_success_result_event_precedes_instruction_finish (state : MachineState)
    (address : Address) (tail : EvmStack) (hPush : state.stack.push address = some tail) :
    ∃ payload gasLeft, (pushAddress .traced address state).state.trace =
      state.trace ++ [.stackPush payload, .instructionFinish gasLeft] := by
  refine ⟨(Eip803x.Evm.MemoryStackControl.wordToBytes address).drop 12,
    state.gas.gasLeft, ?_⟩
  simp [pushAddress, pushResult, hPush, finishInstruction, appendTrace, fail,
    tableTracing, List.append_assoc]

theorem call_suspend_finishes_instruction_before_action_start
    (label : String) (frame : ChildFrame) (state : MachineState)
    (hActions : state.traceActions = true) :
    ∃ finishGas actionLabel actionGas actionTarget,
      (startChildAction label frame (finishInstruction .traced state)).trace =
        state.trace ++
          [.instructionFinish finishGas,
            .actionStart actionLabel actionGas actionTarget] := by
  refine ⟨state.gas.gasLeft, label, frame.gasEntry.child.gas.gasLeft, frame.target, ?_⟩
  simp [startChildAction, finishInstruction, appendTrace, tableTracing,
    hActions, List.append_assoc]

theorem exceptional_action_error_precedes_restored_world
    (child : ChildOutcome) (frame : ChildFrame) (state : MachineState)
    (hExit : child.exit = .exceptional) (hActions : state.traceActions = true) :
    (∃ status, (restoreFailureWorld frame (endChildAction child frame state)).trace =
        state.trace ++ [.actionError status]) /\
      (restoreFailureWorld frame (endChildAction child frame state)).world = frame.snapshot /\
      (restoreFailureWorld frame (endChildAction child frame state)).journal = frame.journalSnapshot := by
  refine ⟨⟨child.exceptionStatus, ?_⟩, ?_, ?_⟩ <;>
    simp [endChildAction, restoreFailureWorld, hExit, hActions, appendTrace]

theorem resumed_output_copy_is_after_result_trace
    (frame : ChildFrame) (output : List EvmByte) (result : Outcome)
    (hContinued : result.status = .continued) :
    (writeReturnedOutput frame output result).state.trace = result.state.trace := by
  by_cases hLength : frame.outputLength = 0 <;>
    simp [writeReturnedOutput, hContinued, hLength]

theorem selfdestruct_new_account_predicate_refines (facts : WorldFacts)
    (input : Eip803x.Evm.SelfDestruct.Situation)
    (hBalance : input.sourceBalance = facts.callerBalance)
    (hDead : input.beneficiaryDead = facts.beneficiaryDead) :
    selfDestructNeedsNewAccount facts =
      Eip803x.Evm.SelfDestruct.needsNewAccountCharge input := by
  simp [selfDestructNeedsNewAccount,
    Eip803x.Evm.SelfDestruct.needsNewAccountCharge, hBalance, hDead]

end Eip803x.Generated.CallCreateOpcodeRefinement
