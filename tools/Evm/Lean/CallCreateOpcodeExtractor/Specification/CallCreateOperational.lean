-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import CallCreateOpcodeExtractor.Specification.CallCreateExecution
import Eip803x.Evm.CallCreateFrame
import Eip803x.Evm.SelfDestruct

namespace Eip803x
namespace Evm
namespace CallCreateExecution
namespace Reference

open Eip803x.GasMachine
open Eip803x.Evm.MemoryStackControl.Stack

/-!
Handwritten opcode-layer reference for the seven Amsterdam CALL/CREATE routes.
It shares representation and gas-algebra types with the generated kernel, but
does not import or invoke that kernel. `CallCreateFrame` and `SelfDestruct`
remain the independently maintained lower-level checks used by the refinement
module for frame gas, refill, deposit, and SELFDESTRUCT pricing projections.
-/

abbrev EvmStack := Eip803x.Evm.MemoryStackControl.Stack
abbrev EvmMemory := Eip803x.Evm.MemoryStackControl.Memory
abbrev EvmByte := Eip803x.Evm.MemoryStackControl.Byte

def amsterdamSchedule : Schedule :=
  {
    callBase := 0, callValue := 11300, callStipend := 2300
    warmAccess := 100, coldAccess := 3000
    createAccess := 12000, initCodeWord := 2, create2HashWord := 6
    accountWrite := 9000, selfDestructBase := 5000
    newAccountState := 183600, createState := 183600
    codeDepositExecutionPerWord := 6, codeDepositStatePerByte := 1530
    memoryLinear := 3, memoryQuadraticDivisor := 512
    maxMemorySize := 2147483616, maxInitCodeSize := 131072, maxCodeSize := 24576
    maxCallDepth := 1024, stackLimit := 1024
  }

def referenceTableTracing : DispatchTable -> Bool
  | .noTrace | .noTraceCancelable => false
  | .traced | .tracedCancelable => true

def recordEvent (event : TraceEvent) (state : MachineState) : MachineState :=
  { state with trace := state.trace ++ [event] }

def enterInstruction (table : DispatchTable) (opcode : Opcode)
    (state : MachineState) : MachineState :=
  let traced :=
    if referenceTableTracing table then
      recordEvent (.instructionStart opcode state.pc state.gas.gasLeft) state
    else state
  { traced with
    pc := traced.pc + 1
    opcodeCount := traced.opcodeCount + 1
    stagedChild := none }

def endInstruction (table : DispatchTable) (state : MachineState) : MachineState :=
  if referenceTableTracing table then
    recordEvent (.instructionFinish state.gas.gasLeft) state
  else state

def outcome (status : Status) (state : MachineState) : Outcome := ⟨status, state⟩

def burnExecution (state : MachineState) : MachineState :=
  { state with gas := { state.gas with gasLeft := 0 } }

def payExecution (amount : Nat) (state : MachineState) : Debit :=
  match chargeExecution amount state.gas with
  | .error _ => .outOfGas (burnExecution state)
  | .ok gas => .paid { state with gas := gas }

def payState (amount : Nat) (state : MachineState) : Debit :=
  match chargeState amount state.gas with
  | .error _ => .outOfGas state
  | .ok gas => .paid { state with gas := gas }

def allocatedWords (memory : EvmMemory) : Nat := memory.bytes.length / 32

def memoryCost (schedule : Schedule) (words : Nat) : Nat :=
  words * schedule.memoryLinear + words * words / schedule.memoryQuadraticDivisor

def validRange (schedule : Schedule) (offset length : UInt256) : Bool :=
  length.val = 0 ||
    (length.val <= schedule.maxMemorySize && offset.val <= schedule.maxMemorySize - length.val)

def expandMemory (schedule : Schedule) (memory : EvmMemory)
    (offset length : UInt256) : Expansion :=
  if length.val = 0 then .prepared 0 memory
  else if validRange schedule offset length then
    let expanded := memory.expand offset.val length.val
    .prepared
      (memoryCost schedule (allocatedWords expanded) - memoryCost schedule (allocatedWords memory))
      expanded
  else .invalid

def payExpansion (schedule : Schedule) (offset length : UInt256)
    (state : MachineState) : Debit :=
  match expandMemory schedule state.memory offset length with
  | .invalid => .outOfGas state
  | .prepared cost memory => payExecution cost { state with memory := memory }

