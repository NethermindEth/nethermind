-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Evm.Environment

namespace Eip803x
namespace Evm
namespace Environment
namespace BoundaryVector

open Word

def w (value : Nat) : UInt256 := Word.ofNat value

def rawContext : Context where
  executingAccount := w 0x11
  origin := w 0x22
  caller := w 0x33
  callValue := w 0x44
  calldataSize := 5
  codeSize := 6
  returnDataSize := 7
  gasPrice := w 8
  coinbase := w 0x99
  timestamp := 10
  number := 1000
  prevRandao := w 0xaa
  gasLimit := 30_000_000
  chainId := w 1
  selfBalance := w 12
  baseFee := w 13
  blobVersionedHashes := [w 0x101, w 0x202]
  excessBlobGas := some 42
  blobBaseFee := w 142
  slotNumber := some 15
  blockHashes :=
    [(1001, w 0x1001), (1000, w 0x1000), (999, w 0x999), (744, w 0x744),
      (743, w 0x743)]

theorem rawContext_well_formed : ContextWellFormed rawContext := by native_decide

def context : ValidContext := ⟨rawContext, rawContext_well_formed⟩

structure Vector where
  name : String
  opcode : Opcode
  argument : Option UInt256
  expected : Result
  deriving DecidableEq, Repr

def vectors : List Vector :=
  [ { name := "address", opcode := .address, argument := none, expected := .word (w 0x11) }
  , { name := "origin", opcode := .origin, argument := none, expected := .word (w 0x22) }
  , { name := "caller", opcode := .caller, argument := none, expected := .word (w 0x33) }
  , { name := "callvalue", opcode := .callvalue, argument := none, expected := .word (w 0x44) }
  , { name := "calldatasize", opcode := .calldatasize, argument := none, expected := .word (w 5) }
  , { name := "codesize", opcode := .codesize, argument := none, expected := .word (w 6) }
  , { name := "gasprice", opcode := .gasprice, argument := none, expected := .word (w 8) }
  , { name := "returndatasize", opcode := .returndatasize, argument := none, expected := .word (w 7) }
  , { name := "blockhash-present", opcode := .blockhash, argument := some (w 999), expected := .word (w 0x999) }
  , { name := "blockhash-current", opcode := .blockhash, argument := some (w 1000), expected := .word Word.zero }
  , { name := "blockhash-missing", opcode := .blockhash, argument := some (w 998), expected := .word Word.zero }
  , { name := "blockhash-oldest-in-window", opcode := .blockhash, argument := some (w 744), expected := .word (w 0x744) }
  , { name := "blockhash-too-old", opcode := .blockhash, argument := some (w 743), expected := .word Word.zero }
  , { name := "blockhash-underflow", opcode := .blockhash, argument := none, expected := .stackUnderflow }
  , { name := "coinbase", opcode := .coinbase, argument := none, expected := .word (w 0x99) }
  , { name := "timestamp", opcode := .timestamp, argument := none, expected := .word (w 10) }
  , { name := "number", opcode := .number, argument := none, expected := .word (w 1000) }
  , { name := "prevrandao", opcode := .prevrandao, argument := none, expected := .word (w 0xaa) }
  , { name := "gaslimit", opcode := .gaslimit, argument := none, expected := .word (w 30_000_000) }
  , { name := "chainid", opcode := .chainid, argument := none, expected := .word (w 1) }
  , { name := "selfbalance", opcode := .selfbalance, argument := none, expected := .word (w 12) }
  , { name := "basefee", opcode := .basefee, argument := none, expected := .word (w 13) }
  , { name := "blobhash-present", opcode := .blobhash, argument := some (w 1), expected := .word (w 0x202) }
  , { name := "blobhash-out-of-range", opcode := .blobhash, argument := some (w 2), expected := .word Word.zero }
  , { name := "blobhash-underflow", opcode := .blobhash, argument := none, expected := .stackUnderflow }
  , { name := "blobbasefee", opcode := .blobbasefee, argument := none, expected := .word (w 142) }
  , { name := "slotnum", opcode := .slotnum, argument := none, expected := .word (w 15) } ]

def passes (vector : Vector) : Bool :=
  decide (execute context vector.opcode vector.argument = vector.expected)

def covers (opcode : Opcode) : Bool :=
  vectors.any fun vector => decide (vector.opcode = opcode)

theorem all_pass : vectors.all passes = true := by native_decide

theorem every_opcode_has_vector : allOpcodes.all covers = true := by native_decide

theorem missing_optional_header_fields_are_bad_instructions :
    execute ⟨{ rawContext with excessBlobGas := none }, by native_decide⟩ .blobbasefee =
        .badInstruction ∧
      execute ⟨{ rawContext with slotNumber := none }, by native_decide⟩ .slotnum =
        .badInstruction := by
  native_decide

theorem unrepresentable_block_number_is_zero :
    execute context .blockhash (some (w (2 ^ 64))) = .word Word.zero := by
  native_decide

def calculateBlobBaseFee (excess : Nat) : UInt256 := w (excess + 100)

theorem blob_base_fee_adapter_relation :
    BlobBaseFeeRepresents calculateBlobBaseFee context := by
  intro excess h
  simp [context, rawContext] at h
  subst excess
  native_decide

theorem blob_base_fee_uses_related_value :
    execute context .blobbasefee = .word (calculateBlobBaseFee 42) := by
  exact blobBaseFee_uses_related_calculation context calculateBlobBaseFee 42
    blob_base_fee_adapter_relation rfl

theorem impossible_width_contexts_are_rejected :
    ¬ContextWellFormed { rawContext with executingAccount := w (2 ^ 160) } ∧
      ¬ContextWellFormed { rawContext with calldataSize := 2 ^ 31 } ∧
      ¬ContextWellFormed { rawContext with number := 2 ^ 64 } ∧
      ¬ContextWellFormed { rawContext with slotNumber := some (2 ^ 64) } := by
  native_decide

end BoundaryVector
end Environment
end Evm
end Eip803x
