-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Evm.Word

namespace Eip803x
namespace Evm
namespace PureStack

open Word

/-- The Amsterdam pure word opcodes covered by this stack-only reference slice. -/
inductive Opcode where
  | add | mul | sub | div | sdiv | mod | smod | addmod | mulmod | exp | signextend
  | lt | gt | slt | sgt | eq | iszero
  | and | or | xor | not | byte | shl | shr | sar | clz
  deriving DecidableEq, Repr

def allOpcodes : List Opcode :=
  [ .add, .mul, .sub, .div, .sdiv, .mod, .smod, .addmod, .mulmod, .exp, .signextend
  , .lt, .gt, .slt, .sgt, .eq, .iszero
  , .and, .or, .xor, .not, .byte, .shl, .shr, .sar, .clz ]

theorem opcode_count : allOpcodes.length = 26 := by native_decide

def arity : Opcode → Nat
  | .add | .mul | .sub | .div | .sdiv | .mod | .smod | .exp | .signextend
  | .lt | .gt | .slt | .sgt | .eq | .and | .or | .xor | .byte | .shl | .shr | .sar => 2
  | .addmod | .mulmod => 3
  | .iszero | .not | .clz => 1

/-- A stack word operation whose input is the EVM top-of-stack first. -/
def unary (operation : UInt256 → UInt256) : List UInt256 → Option (List UInt256)
  | value :: rest => some (operation value :: rest)
  | _ => none

/-- A two-word operation; the first argument is the EVM top-of-stack. -/
def binary (operation : UInt256 → UInt256 → UInt256) : List UInt256 → Option (List UInt256)
  | top :: next :: rest => some (operation top next :: rest)
  | _ => none

/-- A three-word operation; the first argument is the EVM top-of-stack. -/
def ternary (operation : UInt256 → UInt256 → UInt256 → UInt256) : List UInt256 → Option (List UInt256)
  | top :: next :: third :: rest => some (operation top next third :: rest)
  | _ => none

/--
Executes only the pure stack transformation. Gas, program counter, stack-depth
limit, memory, state, and exceptional EVM control flow remain outside this slice.
-/
def execute : Opcode → List UInt256 → Option (List UInt256)
  | .add => binary Word.add
  | .mul => binary Word.mul
  | .sub => binary Word.sub
  | .div => binary Word.udiv
  | .sdiv => binary Word.sdiv
  | .mod => binary Word.umod
  | .smod => binary Word.smod
  | .addmod => ternary Word.addmod
  | .mulmod => ternary Word.mulmod
  | .exp => binary Word.exp
  | .signextend => binary Word.signextend
  | .lt => binary Word.unsignedLt
  | .gt => binary Word.unsignedGt
  | .slt => binary Word.signedLt
  | .sgt => binary Word.signedGt
  | .eq => binary Word.equal
  | .iszero => unary Word.isZero
  | .and => binary Word.bitwiseAnd
  | .or => binary Word.bitwiseOr
  | .xor => binary Word.bitwiseXor
  | .not => unary Word.bitwiseNot
  | .byte => binary Word.byte
  | .shl => binary Word.shl
  | .shr => binary Word.shr
  | .sar => binary Word.sar
  | .clz => unary Word.clz

theorem execute_output_range {opcode : Opcode} {input output : List UInt256}
    (_h : execute opcode input = some output) :
    ∀ word ∈ output, word.val < Word.modulus := by
  intro word _
  exact Word.range word

theorem unary_underflow (operation : UInt256 → UInt256) : unary operation [] = none := rfl

theorem binary_underflow_empty (operation : UInt256 → UInt256 → UInt256) :
    binary operation [] = none := rfl

theorem binary_underflow_one (operation : UInt256 → UInt256 → UInt256) (value : UInt256) :
    binary operation [value] = none := rfl

theorem ternary_underflow_two (operation : UInt256 → UInt256 → UInt256 → UInt256)
    (top next : UInt256) : ternary operation [top, next] = none := rfl

namespace BoundaryVector

structure Vector where
  name : String
  opcode : Opcode
  input : List UInt256
  expected : Option (List UInt256)
  deriving DecidableEq, Repr

def w (value : Nat) : UInt256 := Word.ofNat value

def s (value : Int) : UInt256 := Word.fromInt value