def addressWarm (address : Address) (state : MachineState) : Bool :=
  address ∈ state.journal.warmAddresses

def markWarm (address : Address) (state : MachineState) : MachineState :=
  if addressWarm address state then state
  else
    { state with journal :=
        { state.journal with warmAddresses := state.journal.warmAddresses ++ [address] } }

def payAccess (schedule : Schedule) (address : Address) (state : MachineState) : Debit :=
  let observed := if state.traceAccess then markWarm address state else state
  let cost := if addressWarm address observed then schedule.warmAccess else schedule.coldAccess
  payExecution cost (markWarm address observed)

def pushResult (table : DispatchTable) (value : UInt256) (payload : List EvmByte)
    (ownsFinish : Bool) (state : MachineState) : Outcome :=
  match state.stack.push value with
  | none => outcome .stackOverflow state
  | some stack =>
      let pushed := { state with stack := stack }
      let traced :=
        if referenceTableTracing table then recordEvent (.stackPush payload) pushed else pushed
      outcome .continued (if ownsFinish then endInstruction table traced else traced)

def pushZero (table : DispatchTable) (state : MachineState) : Outcome :=
  pushResult table Eip803x.Evm.Word.zero [0] true state

def pushOne (table : DispatchTable) (state : MachineState) : Outcome :=
  pushResult table (Eip803x.Evm.Word.ofNat 1) [1] true state

def pushAddress (table : DispatchTable) (address : Address) (state : MachineState) : Outcome :=
  pushResult table address ((Eip803x.Evm.MemoryStackControl.wordToBytes address).drop 12) true state

def pushCollisionZero (table : DispatchTable) (state : MachineState) : Outcome :=
  pushResult table Eip803x.Evm.Word.zero [0] false state

def beginChildAction (label : String) (frame : ChildFrame)
    (state : MachineState) : MachineState :=
  if state.traceActions then
    recordEvent (.actionStart label frame.gasEntry.child.gas.gasLeft frame.target) state
  else state

def endChildAction (child : ChildOutcome) (frame : ChildFrame)
    (state : MachineState) : MachineState :=
  if !state.traceActions then state
  else
    match child.exit with
    | .exceptional => recordEvent (.actionError child.exceptionStatus) state
    | .revert => recordEvent (.actionRevert child.gas.gas.gasLeft child.output) state
    | .success => recordEvent (.actionEnd child.gas.gas.gasLeft frame.target child.output) state

def addressOfWord (word : UInt256) : Address :=
  Eip803x.Evm.Word.ofNat (word.val % (2 ^ 160))

def popCallTail (stack : EvmStack) (requestedGas codeSource value : UInt256) :
    Option CallOperands × EvmStack :=
  match stack.pop with
  | none => (none, stack)
  | some (inputOffset, afterInputOffset) =>
    match afterInputOffset.pop with
    | none => (none, afterInputOffset)
    | some (inputLength, afterInputLength) =>
      match afterInputLength.pop with
      | none => (none, afterInputLength)
      | some (outputOffset, afterOutputOffset) =>
        match afterOutputOffset.pop with
        | none => (none, afterOutputOffset)
        | some (outputLength, tail) =>
          (some { requestedGas, codeSource, value, inputOffset, inputLength, outputOffset, outputLength }, tail)

def popCall (kind : CallKind) (environmentValue : UInt256) (stack : EvmStack) :
    Option CallOperands × EvmStack :=
  match stack.pop with
  | none => (none, stack)
  | some (requestedGas, afterGas) =>
    match afterGas.pop with
    | none => (none, afterGas)
    | some (codeSourceWord, afterSource) =>
      let codeSource := addressOfWord codeSourceWord
      match kind with
      | .call | .callcode =>
        match afterSource.pop with
        | none => (none, afterSource)
        | some (value, afterValue) =>
          popCallTail afterValue requestedGas codeSource value
      | .delegatecall => popCallTail afterSource requestedGas codeSource environmentValue
      | .staticcall => popCallTail afterSource requestedGas codeSource Eip803x.Evm.Word.zero

def asCallKind : Opcode → Option CallKind
  | .call => some .call
  | .callcode => some .callcode
  | .delegatecall => some .delegatecall
  | .staticcall => some .staticcall
  | _ => none

