-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Evm.MemoryStackControl

/-!
  Literal boundary vectors for `MemoryStackControl`.  Expected snapshots are
  written without calling the transition functions under test.  The mutation
  checks below deliberately omit PUSH zero-padding, instruction-boundary jump
  validation, post-pop exceptional/terminal tails, MCOPY snapshotting,
  returndata bounds, end-of-code PC preservation, or MSTORE8's low-byte rule;
  each has a named counterexample.
-/

namespace Eip803x
namespace Evm
namespace MemoryStackControl
namespace BoundaryVector

def b (value : Nat) : Byte := byte value

def w (value : Nat) : UInt256 := Word.ofNat value

def zeros (count : Nat) : List Byte := List.replicate count zeroByte

def mstore0102 : List Byte := zeros 30 ++ [b 0x01, b 0x02]

def mstore8LowByte : List Byte := b 0x34 :: zeros 31

def copiedCallData : List Byte := [b 0xbb, zeroByte, zeroByte] ++ zeros 29

def copiedCode : List Byte := [b 0x39, zeroByte] ++ zeros 30

def copiedReturnData : List Byte := b 0x20 :: zeros 31

def mcopyOverlap : List Byte := [b 0x01, b 0x01, b 0x02, b 0x03] ++ zeros 28

def returnedBytes : List Byte := [b 0xaa, b 0xbb]

def state (code : List Byte) (stack : List UInt256 := []) (calldata : List Byte := [])
    (returnData : List Byte := []) (memory : List Byte := []) (gasLeft : Nat := 0) : State :=
  { State.initial code with
    stack := Stack.fromWords stack
    calldata := calldata
    returnData := returnData
    memory := Memory.ofBytes memory
    gasLeft := gasLeft }

structure Snapshot where
  status : Status
  pc : Nat
  stack : List UInt256
  memory : List Byte
  deriving DecidableEq, Repr

def observe (state : State) : Snapshot :=
  { status := state.status
    pc := state.pc
    stack := state.stack.words
    memory := state.memory.bytes }

structure Vector where
  name : String
  fuel : Nat
  initial : State
  expected : Snapshot
  deriving Repr

def passes (vector : Vector) : Bool :=
  decide (observe (run vector.fuel vector.initial) = vector.expected)

def depth16 : List UInt256 :=
  [w 1, w 2, w 3, w 4, w 5, w 6, w 7, w 8,
    w 9, w 10, w 11, w 12, w 13, w 14, w 15, w 16]

def depth17 : List UInt256 := depth16 ++ [w 17]

