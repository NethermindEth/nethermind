-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Lean.Elab.Tactic.Omega
import Eip803x.Evm.Word

/-!
  A deliberately bounded, executable reference semantics for the Amsterdam EVM
  stack/memory/control-flow slice.  It models the specified opcodes in this
  module only; unknown bytecode is reported as `unmodeledOpcode`, rather than
  being misrepresented as an EVM `INVALID` instruction.

  `GasSchedule` currently contains only the EIP-8037/8038 quantities.  This
  module therefore carries `gasLeft` for the observable `GAS` result but does
  not debit any opcode or memory-expansion gas.  A future exact gas connection
  needs a named extension covering base/very-low/mid/high costs, copy-word
  cost, memory-linear cost, and the memory-quadratic divisor.  No duplicate
  concrete gas constants are introduced here.
-/

namespace Eip803x
namespace Evm
namespace MemoryStackControl

open Word

abbrev Byte := Fin 256

def byte (value : Nat) : Byte :=
  ⟨value % 256, Nat.mod_lt _ (by decide)⟩

def zeroByte : Byte := byte 0

@[simp] theorem zeroByte_value : zeroByte.val = 0 := by native_decide

/--
The still-unpinned EVM-opcode part of the gas schedule required before this
reference can debit its transitions.  This is intentionally a field-only
extension: unlike `GasSchedule.amsterdam`, it has no concrete instance here.
-/
structure RequiredEvmGasScheduleExtension where
  base : Nat
  veryLow : Nat
  low : Nat
  mid : Nat
  high : Nat
  jumpdest : Nat
  copyWord : Nat
  memoryLinear : Nat
  memoryQuadraticDivisor : Nat
  deriving DecidableEq, Repr

/-- The EVM stack capacity, enforced by the executable state representation. -/
def stackLimit : Nat := 1024

/-- A valid top-first EVM operand stack. -/
structure Stack where
  words : List UInt256
  bounded : words.length ≤ stackLimit
  deriving Repr

inductive StackFault where
  | underflow
  | overflow
  deriving DecidableEq, Repr

namespace Stack

def empty : Stack := ⟨[], by simp [stackLimit]⟩

/-- A total construction helper for finite test/reference inputs, never used by transitions. -/
def fromWords (words : List UInt256) : Stack :=
  ⟨words.take stackLimit, by simpa [stackLimit] using List.length_take_le stackLimit words⟩

def full : Stack :=
  ⟨List.replicate stackLimit Word.zero, by
    simp only [List.length_replicate]
    exact Nat.le_refl stackLimit⟩

def push (stack : Stack) (value : UInt256) : Option Stack :=
  if h : stack.words.length < stackLimit then
    some ⟨value :: stack.words, by
      simpa using Nat.succ_le_of_lt h⟩
  else
    none

def pop : (stack : Stack) → Option (UInt256 × Stack)
  | ⟨[], _⟩ => none
  | ⟨value :: tail, h⟩ => some (value, ⟨tail, by
      have h' : tail.length + 1 ≤ stackLimit := by
        simpa only [List.length_cons] using h
      omega⟩)

def popTwo (stack : Stack) : Option (UInt256 × UInt256 × Stack) := do
  let (first, afterFirst) ← stack.pop
  let (second, afterSecond) ← afterFirst.pop
  pure (first, second, afterSecond)

def popThree (stack : Stack) : Option (UInt256 × UInt256 × UInt256 × Stack) := do
  let (first, afterFirst) ← stack.pop
  let (second, afterSecond) ← afterFirst.pop
  let (third, afterThird) ← afterSecond.pop
  pure (first, second, third, afterThird)

def replaceAt {α : Type} : Nat → α → List α → Option (List α)
  | _, _, [] => none
  | 0, value, _ :: tail => some (value :: tail)
  | index + 1, value, head :: tail =>
    (replaceAt index value tail).map (head :: ·)