def transfersValue (kind : CallKind) (value : UInt256) : Bool :=
  (kind == .call || kind == .callcode) && value.val != 0

def createsAccount (kind : CallKind) (value : UInt256) (targetDead : Bool) : Bool :=
  kind == .call && value.val != 0 && targetDead

def stateTarget (kind : CallKind) (operands : CallOperands)
    (state : MachineState) : Address :=
  match kind with
  | .call | .staticcall => operands.codeSource
  | .callcode | .delegatecall => state.environment.executingAccount

def directPrecompileAllowed (codeSource : Address) : Bool :=
  (addressOfWord codeSource).val != 3

def validInlineGas (entry : FrameEntry) (oracle : HandlerOracle) : Bool :=
  oracle.precompileGasRemaining <= entry.child.gas.gasLeft

def includeStipend (amount : Nat) (entry : FrameEntry) : FrameEntry :=
  { entry with child :=
      { entry.child with gas :=
          { entry.child.gas with gasLeft := entry.child.gas.gasLeft + amount } } }

def refundReservation (entry : FrameEntry) : GasState := mergeSuccess entry entry.child

def inspectMemory (memory : EvmMemory) (offset length : Nat) : List EvmByte :=
  if offset + length <= memory.bytes.length then (memory.bytes.drop offset).take length else []

def completeBlockedCall (schedule : Schedule) (table : DispatchTable)
    (operands : CallOperands) (entry : FrameEntry) (chargedNew : Bool)
    (state : MachineState) : Outcome :=
  let reserved := { state with gas := entry.pausedParent, returnData := [] }
  let pushed : Outcome :=
    match reserved.stack.push Eip803x.Evm.Word.zero with
    | none => outcome .stackOverflow reserved
    | some stack =>
      let withStack := { reserved with stack := stack }
      let traced :=
        if referenceTableTracing table then recordEvent (.stackPush [0]) withStack else withStack
      outcome .continued traced
  let inspected :=
    if pushed.state.traceRefunds then
      recordEvent
        (.memoryInspect operands.inputOffset.val
          (inspectMemory pushed.state.memory operands.inputOffset.val 32))
        pushed.state
    else pushed.state
  let preRefundTrace :=
    if referenceTableTracing table then
      recordEvent (.instructionError .notEnoughBalance) (endInstruction table inspected)
    else inspected
  let returned := refundReservation entry
  let gas := if chargedNew then refillState schedule.newAccountState returned else returned
  let refunded := { preRefundTrace with gas := gas }
  let gasTraced :=
    if referenceTableTracing table then
      recordEvent (.gasUpdate entry.child.gas.gasLeft gas.gasLeft) refunded
    else refunded
  if pushed.status = .continued then outcome .continued (endInstruction table gasTraced)
  else { pushed with state := gasTraced }

def stageCall (kind : CallKind) (operands : CallOperands) (target caller : Address)
    (oracle : HandlerOracle) (chargedNew : Bool) (entry : FrameEntry)
    (table : DispatchTable) (state : MachineState) : Outcome :=
  let (_, input) := state.memory.readRange operands.inputOffset.val operands.inputLength.val
  let destination := if operands.outputLength.val = 0 then 0 else operands.outputOffset.val
  let frame : ChildFrame :=
    { opcode :=
        match kind with
        | .call => .call
        | .callcode => .callcode
        | .delegatecall => .delegatecall
        | .staticcall => .staticcall
      gasEntry := entry, target := target, codeSource := operands.codeSource, caller := caller,
      value := operands.value, input := input, callDepth := state.environment.callDepth + 1,
      isStatic := kind == .staticcall || state.environment.isStatic,
      outputOffset := destination, outputLength := operands.outputLength.val,
      snapshot := oracle.rollbackWorld, journalSnapshot := state.journal,
      isPrecompile := oracle.facts.targetCodeRoute == .precompile,
      newAccountCharged := chargedNew, createStateCharged := false,
      createOnPhysicalAccount := false }
  let staged :=
    { state with
      gas := entry.pausedParent
      stagedChild := some frame
      world := oracle.nextWorld
      journal := oracle.nextJournal }
  let label :=
    match kind with
    | .call => "CALL"
    | .callcode => "CALLCODE"
    | .delegatecall => "DELEGATECALL"
    | .staticcall => "STATICCALL"
  outcome .suspended (beginChildAction label frame (endInstruction table staged))

