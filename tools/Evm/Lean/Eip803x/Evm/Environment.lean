-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Evm.Word

namespace Eip803x
namespace Evm
namespace Environment

open Word

/--
Values exposed by the context-reading opcodes.  The block-hash table is the
result of the pinned production `IBlockhashProvider`; proving that provider is
a separate adapter obligation.
-/
structure Context where
  executingAccount : UInt256
  origin : UInt256
  caller : UInt256
  callValue : UInt256
  calldataSize : Nat
  codeSize : Nat
  returnDataSize : Nat
  gasPrice : UInt256
  coinbase : UInt256
  timestamp : Nat
  number : Nat
  prevRandao : UInt256
  gasLimit : Nat
  chainId : UInt256
  selfBalance : UInt256
  baseFee : UInt256
  blobVersionedHashes : List UInt256
  excessBlobGas : Option Nat
  blobBaseFee : UInt256
  slotNumber : Option Nat
  blockHashes : List (Nat × UInt256)
  deriving Repr

def optionalBounded (limit : Nat) : Option Nat → Prop
  | none => True
  | some value => value < limit

instance optionalBoundedDecidable (limit : Nat) (value : Option Nat) :
    Decidable (optionalBounded limit value) := by
  cases value <;> simp [optionalBounded] <;> infer_instance

def ContextWellFormed (context : Context) : Prop :=
  context.executingAccount.val < 2 ^ 160 ∧
    context.origin.val < 2 ^ 160 ∧
    context.caller.val < 2 ^ 160 ∧
    context.coinbase.val < 2 ^ 160 ∧
    context.calldataSize < 2 ^ 31 ∧
    context.codeSize < 2 ^ 31 ∧
    context.returnDataSize < 2 ^ 31 ∧
    context.timestamp < 2 ^ 64 ∧
    context.number < 2 ^ 64 ∧
    context.gasLimit < 2 ^ 64 ∧
    optionalBounded (2 ^ 64) context.excessBlobGas ∧
    optionalBounded (2 ^ 64) context.slotNumber ∧
    context.blockHashes.all (fun entry => decide (entry.1 < 2 ^ 64)) = true

instance contextWellFormedDecidable (context : Context) :
    Decidable (ContextWellFormed context) := by
  unfold ContextWellFormed
  infer_instance

/-- A production-representable context for the value-selection semantics. -/
structure ValidContext where
  value : Context
  wellFormed : ContextWellFormed value

/--
The production context caches blob base fee, whereas EELS calculates it from
`excess_blob_gas`.  A refinement theorem must supply this relation for the
pinned calculator.
-/
def BlobBaseFeeRepresents (calculate : Nat → UInt256) (context : ValidContext) : Prop :=
  ∀ excess, context.value.excessBlobGas = some excess →
    context.value.blobBaseFee = calculate excess

inductive Opcode where
  | address
  | origin
  | caller
  | callvalue
  | calldatasize
  | codesize
  | gasprice
  | returndatasize
  | blockhash
  | coinbase
  | timestamp
  | number
  | prevrandao
  | gaslimit
  | chainid
  | selfbalance
  | basefee
  | blobhash
  | blobbasefee
  | slotnum
  deriving DecidableEq, Repr

def allOpcodes : List Opcode :=
  [ .address, .origin, .caller, .callvalue, .calldatasize, .codesize, .gasprice
  , .returndatasize, .blockhash, .coinbase, .timestamp, .number, .prevrandao
  , .gaslimit, .chainid, .selfbalance, .basefee, .blobhash, .blobbasefee, .slotnum ]

theorem opcode_count : allOpcodes.length = 20 := by native_decide

inductive Result where
  | word (value : UInt256)
  | stackUnderflow
  | badInstruction
  deriving DecidableEq, Repr

def lookupBlockHash : Nat → List (Nat × UInt256) → Option UInt256
  | _, [] => none
  | number, (candidate, hash) :: tail =>
    if number = candidate then some hash else lookupBlockHash number tail

def blockHash (context : Context) (query : UInt256) : UInt256 :=
  if query.val < 2 ^ 64 ∧ query.val < context.number ∧ context.number ≤ query.val + 256 then
    (lookupBlockHash query.val context.blockHashes).getD Word.zero
  else
    Word.zero