theorem replaceAt_length {α : Type} (index : Nat) (value : α) (words replaced : List α)
    (h : replaceAt index value words = some replaced) :
    replaced.length = words.length := by
  induction index generalizing words replaced with
  | zero =>
    cases words with
    | nil => simp [replaceAt] at h
    | cons head tail =>
      simp [replaceAt] at h
      subst replaced
      rfl
  | succ index ih =>
    cases words with
    | nil => simp [replaceAt] at h
    | cons head tail =>
      simp only [replaceAt] at h
      cases hRest : replaceAt index value tail with
      | none => simp [hRest] at h
      | some tailResult =>
        simp [hRest] at h
        subst replaced
        simp [ih tail tailResult hRest]

def replaceTop (stack : Stack) (value : UInt256) : Option Stack :=
  match stack.words with
  | [] => none
  | _ :: tail => some (fromWords (value :: tail))

def duplicate (depth : Nat) (stack : Stack) : Except StackFault Stack :=
  if _hDepth : 0 < depth ∧ depth ≤ stack.words.length then
    match stack.words[depth - 1]? with
    | none => .error .underflow
    | some value =>
      match stack.push value with
      | none => .error .overflow
      | some result => .ok result
  else
    .error .underflow

def swap (depth : Nat) (stack : Stack) : Option Stack :=
  match depth, stack.words with
  | 0, _ => none
  | _, [] => none
  | depth, top :: tail =>
    match tail[depth - 1]? with
    | none => none
    | some other =>
      match replaceAt (depth - 1) top tail with
      | none => none
      | some changedTail => some (fromWords (other :: changedTail))

theorem stack_is_bounded (stack : Stack) : stack.words.length ≤ stackLimit := stack.bounded

theorem push_full_is_none : full.push Word.zero = none := by native_decide

theorem pop_empty_is_none : empty.pop = none := rfl

theorem popped_tail_has_room (stack tail : Stack) (value : UInt256)
    (hPop : stack.pop = some (value, tail)) :
    tail.words.length < stackLimit := by
  rcases stack with ⟨words, hBound⟩
  cases words with
  | nil => simp [pop] at hPop
  | cons top rest =>
      simp [pop] at hPop
      rcases hPop with ⟨rfl, rfl⟩
      simp only [List.length_cons] at hBound
      change rest.length < stackLimit
      omega

end Stack

inductive ExceptionReason where
  | stackUnderflow
  | stackOverflow
  | invalidJump
  | returnDataOutOfBounds
  | invalidOpcode
  | unmodeledOpcode (byte : Byte)
  deriving DecidableEq, Repr

inductive Status where
  | running
  | stopped
  | returned (data : List Byte)
  | reverted (data : List Byte)
  | exceptional (reason : ExceptionReason)
  deriving DecidableEq, Repr

inductive Opcode where
  | stop
  | pop
  | mload | mstore | mstore8
  | msize | pc | gas
  | jump | jumpi | jumpdest
  | calldatasize | calldataload | calldatacopy
  | codesize | codecopy
  | returndatasize | returndatacopy
  | mcopy
  | return | revert
  | push (width : Nat)
  | dup (depth : Nat)
  | swap (depth : Nat)
  | invalid
  | unmodeled (byte : Byte)
  deriving DecidableEq, Repr