def routeCall (schedule : Schedule) (table : DispatchTable) (kind : CallKind)
    (operands : CallOperands) (oracle : HandlerOracle) (chargedNew : Bool)
    (state : MachineState) : Outcome :=
  let baseEntry := enterFrame operands.requestedGas.val state.gas
  let entry := if transfersValue kind operands.value then
    includeStipend schedule.callStipend baseEntry else baseEntry
  let stipendTraced :=
    if transfersValue kind operands.value && state.traceRefunds then
      recordEvent (.extraGasPressure schedule.callStipend) state
    else state
  let target := stateTarget kind operands state
  let caller := if kind = .delegatecall then
    state.environment.caller else state.environment.executingAccount
  if state.environment.callDepth >= schedule.maxCallDepth ||
      (transfersValue kind operands.value &&
        oracle.facts.callerBalance < operands.value.val) then
    completeBlockedCall schedule table operands entry chargedNew stipendTraced
  else
    match oracle.facts.targetCodeRoute with
    | .empty | .delegatedEmpty =>
      if !referenceTableTracing table && !state.traceActions then
        let reserved := { stipendTraced with gas := entry.pausedParent, returnData := [] }
        match reserved.stack.push (Eip803x.Evm.Word.ofNat 1) with
        | none => outcome .stackOverflow reserved
        | some stack => outcome .continued
          { reserved with
            stack := stack
            gas := refundReservation entry
            world := oracle.nextWorld
            journal := oracle.nextJournal }
      else stageCall kind operands target caller oracle chargedNew entry table stipendTraced
    | .precompile =>
      if kind == .staticcall && !referenceTableTracing table && !state.traceActions &&
          directPrecompileAllowed operands.codeSource then
        let child :=
          { entry.child with gas :=
              { entry.child.gas with gasLeft := oracle.precompileGasRemaining } }
        if !validInlineGas entry oracle then outcome .oracleMismatch stipendTraced
        else if oracle.precompileSuccess then
          let clipped := oracle.precompileOutput.take operands.outputLength.val
          let completed :=
            { stipendTraced with
              gas := mergeSuccess entry child
              memory := stipendTraced.memory.writeRange operands.outputOffset.val clipped
              returnData := oracle.precompileOutput
              world := oracle.nextWorld
              journal := oracle.nextJournal }
          pushOne table completed
        else
          pushZero table
            { stipendTraced with gas := mergeException entry child, returnData := [] }
      else stageCall kind operands target caller oracle chargedNew entry table stipendTraced
    | .bytecode | .delegatedBytecode =>
      stageCall kind operands target caller oracle chargedNew entry table stipendTraced

def afterCallAccesses (schedule : Schedule) (table : DispatchTable) (kind : CallKind)
    (operands : CallOperands) (oracle : HandlerOracle) (state : MachineState) : Outcome :=
  match payAccess schedule operands.codeSource state with
  | .outOfGas failed => outcome .outOfGas failed
  | .paid sourceCharged =>
    let delegated : Debit :=
      match oracle.facts.delegatedAddress with
      | none => .paid sourceCharged
      | some address => payAccess schedule address sourceCharged
    match delegated with
    | .outOfGas failed => outcome .outOfGas failed
    | .paid accessed =>
      let chargedNew := createsAccount kind operands.value oracle.facts.targetDead
      if chargedNew then
        match payState schedule.newAccountState accessed with
        | .outOfGas failed => outcome .outOfGas failed
        | .paid charged => routeCall schedule table kind operands oracle true charged
      else routeCall schedule table kind operands oracle false accessed