def vectors : List Vector :=
  [ { name := "push0-pushes-a-canonical-zero"
      fuel := 2
      initial := state [b 0x5f, b 0x00]
      expected := { status := .stopped, pc := 2, stack := [w 0], memory := [] } }
  , { name := "push1-advances-over-its-immediate"
      fuel := 2
      initial := state [b 0x60, b 0x2a, b 0x00]
      expected := { status := .stopped, pc := 3, stack := [w 0x2a], memory := [] } }
  , { name := "truncated-push2-right-zero-pads-missing-code"
      fuel := 1
      initial := state [b 0x61, b 0xab]
      expected := { status := .running, pc := 3, stack := [w 0xab00], memory := [] } }
  , { name := "push32-reads-a-full-big-endian-word"
      fuel := 1
      initial := state ([b 0x7f, b 0x01] ++ zeros 31)
      expected := { status := .running, pc := 33, stack := [w (256 ^ 31)], memory := [] } }
  , { name := "pop-removes-the-top-word"
      fuel := 2
      initial := state [b 0x50, b 0x00] [w 7]
      expected := { status := .stopped, pc := 2, stack := [], memory := [] } }
  , { name := "dup2-and-swap1-use-top-first-depths"
      fuel := 5
      initial := state [b 0x60, b 0x01, b 0x60, b 0x02, b 0x81, b 0x90, b 0x00]
      expected := { status := .stopped, pc := 7, stack := [w 2, w 1, w 1], memory := [] } }
  , { name := "dup16-reaches-the-deepest-supported-word"
      fuel := 1
      initial := state [b 0x8f] depth16
      expected := { status := .running, pc := 1, stack := w 16 :: depth16, memory := [] } }
  , { name := "swap16-exchanges-top-with-the-seventeenth-word"
      fuel := 1
      initial := state [b 0x9f] depth17
      expected := { status := .running, pc := 1, stack :=
        [w 17, w 2, w 3, w 4, w 5, w 6, w 7, w 8, w 9,
          w 10, w 11, w 12, w 13, w 14, w 15, w 16, w 1], memory := [] } }
  , { name := "mstore-writes-32-big-endian-bytes-and-expands-memory"
      fuel := 2
      initial := state [b 0x52, b 0x00] [w 0, w 0x0102]
      expected := { status := .stopped, pc := 2, stack := [], memory := mstore0102 } }
  , { name := "mstore8-writes-the-low-byte"
      fuel := 2
      initial := state [b 0x53, b 0x00] [w 0, w 0x1234]
      expected := { status := .stopped, pc := 2, stack := [], memory := mstore8LowByte } }
  , { name := "mload-reads-a-32-byte-big-endian-word"
      fuel := 2
      initial := state [b 0x51, b 0x00] [w 0] [] [] mstore0102
      expected := { status := .stopped, pc := 2, stack := [w 0x0102], memory := mstore0102 } }
  , { name := "msize-observes-rounded-memory-length"
      fuel := 3
      initial := state [b 0x52, b 0x59, b 0x00] [w 0, w 0x0102]
      expected := { status := .stopped, pc := 3, stack := [w 32], memory := mstore0102 } }
  , { name := "msize-cannot-observe-an-unaligned-memory-state"
      fuel := 2
      initial := state [b 0x59, b 0x00] [] [] [] [b 0x01]
      expected := { status := .stopped, pc := 2, stack := [w 32], memory := b 0x01 :: zeros 31 } }
  , { name := "pc-and-gas-push-the-current-observable-values"
      fuel := 3
      initial := state [b 0x58, b 0x5a, b 0x00] [] [] [] [] 77
      expected := { status := .stopped, pc := 3, stack := [w 77, w 0], memory := [] } }
  , { name := "jump-requires-and-enters-an-instruction-boundary-jumpdest"
      fuel := 4
      initial := state [b 0x60, b 0x03, b 0x56, b 0x5b, b 0x00]
      expected := { status := .stopped, pc := 5, stack := [], memory := [] } }
  , { name := "jumpdest-byte-inside-push-data-is-not-a-destination"
      fuel := 2
      initial := state [b 0x60, b 0x04, b 0x56, b 0x60, b 0x5b, b 0x00]
      expected := { status := .exceptional .invalidJump, pc := 2, stack := [], memory := [] } }
  , { name := "invalid-jump-retains-the-post-pop-tail-and-pc"
      fuel := 1
      initial := state [b 0x56] [w 999, w 7]
      expected := { status := .exceptional .invalidJump, pc := 0, stack := [w 7], memory := [] } }
  , { name := "taken-invalid-jumpi-retains-the-post-pop-tail-and-pc"
      fuel := 1
      initial := state [b 0x57] [w 999, w 1, w 7]
      expected := { status := .exceptional .invalidJump, pc := 0, stack := [w 7], memory := [] } }
  , { name := "jumpi-with-zero-condition-does-not-validate-or-take-target"
      fuel := 2
      initial := state [b 0x57, b 0x00] [w 999, w 0]
      expected := { status := .stopped, pc := 2, stack := [], memory := [] } }
  , { name := "calldatasize-uses-the-calldata-length"
      fuel := 2
      initial := state [b 0x36, b 0x00] [] [b 0xaa, b 0xbb]
      expected := { status := .stopped, pc := 2, stack := [w 2], memory := [] } }
  , { name := "calldataload-zero-extends-to-a-word"
      fuel := 2
      initial := state [b 0x35, b 0x00] [w 0] [b 0x01, b 0x02]
      expected := { status := .stopped, pc := 2, stack := [w (256 ^ 31 + 2 * 256 ^ 30)], memory := [] } }
  , { name := "calldatacopy-zero-extends-past-the-input"
      fuel := 2
      initial := state [b 0x37, b 0x00] [w 0, w 1, w 3] [b 0xaa, b 0xbb]
      expected := { status := .stopped, pc := 2, stack := [], memory := copiedCallData } }
  , { name := "codesize-counts-opcode-bytes"
      fuel := 2
      initial := state [b 0x38, b 0x00]
      expected := { status := .stopped, pc := 2, stack := [w 2], memory := [] } }
  , { name := "codecopy-can-copy-the-current-opcode-bytes"
      fuel := 2
      initial := state [b 0x39, b 0x00] [w 0, w 0, w 2]
      expected := { status := .stopped, pc := 2, stack := [], memory := copiedCode } }
  , { name := "returndatasize-counts-only-the-current-return-buffer"
      fuel := 2
      initial := state [b 0x3d, b 0x00] [] [] [b 0x10, b 0x20]
      expected := { status := .stopped, pc := 2, stack := [w 2], memory := [] } }
  , { name := "returndatacopy-copies-a-valid-bounded-range"
      fuel := 2
      initial := state [b 0x3e, b 0x00] [w 0, w 1, w 1] [] [b 0x10, b 0x20]
      expected := { status := .stopped, pc := 2, stack := [], memory := copiedReturnData } }
  , { name := "returndatacopy-past-the-buffer-is-exceptional-not-zero-extended"
      fuel := 1
      initial := state [b 0x3e] [w 0, w 1, w 2] [] [b 0x10, b 0x20]
      expected := { status := .exceptional .returnDataOutOfBounds, pc := 0, stack := [], memory := [] } }
  , { name := "returndatacopy-zero-size-at-the-buffer-end-succeeds"
      fuel := 2
      initial := state [b 0x3e, b 0x00] [w 0, w 2, w 0, w 7] [] [b 0x10, b 0x20]
      expected := { status := .stopped, pc := 2, stack := [w 7], memory := [] } }
  , { name := "returndatacopy-zero-size-past-the-buffer-retains-the-post-pop-tail"
      fuel := 1
      initial := state [b 0x3e] [w 0, w 3, w 0, w 7] [] [b 0x10, b 0x20]
      expected := { status := .exceptional .returnDataOutOfBounds, pc := 0, stack := [w 7], memory := [] } }
  , { name := "mcopy-overlap-uses-a-source-snapshot"
      fuel := 2
      initial := state [b 0x5e, b 0x00] [w 1, w 0, w 3] [] [] [b 0x01, b 0x02, b 0x03, b 0x04]
      expected := { status := .stopped, pc := 2, stack := [], memory := mcopyOverlap } }
  , { name := "return-halts-successfully-with-memory-data"
      fuel := 1
      initial := state [b 0xf3] [w 0, w 2, w 7, w 8] [] [] returnedBytes
      expected := { status := .returned returnedBytes, pc := 1, stack := [w 7, w 8], memory := returnedBytes ++ zeros 30 } }
  , { name := "revert-is-distinct-from-return-with-the-same-data"
      fuel := 1
      initial := state [b 0xfd] [w 0, w 2, w 7, w 8] [] [] returnedBytes
      expected := { status := .reverted returnedBytes, pc := 1, stack := [w 7, w 8], memory := returnedBytes ++ zeros 30 } }
  , { name := "implicit-end-of-code-keeps-pc-at-the-code-length"
      fuel := 1
      initial := state []
      expected := { status := .stopped, pc := 0, stack := [], memory := [] } }
  , { name := "implicit-end-of-code-preserves-a-truncated-push-overshoot"
      fuel := 2
      initial := state [b 0x61, b 0xab]
      expected := { status := .stopped, pc := 3, stack := [w 0xab00], memory := [] } }
  , { name := "invalid-is-an-exceptional-halt"
      fuel := 1
      initial := state [b 0xfe]
      expected := { status := .exceptional .invalidOpcode, pc := 0, stack := [], memory := [] } }
  , { name := "unmodeled-bytecode-is-explicitly-not-claimed-as-invalid"
      fuel := 1
      initial := state [b 0x01]
      expected := { status := .exceptional (.unmodeledOpcode (b 0x01)), pc := 0, stack := [], memory := [] } } ]