def decodeByte (value : Byte) : Opcode :=
  let raw := value.val
  if raw = 0x00 then .stop
  else if raw = 0x35 then .calldataload
  else if raw = 0x36 then .calldatasize
  else if raw = 0x37 then .calldatacopy
  else if raw = 0x38 then .codesize
  else if raw = 0x39 then .codecopy
  else if raw = 0x3d then .returndatasize
  else if raw = 0x3e then .returndatacopy
  else if raw = 0x50 then .pop
  else if raw = 0x51 then .mload
  else if raw = 0x52 then .mstore
  else if raw = 0x53 then .mstore8
  else if raw = 0x56 then .jump
  else if raw = 0x57 then .jumpi
  else if raw = 0x58 then .pc
  else if raw = 0x59 then .msize
  else if raw = 0x5a then .gas
  else if raw = 0x5b then .jumpdest
  else if raw = 0x5e then .mcopy
  else if raw = 0x5f then .push 0
  else if 0x60 ≤ raw ∧ raw ≤ 0x7f then .push (raw - 0x5f)
  else if 0x80 ≤ raw ∧ raw ≤ 0x8f then .dup (raw - 0x7f)
  else if 0x90 ≤ raw ∧ raw ≤ 0x9f then .swap (raw - 0x8f)
  else if raw = 0xf3 then .return
  else if raw = 0xfd then .revert
  else if raw = 0xfe then .invalid
  else .unmodeled value

def opcodeWidth : Opcode → Nat
  | .push width => width + 1
  | _ => 1

theorem opcodeWidth_positive (opcode : Opcode) : 0 < opcodeWidth opcode := by
  cases opcode <;> simp [opcodeWidth]

def readByte : List Byte → Nat → Byte
  | [], _ => zeroByte
  | value :: _, 0 => value
  | _ :: tail, index + 1 => readByte tail index

def readRange (source : List Byte) (offset size : Nat) : List Byte :=
  (List.range size).map fun index => readByte source (offset + index)

theorem readRange_length (source : List Byte) (offset size : Nat) :
    (readRange source offset size).length = size := by
  simp [readRange]

theorem readByte_zero_extended (source : List Byte) (offset : Nat)
    (h : source.length ≤ offset) : readByte source offset = zeroByte := by
  induction source generalizing offset with
  | nil => rfl
  | cons head tail ih =>
    cases offset with
    | zero =>
      simp only [List.length_cons] at h
      omega
    | succ offset =>
      simp only [readByte]
      apply ih
      simp only [List.length_cons] at h
      omega

def bytesToWord (bytes : List Byte) : UInt256 :=
  Word.ofNat (bytes.foldl (fun accumulated value => accumulated * 256 + value.val) 0)

def wordToBytes (word : UInt256) : List Byte :=
  (List.range Word.byteCount).map fun index =>
    byte ((word.val / 256 ^ (Word.byteCount - 1 - index)) % 256)

theorem wordToBytes_length (word : UInt256) : (wordToBytes word).length = Word.byteCount := by
  simp [wordToBytes]

/-- EVM memory expands in 32-byte words; a zero-length access expands nothing. -/
def roundedMemorySize (endOffset : Nat) : Nat :=
  if endOffset = 0 then 0 else ((endOffset + 31) / 32) * 32

def requestedMemorySize (offset size : Nat) : Nat :=
  if size = 0 then 0 else roundedMemorySize (offset + size)

theorem roundedMemorySize_word_aligned (endOffset : Nat) : 32 ∣ roundedMemorySize endOffset := by
  unfold roundedMemorySize
  split
  · exact ⟨0, by simp⟩
  · refine ⟨((endOffset + 31) / 32), ?_⟩
    omega

theorem requestedMemorySize_word_aligned (offset size : Nat) :
    32 ∣ requestedMemorySize offset size := by
  unfold requestedMemorySize
  split
  · exact ⟨0, by simp⟩
  · exact roundedMemorySize_word_aligned _

def ensureLength (memory : List Byte) (length : Nat) : List Byte :=
  if memory.length < length then
    memory ++ List.replicate (length - memory.length) zeroByte
  else
    memory

def expandMemory (memory : List Byte) (offset size : Nat) : List Byte :=
  ensureLength memory (requestedMemorySize offset size)

theorem expandMemory_zero_size (memory : List Byte) (offset : Nat) :
    expandMemory memory offset 0 = memory := by
  simp [expandMemory, requestedMemorySize, ensureLength]

def writeAt : List Byte → Nat → Byte → List Byte
  | [], _, _ => []
  | _ :: tail, 0, value => value :: tail
  | head :: tail, index + 1, value => head :: writeAt tail index value