def executeCall (schedule : Schedule) (table : DispatchTable) (kind : CallKind)
    (oracle : HandlerOracle) (entered : MachineState) : Outcome :=
  let (operands?, tail) := popCall kind entered.environment.value entered.stack
  match operands? with
  | none => outcome .stackUnderflow { entered with stack := tail }
  | some operands =>
    let popped := { entered with stack := tail }
    if entered.environment.isStatic && transfersValue kind operands.value &&
        kind != .callcode then
      outcome .staticViolation popped
    else
      let valueDebit : Debit :=
        if transfersValue kind operands.value then payExecution schedule.callValue popped
        else .paid popped
      match valueDebit with
      | .outOfGas failed => outcome .outOfGas failed
      | .paid valueCharged =>
        match payExecution schedule.callBase valueCharged with
        | .outOfGas failed => outcome .outOfGas failed
        | .paid baseCharged =>
          match payExpansion schedule operands.inputOffset operands.inputLength baseCharged with
          | .outOfGas failed => outcome .outOfGas failed
          | .paid inputExpanded =>
            match payExpansion schedule operands.outputOffset operands.outputLength inputExpanded with
            | .outOfGas failed => outcome .outOfGas failed
            | .paid outputExpanded =>
              afterCallAccesses schedule table kind operands oracle outputExpanded

def popCreate (kind : CreateKind) (stack : EvmStack) : Option CreateOperands × EvmStack :=
  match stack.pop with
  | none => (none, stack)
  | some (value, afterValue) =>
    match afterValue.pop with
    | none => (none, afterValue)
    | some (initOffset, afterOffset) =>
      match afterOffset.pop with
      | none => (none, afterOffset)
      | some (initLength, afterLength) =>
        match kind with
        | .create => (some { value, initOffset, initLength, salt := none }, afterLength)
        | .create2 =>
          match afterLength.pop with
          | none => (none, afterLength)
          | some (salt, tail) =>
            (some { value, initOffset, initLength, salt := some salt }, tail)

def initCodeWords (length : UInt256) : Option Nat :=
  if length.val < 2 ^ 64 then some ((length.val + 31) / 32) else none

def createEntryCost (schedule : Schedule) (kind : CreateKind) (words : Nat) : Nat :=
  schedule.createAccess + schedule.initCodeWord * words +
    (if kind = .create2 then schedule.create2HashWord * words else 0)

def finishCreateWithoutChild (table : DispatchTable) (state : MachineState) : Outcome :=
  pushZero table { state with returnData := [] }

def validCreateFacts (facts : WorldFacts) : Bool :=
  decide
    ((facts.createLogicalExists -> facts.createPhysicalExists) /\
      (facts.createCollision != .none -> facts.createPhysicalExists) /\
      ((facts.createCollision = .code || facts.createCollision = .nonce) ->
        facts.createLogicalExists))

def stageCreate (kind : CreateKind) (operands : CreateOperands)
    (oracle : HandlerOracle) (chargedState physical : Bool) (entry : FrameEntry)
    (state : MachineState) : Outcome :=
  let (_, input) := state.memory.readRange operands.initOffset.val operands.initLength.val
  let frame : ChildFrame :=
    { opcode := if kind = .create then .create else .create2
      gasEntry := entry
      target := oracle.derivedCreateAddress
      codeSource := oracle.derivedCreateAddress
      caller := state.environment.executingAccount
      value := operands.value
      input := input
      callDepth := state.environment.callDepth + 1
      isStatic := false
      outputOffset := 0
      outputLength := 0
      snapshot := oracle.rollbackWorld
      journalSnapshot := state.journal
      isPrecompile := false
      newAccountCharged := false
      createStateCharged := chargedState
      createOnPhysicalAccount := physical }
  let staged :=
    { state with
      gas := entry.pausedParent
      stagedChild := some frame
      world := oracle.nextWorld
      journal := oracle.nextJournal }
  outcome .suspended
    (beginChildAction (if kind = .create then "CREATE" else "CREATE2") frame staged)

def afterCreateChecks (schedule : Schedule) (table : DispatchTable)
    (kind : CreateKind) (operands : CreateOperands) (oracle : HandlerOracle)
    (state : MachineState) : Outcome :=
  if !validCreateFacts oracle.facts then outcome .oracleMismatch state
  else
    let warmed := markWarm oracle.derivedCreateAddress state
    let chargedState := !oracle.facts.createLogicalExists
    let stateDebit : Debit :=
      if chargedState then payState schedule.createState warmed else .paid warmed
    match stateDebit with
    | .outOfGas failed => outcome .outOfGas failed
    | .paid charged =>
      let preEnded := endInstruction table charged
      let entry := enterFrame preEnded.gas.gasLeft preEnded.gas
      let nonceAndSnapshot :=
        { preEnded with gas := entry.pausedParent, world := oracle.rollbackWorld }
      if oracle.facts.createCollision != .none then
        let gas :=
          if chargedState then refillState schedule.createState entry.pausedParent
          else entry.pausedParent
        pushCollisionZero table { nonceAndSnapshot with gas := gas, returnData := [] }
      else
        stageCreate kind operands oracle chargedState oracle.facts.createPhysicalExists
          entry nonceAndSnapshot