theorem all_pass : vectors.all passes = true := by native_decide

theorem vector_count : vectors.length = 36 := by native_decide

def naiveJumpDest (code : List Byte) (target : Nat) : Bool :=
  match code[target]? with
  | some value => decide (decodeByte value = .jumpdest)
  | none => false

def truncatedPushValue (code : List Byte) (pc width : Nat) : UInt256 :=
  bytesToWord ((code.drop (pc + 1)).take width)

def forwardMcopy : List Byte → Nat → Nat → Nat → List Byte
  | memory, _, _, 0 => memory
  | memory, destination, source, size + 1 =>
    let afterWrite := writeRange memory destination [readByte memory source]
    forwardMcopy afterWrite (destination + 1) (source + 1) size

def zeroExtendingReturnDataCopy (state : State) (destination source size : UInt256) (stack : Stack) : State :=
  State.advance state 1 stack
    (state.memory.writeRange destination.val (readRange state.returnData source.val size.val))

def highByteMstore8 (memory : List Byte) (offset value : UInt256) : List Byte :=
  writeRange memory offset.val [b (value.val / 256)]

def clearingReturnValues (state : State) (offset size : UInt256) : State :=
  let (memory, data) := state.memory.readRange offset.val size.val
  { state with pc := state.pc + 1, memory := memory, stack := Stack.empty, status := .returned data }