def writeRangeAt : List Byte → Nat → List Byte → List Byte
  | memory, _, [] => memory
  | memory, offset, value :: tail =>
    writeRangeAt (writeAt memory offset value) (offset + 1) tail

def writeRange (memory : List Byte) (offset : Nat) (values : List Byte) : List Byte :=
  writeRangeAt (expandMemory memory offset values.length) offset values

def readMemoryRange (memory : List Byte) (offset size : Nat) : List Byte × List Byte :=
  let expanded := expandMemory memory offset size
  (expanded, readRange expanded offset size)

/-- MCOPY reads the entire source before writing, so overlapping copies use memmove semantics. -/
def mcopyMemory (memory : List Byte) (destination source size : Nat) : List Byte :=
  let sourceExpanded := expandMemory memory source size
  let sourceSnapshot := readRange sourceExpanded source size
  writeRange sourceExpanded destination sourceSnapshot

/--
The byte-addressed EVM memory carried by a running state.  Its allocated
length is always a 32-byte multiple, so an unaligned memory state cannot be
constructed through this reference state type.
-/
structure Memory where
  bytes : List Byte
  wordAligned : 32 ∣ bytes.length
  deriving Repr

namespace Memory

def empty : Memory := ⟨[], ⟨0, by simp⟩⟩

/-- Canonicalizes test/reference input bytes by preserving them and zero-extending to a word boundary. -/
def ofBytes (bytes : List Byte) : Memory :=
  ⟨MemoryStackControl.readRange bytes 0 (roundedMemorySize bytes.length), by
    rw [readRange_length]
    exact roundedMemorySize_word_aligned _⟩

def expand (memory : Memory) (offset size : Nat) : Memory :=
  if memory.bytes.length < requestedMemorySize offset size then
    ⟨MemoryStackControl.readRange memory.bytes 0 (requestedMemorySize offset size), by
      rw [readRange_length]
      exact requestedMemorySize_word_aligned offset size⟩
  else
    memory

def writeRange (memory : Memory) (offset : Nat) (values : List Byte) : Memory :=
  ofBytes (MemoryStackControl.writeRange memory.bytes offset values)

def readRange (memory : Memory) (offset size : Nat) : Memory × List Byte :=
  let expanded := memory.expand offset size
  (expanded, MemoryStackControl.readRange expanded.bytes offset size)

def mcopy (memory : Memory) (destination source size : Nat) : Memory :=
  let sourceExpanded := memory.expand source size
  let sourceSnapshot := MemoryStackControl.readRange sourceExpanded.bytes source size
  sourceExpanded.writeRange destination sourceSnapshot

theorem aligned (memory : Memory) : 32 ∣ memory.bytes.length := memory.wordAligned

theorem empty_aligned : 32 ∣ empty.bytes.length := empty.wordAligned

theorem ofBytes_aligned (bytes : List Byte) : 32 ∣ (ofBytes bytes).bytes.length :=
  (ofBytes bytes).wordAligned

theorem expand_aligned (memory : Memory) (offset size : Nat) :
    32 ∣ (memory.expand offset size).bytes.length := (memory.expand offset size).wordAligned

theorem writeRange_aligned (memory : Memory) (offset : Nat) (values : List Byte) :
    32 ∣ (memory.writeRange offset values).bytes.length := (memory.writeRange offset values).wordAligned

theorem mcopy_aligned (memory : Memory) (destination source size : Nat) :
    32 ∣ (memory.mcopy destination source size).bytes.length :=
  (memory.mcopy destination source size).wordAligned

end Memory

def decodeAt (code : List Byte) (pc : Nat) : Opcode :=
  match code[pc]? with
  | none => .stop
  | some value => decodeByte value

/-!
  `scanJumpDest` starts at instruction boundary zero and skips PUSH immediates.
  The explicit fuel is bounded by code length because each iteration consumes at
  least one byte.  It prevents a byte equal to `JUMPDEST` inside push data from
  becoming a valid destination.
