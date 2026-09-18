-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.AccountPricing
import Eip803x.Evm.AccountReadTypes
import Eip803x.Evm.MemoryGas

namespace Eip803x
namespace Evm
namespace AccountReadStack

open GasMachine
open MemoryStackControl
open Word

structure Schedule where
  accountReadBase : Nat
  coldAccountAccess : Nat
  warmAccess : Nat
  copyWord : Nat
  memory : MemoryGas.Schedule
  veryLow : Nat
  deriving DecidableEq, Repr

namespace Schedule

def amsterdam : Schedule where
  accountReadBase := 0
  coldAccountAccess := 3000
  warmAccess := 100
  copyWord := 3
  memory := MemoryGas.Schedule.amsterdam
  veryLow := 3

end Schedule

abbrev AccountView := AccountReadTypes.AccountView
abbrev Provider := AccountReadTypes.Provider

inductive Opcode where
  | balance
  | extCodeHash
  | extCodeSize
  | extCodeCopy
  deriving DecidableEq, Repr

abbrev Status := AccountReadTypes.Status
abbrev State := AccountReadTypes.State
abbrev Outcome := AccountReadTypes.Outcome

def addressModulus : Nat := 2 ^ 160

def addressOfWord (word : UInt256) : UInt256 :=
  Word.ofNat (word.val % addressModulus)

def enter (state : State) : State :=
  { state with pc := state.pc + 1, opcodeCount := state.opcodeCount + 1 }

def exhaust (state : State) : State :=
  { state with gas := { state.gas with gasLeft := 0 } }

def charge (amount : Nat) (state : State) : Except Outcome State :=
  match chargeExecution amount state.gas with
  | .error _ => .error { status := .outOfGas, state := exhaust state }
  | .ok gas => .ok { state with gas }

def containsAddress (addresses : List UInt256) (address : UInt256) : Bool :=
  addresses.contains address

def warmAccount (addresses : List UInt256) (address : UInt256) : List UInt256 :=
  if containsAddress addresses address then addresses else address :: addresses

def accessIsCold (provider : Provider) (state : State) (address : UInt256) : Bool :=
  !(containsAddress state.warmAccounts address) && !(provider address).precompile

def accountAccessCharge (schedule : Schedule) (provider : Provider) (state : State)
    (address : UInt256) : Nat :=
  if accessIsCold provider state address then schedule.coldAccountAccess else schedule.warmAccess

def warmForAccess (state : State) (address : UInt256) : State :=
  { state with warmAccounts := warmAccount state.warmAccounts address }

def recordAccountRead (state : State) (address : UInt256) : State :=
  { state with accountReads := state.accountReads ++ [address] }

def recordBytecodeRead (state : State) (address : UInt256) : State :=
  { state with bytecodeReads := state.bytecodeReads ++ [address] }

def pushKnownRoom (stack : Stack) (value : UInt256) : Stack :=
  Stack.fromWords (value :: stack.words)

def valueFor (provider : Provider) (opcode : Opcode) (address : UInt256) : UInt256 :=
  let account := provider address
  match opcode with
  | .balance => account.balance
  | .extCodeHash => if account.dead then Word.zero else account.codeHash
  | .extCodeSize => Word.ofNat account.code.length
  | .extCodeCopy => Word.zero

def secondReadCharge (schedule : Schedule) : Opcode → Nat
  | .extCodeSize | .extCodeCopy => schedule.warmAccess
  | _ => 0

def isFusionOpcode (value : Byte) : Bool :=
  value = MemoryStackControl.byte 0x15 || value = MemoryStackControl.byte 0x11 ||
    value = MemoryStackControl.byte 0x14

def topIsZero (stack : Stack) : Bool :=
  match stack.words.head? with
  | none => false
  | some value => value.val = 0

def fusionResult (next : Byte) (isContract : Bool) : UInt256 :=
  let condition := if next = MemoryStackControl.byte 0x11 then isContract else !isContract
  if condition then Word.one else Word.zero