def executeCreate (schedule : Schedule) (table : DispatchTable) (kind : CreateKind)
    (oracle : HandlerOracle) (entered : MachineState) : Outcome :=
  if entered.environment.isStatic then outcome .staticViolation entered
  else
    let (operands?, tail) := popCreate kind entered.stack
    match operands? with
    | none => outcome .stackUnderflow { entered with stack := tail }
    | some operands =>
      let popped := { entered with stack := tail }
      if operands.initLength.val > schedule.maxInitCodeSize then
        outcome .outOfGas (burnExecution popped)
      else
        match initCodeWords operands.initLength with
        | none => outcome .outOfGas (burnExecution popped)
        | some words =>
          match payExecution (createEntryCost schedule kind words) popped with
          | .outOfGas failed => outcome .outOfGas failed
          | .paid createCharged =>
            match payExpansion schedule operands.initOffset operands.initLength createCharged with
            | .outOfGas failed => outcome .outOfGas failed
            | .paid expanded =>
              if expanded.environment.callDepth >= schedule.maxCallDepth then
                finishCreateWithoutChild table expanded
              else if !oracle.initCodeReadable then
                outcome .outOfGas (burnExecution expanded)
              else if oracle.facts.callerBalance < operands.value.val ||
                  oracle.facts.creatorNonce >= 2 ^ 64 - 1 then
                finishCreateWithoutChild table expanded
              else afterCreateChecks schedule table kind operands oracle expanded

def selfDestructNeedsNewAccount (facts : WorldFacts) : Bool :=
  facts.callerBalance > 0 && facts.beneficiaryDead

def executeSelfDestruct (schedule : Schedule) (_table : DispatchTable)
    (oracle : HandlerOracle) (entered : MachineState) : Outcome :=
  if entered.environment.isStatic then outcome .staticViolation entered
  else
    match payExecution schedule.selfDestructBase entered with
    | .outOfGas failed => outcome .outOfGas failed
    | .paid baseCharged =>
      match baseCharged.stack.pop with
      | none => outcome .stackUnderflow baseCharged
      | some (beneficiaryWord, tail) =>
        let beneficiary := addressOfWord beneficiaryWord
        let popped := { baseCharged with stack := tail }
        match payAccess schedule beneficiary popped with
        | .outOfGas failed => outcome .outOfGas failed
        | .paid accessed =>
          let destroy :=
            if oracle.facts.createdInTransaction then
              { accessed.journal with destroyList :=
                  accessed.journal.destroyList ++ [accessed.environment.executingAccount] }
            else accessed.journal
          let marked := { accessed with journal := destroy }
          let actionTraced :=
            if marked.traceActions then
              recordEvent
                (.selfdestruct marked.environment.executingAccount beneficiary
                  (Eip803x.Evm.Word.ofNat oracle.facts.callerBalance))
                marked
            else marked
          let needsNew := selfDestructNeedsNewAccount oracle.facts
          let writeDebit : Debit :=
            if needsNew then payExecution schedule.accountWrite actionTraced
            else .paid actionTraced
          match writeDebit with
          | .outOfGas failed => outcome .outOfGas failed
          | .paid writeCharged =>
            let stateDebit : Debit :=
              if needsNew then payState schedule.newAccountState writeCharged
              else .paid writeCharged
            match stateDebit with
            | .outOfGas failed => outcome .outOfGas failed
            | .paid final =>
              outcome .stopped
                { final with world := oracle.nextWorld, journal := oracle.nextJournal }

def executeCore (schedule : Schedule) (table : DispatchTable) (opcode : Opcode)
    (oracle : HandlerOracle) (state : MachineState) : Outcome :=
  let entered := enterInstruction table opcode state
  match asCallKind opcode with
  | some kind => executeCall schedule table kind oracle entered
  | none =>
    match opcode with
    | .create => executeCreate schedule table .create oracle entered
    | .create2 => executeCreate schedule table .create2 oracle entered
    | .selfdestruct => executeSelfDestruct schedule table oracle entered
    | _ => outcome .extractionMismatch entered