-/
def scanJumpDest (code : List Byte) (target pc fuel : Nat) : Bool :=
  match fuel with
  | 0 => false
  | fuel + 1 =>
    if pc = target then
      match code[pc]? with
      | some value => decide (decodeByte value = .jumpdest)
      | none => false
    else
      match code[pc]? with
      | none => false
      | some value => scanJumpDest code target (pc + opcodeWidth (decodeByte value)) fuel

def isValidJumpDest (code : List Byte) (target : Nat) : Bool :=
  scanJumpDest code target 0 code.length

structure State where
  code : List Byte
  calldata : List Byte
  returnData : List Byte
  memory : Memory
  stack : Stack
  pc : Nat
  gasLeft : Nat
  status : Status
  deriving Repr

namespace State

def initial (code : List Byte) : State where
  code := code
  calldata := []
  returnData := []
  memory := Memory.empty
  stack := Stack.empty
  pc := 0
  gasLeft := 0
  status := .running

def exceptional (state : State) (reason : ExceptionReason) : State :=
  { state with status := .exceptional reason }

def advance (state : State) (width : Nat) (stack : Stack) (memory : Memory := state.memory) : State :=
  { state with pc := state.pc + width, stack := stack, memory := memory }

def haltStopped (state : State) (width : Nat) : State :=
  { state with pc := state.pc + width, status := .stopped }

/-- Reaching or running past bytecode is an implicit STOP without rewriting the dispatcher PC. -/
def haltAtEndOfCode (state : State) : State :=
  { state with status := .stopped }

def haltReturned (state : State) (data : List Byte) (memory : Memory) (stack : Stack) : State :=
  { state with pc := state.pc + 1, memory := memory, stack := stack, status := .returned data }

def haltReverted (state : State) (data : List Byte) (memory : Memory) (stack : Stack) : State :=
  { state with pc := state.pc + 1, memory := memory, stack := stack, status := .reverted data }

/-- Models Nethermind's dispatcher convention: it increments PC before RETURN/REVERT execute. -/
def eelsHaltPc (internalPc : Nat) : Nat := internalPc - 1

end State

def stackException (state : State) : StackFault → State
  | .underflow => State.exceptional state .stackUnderflow
  | .overflow => State.exceptional state .stackOverflow

def pushAndAdvance (state : State) (width : Nat) (value : UInt256) : State :=
  match state.stack.push value with
  | some stack => State.advance state width stack
  | none => State.exceptional state .stackOverflow

def popAndAdvance (state : State) : State :=
  match state.stack.pop with
  | some (_, stack) => State.advance state 1 stack
  | none => State.exceptional state .stackUnderflow

def loadMemory (state : State) : State :=
  match state.stack.pop with
  | none => State.exceptional state .stackUnderflow
  | some (offset, stack) =>
    let (memory, bytes) := state.memory.readRange offset.val Word.byteCount
    State.advance state 1 (Stack.fromWords (bytesToWord bytes :: stack.words)) memory

def storeMemory (state : State) : State :=
  match state.stack.popTwo with
  | none => State.exceptional state .stackUnderflow
  | some (offset, value, stack) =>
    State.advance state 1 stack (state.memory.writeRange offset.val (wordToBytes value))

def storeMemoryByte (state : State) : State :=
  match state.stack.popTwo with
  | none => State.exceptional state .stackUnderflow
  | some (offset, value, stack) =>
    State.advance state 1 stack (state.memory.writeRange offset.val [byte value.val])

def copyCallData (state : State) : State :=
  match state.stack.popThree with
  | none => State.exceptional state .stackUnderflow
  | some (destination, source, size, stack) =>
    State.advance state 1 stack
      (state.memory.writeRange destination.val (readRange state.calldata source.val size.val))