def blobHash (context : Context) (query : UInt256) : UInt256 :=
  context.blobVersionedHashes[query.val]?.getD Word.zero

def requiresArgument : Opcode → Bool
  | .blockhash | .blobhash => true
  | _ => false

/--
Executes the value-selection part of every context-reading opcode enabled by
the pinned Amsterdam dispatch table.  Opcode gas, stack mutation, address
width representation, and provider correctness are composed at later layers.
-/
def execute (validContext : ValidContext) (opcode : Opcode)
    (argument : Option UInt256 := none) : Result :=
  let context := validContext.value
  match opcode with
  | .address => .word context.executingAccount
  | .origin => .word context.origin
  | .caller => .word context.caller
  | .callvalue => .word context.callValue
  | .calldatasize => .word (Word.ofNat context.calldataSize)
  | .codesize => .word (Word.ofNat context.codeSize)
  | .gasprice => .word context.gasPrice
  | .returndatasize => .word (Word.ofNat context.returnDataSize)
  | .blockhash =>
    match argument with
    | some query => .word (blockHash context query)
    | none => .stackUnderflow
  | .coinbase => .word context.coinbase
  | .timestamp => .word (Word.ofNat context.timestamp)
  | .number => .word (Word.ofNat context.number)
  | .prevrandao => .word context.prevRandao
  | .gaslimit => .word (Word.ofNat context.gasLimit)
  | .chainid => .word context.chainId
  | .selfbalance => .word context.selfBalance
  | .basefee => .word context.baseFee
  | .blobhash =>
    match argument with
    | some query => .word (blobHash context query)
    | none => .stackUnderflow
  | .blobbasefee =>
    match context.excessBlobGas with
    | some _ => .word context.blobBaseFee
    | none => .badInstruction
  | .slotnum =>
    match context.slotNumber with
    | some value => .word (Word.ofNat value)
    | none => .badInstruction

theorem blockHash_current_or_future_is_zero (context : Context) (query : UInt256)
    (h : context.number ≤ query.val) :
    blockHash context query = Word.zero := by
  simp [blockHash, Nat.not_lt_of_ge h]

theorem blockHash_unrepresentable_is_zero (context : Context) (query : UInt256)
    (h : 2 ^ 64 ≤ query.val) :
    blockHash context query = Word.zero := by
  simp [blockHash, Nat.not_lt_of_ge h]

theorem blockHash_missing_is_zero (context : Context) (query : UInt256)
    (hWidth : query.val < 2 ^ 64) (hPast : query.val < context.number)
    (hRecent : context.number ≤ query.val + 256)
    (hMissing : lookupBlockHash query.val context.blockHashes = none) :
    blockHash context query = Word.zero := by
  simp [blockHash, hWidth, hPast, hRecent, hMissing]

theorem blockHash_older_than_window_is_zero (context : Context) (query : UInt256)
    (h : query.val + 256 < context.number) :
    blockHash context query = Word.zero := by
  simp [blockHash, Nat.not_le_of_gt h]

theorem blobHash_out_of_range_is_zero (context : Context) (query : UInt256)
    (h : context.blobVersionedHashes.length ≤ query.val) :
    blobHash context query = Word.zero := by
  simp [blobHash, List.getElem?_eq_none h]

theorem indexed_opcode_without_argument_underflows (context : ValidContext) (opcode : Opcode)
    (h : requiresArgument opcode = true) :
    execute context opcode = .stackUnderflow := by
  cases opcode <;> simp_all [requiresArgument, execute]

theorem blobBaseFee_missing_is_bad_instruction (context : ValidContext)
    (h : context.value.excessBlobGas = none) :
    execute context .blobbasefee = .badInstruction := by
  simp [execute, h]

theorem blobBaseFee_uses_related_calculation (context : ValidContext)
    (calculate : Nat → UInt256) (excess : Nat)
    (hRelation : BlobBaseFeeRepresents calculate context)
    (hExcess : context.value.excessBlobGas = some excess) :
    execute context .blobbasefee = .word (calculate excess) := by
  simp [execute, hExcess, hRelation excess hExcess]

theorem slotNumber_missing_is_bad_instruction (context : ValidContext)
    (h : context.value.slotNumber = none) :
    execute context .slotnum = .badInstruction := by
  simp [execute, h]

end Environment
end Evm
end Eip803x