def closeFailure (table : DispatchTable) (result : Outcome) : Outcome :=
  if result.status == .continued || result.status == .suspended then result
  else
    let state := if result.status = .outOfGas then burnExecution result.state else result.state
    if referenceTableTracing table then
      let finished := endInstruction table state
      if result.status = .stopped then { result with state := finished }
      else { result with state := recordEvent (.instructionError result.status) finished }
    else { result with state := state }

def executeAmsterdam (table : DispatchTable) (opcode : Opcode)
    (oracle : HandlerOracle) (state : MachineState) : Outcome :=
  closeFailure table (executeCore amsterdamSchedule table opcode oracle state)

def depositCost (schedule : Schedule) (output : List EvmByte) : Option (Nat × Nat) :=
  if output.length <= schedule.maxCodeSize then
    some (schedule.codeDepositExecutionPerWord * ((output.length + 31) / 32),
      schedule.codeDepositStatePerByte * output.length)
  else none

def restoreChildFailure (frame : ChildFrame) (state : MachineState) : MachineState :=
  { state with
    world := frame.snapshot
    journal := frame.journalSnapshot
    returnData := []
    stagedChild := none }

def mergeBeforeRepayment (entry : FrameEntry) (child : FrameGasState) : GasState :=
  { child.gas with
    gasLeft := entry.pausedParent.gasLeft + child.gas.gasLeft
    stateReservoir := entry.pausedParent.stateReservoir + child.gas.stateReservoir
    stateFromGasLeft := entry.pausedParent.stateFromGasLeft + child.gas.stateFromGasLeft }

def createDepositOrder : List CreateDepositPhase :=
  [.refundChild, .chargeParentExecution, .chargeParentState, .commitChild, .repayStateSpill]

def convertRefundToHalt (entry : FrameEntry) (child : FrameGasState) : GasState :=
  let restoredChild : FrameGasState :=
    { child with gas :=
        { child.gas with
          gasLeft := 0
          stateReservoir := child.stateGasBaseline
          stateFromGasLeft := 0
          stateUsed := child.stateUsedBaseline
          refundCounter := child.refundCounterBaseline } }
  repayStateFromGasLeft (mergeBeforeRepayment entry restoredChild)

def payFrameExecution (amount : Nat) (state : FrameGasState) : FrameDebit :=
  match chargeExecution amount state.gas with
  | .error _ => .outOfGas state
  | .ok gas => .paid { state with gas := gas }

def payFrameState (amount : Nat) (state : FrameGasState) : FrameDebit :=
  match chargeState amount state.gas with
  | .error _ => .outOfGas state
  | .ok gas => .paid { state with gas := gas }

def reportPrecompileOutput (table : DispatchTable) (frame : ChildFrame)
    (outputBytes : List EvmByte) (state : MachineState) : MachineState :=
  if referenceTableTracing table && frame.isPrecompile then
    recordEvent (.memoryWrite frame.outputOffset (outputBytes.take frame.outputLength)) state
  else state

def writeReturnedOutput (frame : ChildFrame) (outputBytes : List EvmByte)
    (result : Outcome) : Outcome :=
  if result.status = .continued && frame.outputLength != 0 then
    { result with state :=
        { result.state with
          memory := result.state.memory.writeRange frame.outputOffset
            (outputBytes.take frame.outputLength) } }
  else result

def createDepositFailureGas (schedule : Schedule) (frame : ChildFrame)
    (childGas : FrameGasState) : GasState :=
  let merged := convertRefundToHalt frame.gasEntry childGas
  if frame.createStateCharged then refillState schedule.createState merged else merged

def failCreateDeposit (schedule : Schedule) (table : DispatchTable)
    (frame : ChildFrame) (status : Status) (childGas : FrameGasState)
    (state : MachineState) : Outcome :=
  let restored :=
    restoreChildFailure frame { state with gas := createDepositFailureGas schedule frame childGas }
  let traced :=
    if restored.traceActions then recordEvent (.actionError status) restored else restored
  pushZero table traced