def copyCode (state : State) : State :=
  match state.stack.popThree with
  | none => State.exceptional state .stackUnderflow
  | some (destination, source, size, stack) =>
    State.advance state 1 stack
      (state.memory.writeRange destination.val (readRange state.code source.val size.val))

def performReturnDataCopy (state : State) (destination source size : UInt256) (stack : Stack) : State :=
  if source.val + size.val ≤ state.returnData.length then
    State.advance state 1 stack
      (state.memory.writeRange destination.val (readRange state.returnData source.val size.val))
  else
    State.exceptional { state with stack := stack } .returnDataOutOfBounds

def copyReturnData (state : State) : State :=
  match state.stack.popThree with
  | none => State.exceptional state .stackUnderflow
  | some (destination, source, size, stack) => performReturnDataCopy state destination source size stack

def copyMemory (state : State) : State :=
  match state.stack.popThree with
  | none => State.exceptional state .stackUnderflow
  | some (destination, source, size, stack) =>
    State.advance state 1 stack (state.memory.mcopy destination.val source.val size.val)

def jumpTo (state : State) (target : Nat) (stack : Stack) : State :=
  if isValidJumpDest state.code target then
    { state with pc := target, stack := stack }
  else
    State.exceptional { state with stack := stack } .invalidJump

def executeJump (state : State) : State :=
  match state.stack.pop with
  | none => State.exceptional state .stackUnderflow
  | some (target, stack) => jumpTo state target.val stack

def executeJumpI (state : State) : State :=
  match state.stack.popTwo with
  | none => State.exceptional state .stackUnderflow
  | some (target, condition, stack) =>
    if condition.val = 0 then State.advance state 1 stack else jumpTo state target.val stack

def returnValues (state : State) (offset size : UInt256) (stack : Stack) : State :=
  let (memory, data) := state.memory.readRange offset.val size.val
  State.haltReturned state data memory stack

def revertValues (state : State) (offset size : UInt256) (stack : Stack) : State :=
  let (memory, data) := state.memory.readRange offset.val size.val
  State.haltReverted state data memory stack

def executeReturn (state : State) : State :=
  match state.stack.popTwo with
  | none => State.exceptional state .stackUnderflow
  | some (offset, size, stack) => returnValues state offset size stack

def executeRevert (state : State) : State :=
  match state.stack.popTwo with
  | none => State.exceptional state .stackUnderflow
  | some (offset, size, stack) => revertValues state offset size stack

def stepRunning (state : State) : State :=
  if state.code.length ≤ state.pc then
    State.haltAtEndOfCode state
  else match decodeAt state.code state.pc with
  | .stop => State.haltStopped state 1
  | .pop => popAndAdvance state
  | .mload => loadMemory state
  | .mstore => storeMemory state
  | .mstore8 => storeMemoryByte state
  | .msize => pushAndAdvance state 1 (Word.ofNat state.memory.bytes.length)
  | .pc => pushAndAdvance state 1 (Word.ofNat state.pc)
  | .gas => pushAndAdvance state 1 (Word.ofNat state.gasLeft)
  | .jump => executeJump state
  | .jumpi => executeJumpI state
  | .jumpdest => State.advance state 1 state.stack
  | .calldatasize => pushAndAdvance state 1 (Word.ofNat state.calldata.length)
  | .calldataload =>
    match state.stack.pop with
    | none => State.exceptional state .stackUnderflow
    | some (offset, stack) =>
      State.advance state 1
        (Stack.fromWords (bytesToWord (readRange state.calldata offset.val Word.byteCount) :: stack.words))
  | .calldatacopy => copyCallData state
  | .codesize => pushAndAdvance state 1 (Word.ofNat state.code.length)
  | .codecopy => copyCode state
  | .returndatasize => pushAndAdvance state 1 (Word.ofNat state.returnData.length)
  | .returndatacopy => copyReturnData state
  | .mcopy => copyMemory state
  | .return => executeReturn state
  | .revert => executeRevert state
  | .push width =>
    pushAndAdvance state (width + 1) (bytesToWord (readRange state.code (state.pc + 1) width))
  | .dup depth =>
    match state.stack.duplicate depth with
    | .ok stack => State.advance state 1 stack
    | .error reason => stackException state reason
  | .swap depth =>
    match state.stack.swap depth with
    | some stack => State.advance state 1 stack
    | none => State.exceptional state .stackUnderflow
  | .invalid => State.exceptional state .invalidOpcode
  | .unmodeled value => State.exceptional state (.unmodeledOpcode value)