def vectors : List Vector :=
  [ { name := "add-wraps-at-256-bits"
      opcode := .add
      input := [Word.allOnes, Word.one]
      expected := some [Word.zero] }
  , { name := "mul-wraps-at-256-bits"
      opcode := .mul
      input := [Word.minSignedWord, w 2]
      expected := some [Word.zero] }
  , { name := "sub-wraps-underflow"
      opcode := .sub
      input := [Word.zero, Word.one]
      expected := some [Word.allOnes] }
  , { name := "div-truncates-unsigned"
      opcode := .div
      input := [w 7, w 3]
      expected := some [w 2] }
  , { name := "div-by-zero-is-zero"
      opcode := .div
      input := [w 7, Word.zero]
      expected := some [Word.zero] }
  , { name := "sdiv-truncates-toward-zero"
      opcode := .sdiv
      input := [s (-7), w 3]
      expected := some [s (-2)] }
  , { name := "sdiv-min-int-by-negative-one"
      opcode := .sdiv
      input := [Word.minSignedWord, s (-1)]
      expected := some [Word.minSignedWord] }
  , { name := "mod-uses-unsigned-remainder"
      opcode := .mod
      input := [w 7, w 3]
      expected := some [w 1] }
  , { name := "mod-by-zero-is-zero"
      opcode := .mod
      input := [w 7, Word.zero]
      expected := some [Word.zero] }
  , { name := "smod-retains-dividend-sign"
      opcode := .smod
      input := [s (-7), w 3]
      expected := some [s (-1)] }
  , { name := "smod-by-zero-is-zero"
      opcode := .smod
      input := [s (-7), Word.zero]
      expected := some [Word.zero] }
  , { name := "addmod-uses-unbounded-intermediate-sum"
      opcode := .addmod
      input := [Word.allOnes, Word.one, w 5]
      expected := some [Word.one] }
  , { name := "addmod-zero-modulus-is-zero"
      opcode := .addmod
      input := [w 4, w 5, Word.zero]
      expected := some [Word.zero] }
  , { name := "mulmod-uses-unbounded-intermediate-product"
      opcode := .mulmod
      input := [Word.minSignedWord, w 2, w 5]
      expected := some [Word.one] }
  , { name := "mulmod-zero-modulus-is-zero"
      opcode := .mulmod
      input := [w 4, w 5, Word.zero]
      expected := some [Word.zero] }
  , { name := "exp-reduces-modulo-2-to-256"
      opcode := .exp
      input := [w (2 ^ 255), w 2]
      expected := some [Word.zero] }
  , { name := "exp-zero-to-zero-is-one"
      opcode := .exp
      input := [Word.zero, Word.zero]
      expected := some [Word.one] }
  , { name := "exp-uses-top-as-base"
      opcode := .exp
      input := [w 2, w 3]
      expected := some [w 8] }
  , { name := "signextend-extends-negative-low-byte"
      opcode := .signextend
      input := [Word.zero, w 0x80]
      expected := some [w (Word.modulus - 128)] }
  , { name := "signextend-outside-word-is-identity"
      opcode := .signextend
      input := [w 32, w 0x80]
      expected := some [w 0x80] }
  , { name := "lt-uses-unsigned-order"
      opcode := .lt
      input := [w 1, w 2]
      expected := some [Word.one] }
  , { name := "lt-false-is-zero"
      opcode := .lt
      input := [w 2, w 1]
      expected := some [Word.zero] }
  , { name := "gt-uses-unsigned-order"
      opcode := .gt
      input := [w 2, w 1]
      expected := some [Word.one] }
  , { name := "slt-uses-twos-complement-order"
      opcode := .slt
      input := [s (-1), Word.zero]
      expected := some [Word.one] }
  , { name := "slt-false-is-zero"
      opcode := .slt
      input := [Word.zero, s (-1)]
      expected := some [Word.zero] }
  , { name := "sgt-uses-twos-complement-order"
      opcode := .sgt
      input := [Word.zero, s (-1)]
      expected := some [Word.one] }
  , { name := "eq-compares-canonical-words"
      opcode := .eq
      input := [w 9, w 9]
      expected := some [Word.one] }
  , { name := "eq-false-is-zero"
      opcode := .eq
      input := [w 9, w 10]
      expected := some [Word.zero] }
  , { name := "iszero-returns-one"
      opcode := .iszero
      input := [Word.zero]
      expected := some [Word.one] }
  , { name := "iszero-false-is-zero"
      opcode := .iszero
      input := [Word.one]
      expected := some [Word.zero] }
  , { name := "and-keeps-common-bits"
      opcode := .and
      input := [w 0xF0, w 0x0F]
      expected := some [Word.zero] }
  , { name := "or-combines-bits"
      opcode := .or
      input := [w 0xF0, w 0x0F]
      expected := some [w 0xFF] }
  , { name := "xor-keeps-differing-bits"
      opcode := .xor
      input := [w 0xF0, w 0x0F]
      expected := some [w 0xFF] }
  , { name := "not-complements-within-word"
      opcode := .not
      input := [Word.zero]
      expected := some [Word.allOnes] }
  , { name := "byte-indexes-from-most-significant-byte"
      opcode := .byte
      input := [w 30, w 0x1234]
      expected := some [w 0x12] }
  , { name := "byte-outside-word-is-zero"
      opcode := .byte
      input := [w 32, w 0x1234]
      expected := some [Word.zero] }
  , { name := "shl-wraps-at-word-width"
      opcode := .shl
      input := [w 1, Word.minSignedWord]
      expected := some [Word.zero] }
  , { name := "shl-large-shift-is-zero"
      opcode := .shl
      input := [w 256, Word.one]
      expected := some [Word.zero] }
  , { name := "shr-shifts-logically"
      opcode := .shr
      input := [w 1, w 4]
      expected := some [w 2] }
  , { name := "shr-large-shift-is-zero"
      opcode := .shr
      input := [w 256, Word.one]
      expected := some [Word.zero] }
  , { name := "sar-shifts-negative-values-arithmetically"
      opcode := .sar
      input := [w 1, s (-8)]
      expected := some [s (-4)] }
  , { name := "sar-large-negative-shift-is-all-ones"
      opcode := .sar
      input := [w 256, s (-8)]
      expected := some [Word.allOnes] }
  , { name := "sar-large-nonnegative-shift-is-zero"
      opcode := .sar
      input := [w 256, w 8]
      expected := some [Word.zero] }
  , { name := "clz-zero-is-word-width"
      opcode := .clz
      input := [Word.zero]
      expected := some [w 256] }
  , { name := "clz-all-ones-is-zero"
      opcode := .clz
      input := [Word.allOnes]
      expected := some [Word.zero] }
  , { name := "stack-underflow-is-explicit"
      opcode := .add
      input := []
      expected := none } ]

def passes (vector : Vector) : Bool :=
  decide (execute vector.opcode vector.input = vector.expected)

theorem all_pass : vectors.all passes = true := by native_decide

def covers (opcode : Opcode) : Bool :=
  vectors.any fun vector => decide (vector.opcode = opcode)

theorem every_opcode_has_vector : allOpcodes.all covers = true := by native_decide

end BoundaryVector
end PureStack
end Evm
end Eip803x