def completeCreateSuccess (schedule : Schedule) (table : DispatchTable)
    (frame : ChildFrame) (child : ChildOutcome) (state : MachineState) : Outcome :=
  let kindConsistent :=
    match child.runtimeCodeKind with
    | .empty => child.output.isEmpty
    | .valid | .invalid => !child.output.isEmpty
  if !kindConsistent then outcome .oracleMismatch state
  else
    let refunded :=
      { state with
        gas := mergeBeforeRepayment frame.gasEntry child.gas
        world := child.world
        returnData := []
        stagedChild := none }
    let cost := depositCost schedule child.output
    if child.runtimeCodeKind = .invalid then
      failCreateDeposit schedule table frame .invalidCode child.gas refunded
    else
      match cost with
      | none => failCreateDeposit schedule table frame .outOfGas child.gas refunded
      | some (executionCost, stateCost) =>
        match payFrameExecution executionCost child.gas with
        | .outOfGas _ =>
          failCreateDeposit schedule table frame .outOfGas child.gas refunded
        | .paid executionPreview =>
          match payFrameState stateCost executionPreview with
          | .outOfGas _ =>
            failCreateDeposit schedule table frame .outOfGas child.gas refunded
          | .paid depositedPreview =>
            match payExecution executionCost refunded with
            | .outOfGas _ => outcome .oracleMismatch state
            | .paid executionCharged =>
              match payState stateCost executionCharged with
              | .outOfGas _ => outcome .oracleMismatch state
              | .paid stateCharged =>
                let depositedOutcome := { child with gas := depositedPreview }
                let committed := { stateCharged with journal := child.journal }
                let repaid := { committed with gas := repayStateFromGasLeft committed.gas }
                pushAddress table frame.target (endChildAction depositedOutcome frame repaid)

def validChildGas (frame : ChildFrame) (child : ChildOutcome) : Bool :=
  decide
    (child.gas.stateGasBaseline = frame.gasEntry.child.stateGasBaseline /\
      child.gas.stateUsedBaseline = frame.gasEntry.child.stateUsedBaseline /\
      child.gas.refundCounterBaseline = frame.gasEntry.child.refundCounterBaseline /\
      child.gas.gas.gasLeft <= frame.gasEntry.child.gas.gasLeft /\
      child.gas.gas.stateFromGasLeft <= child.gas.gas.stateUsed /\
      child.gas.gas.stateUsed + child.gas.gas.stateReservoir =
        child.gas.stateUsedBaseline + child.gas.stateGasBaseline +
          child.gas.gas.stateFromGasLeft)

def resumeChild (schedule : Schedule) (table : DispatchTable) (child : ChildOutcome)
    (state : MachineState) : Outcome :=
  match state.stagedChild with
  | none => outcome .oracleMismatch state
  | some frame =>
    if !validChildGas frame child then outcome .oracleMismatch state
    else
      let isCreate := frame.opcode == .create || frame.opcode == .create2
      let refilled (gas : GasState) :=
        if frame.createStateCharged then refillState schedule.createState gas
        else if frame.newAccountCharged then refillState schedule.newAccountState gas
        else gas
      match child.exit with
      | .exceptional =>
        let gasMerged :=
          { state with gas := refilled (mergeException frame.gasEntry child.gas) }
        let actionTraced := endChildAction child frame gasMerged
        pushZero table (restoreChildFailure frame actionTraced)
      | .revert =>
        let restored :=
          restoreChildFailure frame
            { state with gas := refilled (mergeRevert frame.gasEntry child.gas) }
        let actionTraced :=
          endChildAction child frame { restored with returnData := child.output }
        let pushed := pushZero table actionTraced
        if isCreate then pushed else writeReturnedOutput frame child.output pushed
      | .success =>
        if isCreate then completeCreateSuccess schedule table frame child state
        else
          let merged :=
            { state with
              gas := mergeSuccess frame.gasEntry child.gas
              world := child.world
              journal := child.journal
              returnData := child.output
              stagedChild := none }
          let memoryTraced := reportPrecompileOutput table frame child.output merged
          let pushed := pushOne table (endChildAction child frame memoryTraced)
          writeReturnedOutput frame child.output pushed

def resumeAmsterdam (table : DispatchTable) (child : ChildOutcome)
    (state : MachineState) : Outcome :=
  resumeChild amsterdamSchedule table child state

end Reference
end CallCreateExecution
end Evm
end Eip803x