def step (state : State) : State :=
  match state.status with
  | .running => stepRunning state
  | _ => state

def run : Nat → State → State
  | 0, state => state
  | fuel + 1, state => run fuel (step state)

theorem state_stack_bound (state : State) : state.stack.words.length ≤ stackLimit :=
  state.stack.bounded

theorem state_memory_word_aligned (state : State) : 32 ∣ state.memory.bytes.length :=
  state.memory.wordAligned

theorem initial_memory_word_aligned (code : List Byte) :
    32 ∣ (State.initial code).memory.bytes.length := (State.initial code).memory.wordAligned

theorem exceptional_memory_word_aligned (state : State) (reason : ExceptionReason) :
    32 ∣ (State.exceptional state reason).memory.bytes.length :=
  (State.exceptional state reason).memory.wordAligned

theorem advance_memory_word_aligned (state : State) (width : Nat) (stack : Stack) (memory : Memory) :
    32 ∣ (State.advance state width stack memory).memory.bytes.length :=
  (State.advance state width stack memory).memory.wordAligned

theorem haltStopped_memory_word_aligned (state : State) (width : Nat) :
    32 ∣ (State.haltStopped state width).memory.bytes.length :=
  (State.haltStopped state width).memory.wordAligned

theorem haltAtEndOfCode_memory_word_aligned (state : State) :
    32 ∣ (State.haltAtEndOfCode state).memory.bytes.length :=
  (State.haltAtEndOfCode state).memory.wordAligned

theorem haltReturned_memory_word_aligned (state : State) (data : List Byte) (memory : Memory) (stack : Stack) :
    32 ∣ (State.haltReturned state data memory stack).memory.bytes.length :=
  (State.haltReturned state data memory stack).memory.wordAligned

theorem haltReverted_memory_word_aligned (state : State) (data : List Byte) (memory : Memory) (stack : Stack) :
    32 ∣ (State.haltReverted state data memory stack).memory.bytes.length :=
  (State.haltReverted state data memory stack).memory.wordAligned

theorem step_memory_word_aligned (state : State) : 32 ∣ (step state).memory.bytes.length :=
  (step state).memory.wordAligned

theorem run_memory_word_aligned (fuel : Nat) (state : State) :
    32 ∣ (run fuel state).memory.bytes.length := (run fuel state).memory.wordAligned

theorem step_stable_after_halt (state : State) (h : state.status ≠ .running) : step state = state := by
  cases state.status <;> simp_all [step]

theorem advance_pc (state : State) (width : Nat) (stack : Stack) (memory : Memory) :
    (State.advance state width stack memory).pc = state.pc + width := rfl

theorem fallthrough_width_positive (opcode : Opcode) : 0 < opcodeWidth opcode := opcodeWidth_positive opcode

theorem jumpTo_valid (state : State) (target : Nat) (stack : Stack)
    (h : isValidJumpDest state.code target = true) :
    (jumpTo state target stack).status = state.status ∧ (jumpTo state target stack).pc = target := by
  simp [jumpTo, h]

theorem jumpTo_invalid (state : State) (target : Nat) (stack : Stack)
    (h : isValidJumpDest state.code target = false) :
    (jumpTo state target stack).status = .exceptional .invalidJump ∧
      (jumpTo state target stack).pc = state.pc ∧
      (jumpTo state target stack).stack.words = stack.words ∧
      (jumpTo state target stack).memory.bytes = state.memory.bytes := by
  simp [jumpTo, h, State.exceptional]