def executeRead (schedule : Schedule) (provider : Provider) (instructionTracing : Bool)
    (code : List Byte) (opcode : Opcode) (state : State) : Outcome :=
  let entered := enter state
  match charge schedule.accountReadBase entered with
  | .error outcome => outcome
  | .ok baseCharged =>
    match baseCharged.stack.pop with
    | none => { status := .stackUnderflow, state := baseCharged }
    | some (rawAddress, tail) =>
      let address := addressOfWord rawAddress
      let access := accountAccessCharge schedule provider baseCharged address
      let warmed := warmForAccess { baseCharged with stack := tail } address
      match charge access warmed with
      | .error outcome => outcome
      | .ok accessCharged =>
        match charge (secondReadCharge schedule opcode) accessCharged with
        | .error outcome => outcome
        | .ok charged =>
          let readState :=
            if opcode = .extCodeSize then recordAccountRead charged address else charged
          if opcode = .extCodeSize && !instructionTracing then
            match code[entered.pc]? with
            | some next =>
              if isFusionOpcode next &&
                  (next = MemoryStackControl.byte 0x15 || topIsZero tail) then
                let fusionTail := if next = MemoryStackControl.byte 0x15 then tail else Stack.fromWords tail.words.tail
                let fused := { readState with pc := readState.pc + 1, opcodeCount := readState.opcodeCount + 1, stack := fusionTail }
                match charge schedule.veryLow fused with
                | .error outcome => outcome
                | .ok fusionCharged =>
                  { status := .ok, state := { fusionCharged with
                      stack := pushKnownRoom fusionCharged.stack
                        (fusionResult next (provider address).isContract) } }
              else
                { status := .ok, state := { readState with
                    stack := pushKnownRoom tail (valueFor provider opcode address) } }
            | none =>
              { status := .ok, state := { readState with
                  stack := pushKnownRoom tail (valueFor provider opcode address) } }
          else
            { status := .ok, state := { readState with
                stack := pushKnownRoom tail (valueFor provider opcode address) } }

def copyWords (length : UInt256) : Option Nat :=
  if length.val ≤ (2 ^ 32 - 1) * 32 then some ((length.val + 31) / 32) else none

def copyBytes (code : List Byte) (source length : Nat) : List Byte :=
  MemoryStackControl.readRange code source length

def executeExtCodeCopy (schedule : Schedule) (provider : Provider) (state : State) : Outcome :=
  let entered := enter state
  match entered.stack.pop with
  | none => { status := .stackUnderflow, state := entered }
  | some (rawAddress, afterAddress) =>
    match afterAddress.popThree with
    | none => { status := .stackUnderflow, state := { entered with stack := afterAddress } }
    | some (destination, source, length, tail) =>
      let popped := { entered with stack := tail }
      match copyWords length with
      | none => { status := .outOfGas, state := exhaust popped }
      | some words =>
        match charge (schedule.accountReadBase + schedule.copyWord * words) popped with
        | .error outcome => outcome
        | .ok copyCharged =>
          let address := addressOfWord rawAddress
          let access := accountAccessCharge schedule provider copyCharged address
          let warmed := warmForAccess copyCharged address
          match charge access warmed with
          | .error outcome => outcome
          | .ok accessCharged =>
            match charge schedule.warmAccess accessCharged with
            | .error outcome => outcome
            | .ok secondCharged =>
              if length.val = 0 then
                { status := .ok, state := recordBytecodeRead
                    (recordAccountRead secondCharged address) address }
              else
                match MemoryGas.prepare schedule.memory secondCharged.memory destination length with
                | none => { status := .outOfGas, state := exhaust secondCharged }
                | some (memoryCost, expanded) =>
                  let memoryPrepared := { secondCharged with memory := expanded }
                  match charge memoryCost memoryPrepared with
                  | .error outcome => outcome
                  | .ok memoryCharged =>
                    let code := (provider address).code
                    { status := .ok, state := recordAccountRead
                        { memoryCharged with memory := memoryCharged.memory.writeRange destination.val (copyBytes code source.val length.val) } address }

def execute (schedule : Schedule) (provider : Provider) (instructionTracing : Bool)
    (code : List Byte) (opcode : Opcode) (state : State) : Outcome :=
  match opcode with
  | .extCodeCopy => executeExtCodeCopy schedule provider state
  | _ => executeRead schedule provider instructionTracing code opcode state

theorem balance_price_matches_account_pricing (access : AccessStatus) :
    (match access with | .cold => Schedule.amsterdam.coldAccountAccess | .warm => Schedule.amsterdam.warmAccess) =
      (AccountPricing.priceBalance AccountPricing.AccountGasSchedule.amsterdam access).executionCharge := by
  cases access <;> rfl

theorem extCodeHash_price_matches_account_pricing (access : AccessStatus) :
    (match access with | .cold => Schedule.amsterdam.coldAccountAccess | .warm => Schedule.amsterdam.warmAccess) =
      (AccountPricing.priceExtCodeHash AccountPricing.AccountGasSchedule.amsterdam access).executionCharge := by
  cases access <;> rfl

theorem extCodeSize_price_matches_account_pricing (access : AccessStatus) :
    (match access with | .cold => Schedule.amsterdam.coldAccountAccess | .warm => Schedule.amsterdam.warmAccess) +
        Schedule.amsterdam.warmAccess =
      (AccountPricing.priceExtCodeSize AccountPricing.AccountGasSchedule.amsterdam access).executionCharge := by
  cases access <;> rfl

theorem extCodeCopy_price_matches_account_pricing (access : AccessStatus) :
    (match access with | .cold => Schedule.amsterdam.coldAccountAccess | .warm => Schedule.amsterdam.warmAccess) +
        Schedule.amsterdam.warmAccess =
      (AccountPricing.priceExtCodeCopy AccountPricing.AccountGasSchedule.amsterdam access).executionCharge := by
  cases access <;> rfl

end AccountReadStack
end Evm
end Eip803x