def clearingRevertValues (state : State) (offset size : UInt256) : State :=
  let (memory, data) := state.memory.readRange offset.val size.val
  { state with pc := state.pc + 1, memory := memory, stack := Stack.empty, status := .reverted data }

def restoringInvalidJump (state : State) : State :=
  State.exceptional state .invalidJump

def restoringReturnDataCopy (state : State) : State :=
  State.exceptional state .returnDataOutOfBounds

def advancingImplicitEnd (state : State) : State :=
  State.haltStopped state 1

def normalizingImplicitEnd (state : State) : State :=
  { state with pc := state.code.length, status := .stopped }

theorem mutation_checks :
    naiveJumpDest [b 0x60, b 0x04, b 0x56, b 0x60, b 0x5b, b 0x00] 4 = true ∧
    isValidJumpDest [b 0x60, b 0x04, b 0x56, b 0x60, b 0x5b, b 0x00] 4 = false ∧
    truncatedPushValue [b 0x61, b 0xab] 0 2 ≠ w 0xab00 ∧
    forwardMcopy [b 0x01, b 0x02, b 0x03, b 0x04] 1 0 3 ≠ mcopyOverlap ∧
    (zeroExtendingReturnDataCopy
      (state [b 0x3e] [w 0, w 1, w 2] [] [b 0x10, b 0x20]) (w 0) (w 1) (w 2) Stack.empty).status ≠
      .exceptional .returnDataOutOfBounds ∧
    highByteMstore8 [] (w 0) (w 0x1234) ≠ mstore8LowByte ∧
    (clearingReturnValues (state [b 0xf3] [w 0, w 2, w 7]) (w 0) (w 2)).stack.words ≠ [w 7] ∧
    (clearingRevertValues (state [b 0xfd] [w 0, w 2, w 7]) (w 0) (w 2)).stack.words ≠ [w 7] ∧
    (restoringInvalidJump (state [b 0x56] [w 999, w 7])).stack.words ≠ [w 7] ∧
    (restoringReturnDataCopy (state [b 0x3e] [w 0, w 3, w 0, w 7])).stack.words ≠ [w 7] ∧
    (advancingImplicitEnd (state [])).pc ≠ 0 ∧
    (normalizingImplicitEnd { (state [b 0x61, b 0xab]) with pc := 3 }).pc ≠ 3 := by
  native_decide

end BoundaryVector
end MemoryStackControl
end Evm
end Eip803x