theorem gas_pushes_gasLeft (state : State) :
    stepRunning { state with code := [byte 0x5a], pc := 0, status := .running } =
      pushAndAdvance { state with code := [byte 0x5a], pc := 0, status := .running } 1
        (Word.ofNat state.gasLeft) := by
  rfl

theorem returndata_copy_oob (state : State) (destination source size : UInt256) (stack : Stack)
    (h : state.returnData.length < source.val + size.val) :
    (performReturnDataCopy state destination source size stack).status =
      .exceptional .returnDataOutOfBounds ∧
      (performReturnDataCopy state destination source size stack).pc = state.pc ∧
      (performReturnDataCopy state destination source size stack).stack.words = stack.words ∧
      (performReturnDataCopy state destination source size stack).memory.bytes = state.memory.bytes := by
  simp [performReturnDataCopy, Nat.not_le_of_lt h, State.exceptional]

theorem returned_and_reverted_distinct (data : List Byte) :
    Status.returned data ≠ Status.reverted data := by
  simp

theorem returnValues_preserves_post_pop_tail (state : State) (offset size : UInt256) (stack : Stack) :
    (returnValues state offset size stack).stack.words = stack.words := by
  simp [returnValues, State.haltReturned]

theorem revertValues_preserves_post_pop_tail (state : State) (offset size : UInt256) (stack : Stack) :
    (revertValues state offset size stack).stack.words = stack.words := by
  simp [revertValues, State.haltReverted]

theorem returnValues_nethermind_pc (state : State) (offset size : UInt256) (stack : Stack) :
    (returnValues state offset size stack).pc = state.pc + 1 := by
  simp [returnValues, State.haltReturned]

theorem revertValues_nethermind_pc (state : State) (offset size : UInt256) (stack : Stack) :
    (revertValues state offset size stack).pc = state.pc + 1 := by
  simp [revertValues, State.haltReverted]

/-- The internal post-dispatch PC corresponds to EELS's unchanged opcode offset on RETURN. -/
theorem returnValues_eels_pc (state : State) (offset size : UInt256) (stack : Stack) :
    State.eelsHaltPc (returnValues state offset size stack).pc = state.pc := by
  simp [returnValues, State.haltReturned, State.eelsHaltPc]

/-- The internal post-dispatch PC corresponds to EELS's unchanged opcode offset on REVERT. -/
theorem revertValues_eels_pc (state : State) (offset size : UInt256) (stack : Stack) :
    State.eelsHaltPc (revertValues state offset size stack).pc = state.pc := by
  simp [revertValues, State.haltReverted, State.eelsHaltPc]

theorem return_is_not_exception (state : State) (offset size : UInt256) (stack : Stack) :
    (returnValues state offset size stack).status ≠
      .exceptional .invalidOpcode := by
  simp [returnValues, State.haltReturned]

theorem implicit_end_of_code_pc (state : State) (h : state.code.length ≤ state.pc) :
    (stepRunning state).status = .stopped ∧ (stepRunning state).pc = state.pc := by
  simp [stepRunning, h, State.haltAtEndOfCode]

def pushWidths : List Nat := List.range 33

def dupDepths : List Nat := (List.range 16).map (· + 1)

def swapDepths : List Nat := (List.range 16).map (· + 1)

theorem every_push_family_decodes :
    pushWidths.all (fun width => decide (decodeByte (byte (0x5f + width)) = .push width)) = true := by
  native_decide

theorem every_dup_family_decodes :
    dupDepths.all (fun depth => decide (decodeByte (byte (0x7f + depth)) = .dup depth)) = true := by
  native_decide

theorem every_swap_family_decodes :
    swapDepths.all (fun depth => decide (decodeByte (byte (0x8f + depth)) = .swap depth)) = true := by
  native_decide

end MemoryStackControl
end Evm
end Eip803x
