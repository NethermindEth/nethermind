-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Gas
import Eip803x.Evm.MemoryStackControl

namespace Eip803x
namespace Evm
namespace CallCreateExecution

open MemoryStackControl

/-!
Representation types for the generated operational kernel. This module contains
no executable opcode transition and deliberately does not import either
handwritten refinement target.
-/

abbrev Address := UInt256

inductive CallKind where
  | call | callcode | delegatecall | staticcall
  deriving DecidableEq, Repr

inductive CreateKind where
  | create | create2
  deriving DecidableEq, Repr

inductive Opcode where
  | call | callcode | delegatecall | staticcall | create | create2 | selfdestruct
  deriving DecidableEq, Repr

inductive DispatchTable where
  | noTrace | noTraceCancelable | traced | tracedCancelable
  deriving DecidableEq, Repr

/-- Shared representation of the configurable production gas and limit inputs.
    The generated kernel and independent reference each own their Amsterdam
    instance and executable transition. -/
structure Schedule where
  callBase : Nat
  callValue : Nat
  callStipend : Nat
  warmAccess : Nat
  coldAccess : Nat
  createAccess : Nat
  initCodeWord : Nat
  create2HashWord : Nat
  accountWrite : Nat
  selfDestructBase : Nat
  newAccountState : Nat
  createState : Nat
  codeDepositExecutionPerWord : Nat
  codeDepositStatePerByte : Nat
  memoryLinear : Nat
  memoryQuadraticDivisor : Nat
  maxMemorySize : Nat
  maxInitCodeSize : Nat
  maxCodeSize : Nat
  maxCallDepth : Nat
  stackLimit : Nat
  deriving DecidableEq, Repr

inductive CodeRoute where
  | empty | bytecode | precompile | delegatedEmpty | delegatedBytecode
  deriving DecidableEq, Repr

inductive CollisionKind where
  | none | code | nonce | storage
  deriving DecidableEq, Repr

inductive RuntimeCodeKind where
  | empty | valid | invalid
  deriving DecidableEq, Repr

inductive CreateDepositPhase where
  | refundChild
  | chargeParentExecution
  | chargeParentState
  | commitChild
  | repayStateSpill
  deriving DecidableEq, Repr

inductive ChildExit where
  | success | revert | exceptional
  deriving DecidableEq, Repr

inductive Status where
  | continued
  | suspended
  | stopped
  | stackUnderflow
  | stackOverflow
  | staticViolation
  | outOfGas
  | invalidCode
  | notEnoughBalance
  | oracleMismatch
  | extractionMismatch
  deriving DecidableEq, Repr

inductive TraceEvent where
  | instructionStart (opcode : Opcode) (pc gasLeft : Nat)
  | instructionFinish (gasLeft : Nat)
  | instructionError (status : Status)
  | stackPush (bytes : List Byte)
  | actionStart (kind : String) (gasLeft : Nat) (target : Address)
  | actionEnd (gasLeft : Nat) (target : Address) (output : List Byte)
  | actionRevert (gasLeft : Nat) (output : List Byte)
  | actionError (status : Status)
  | transfer (source target : Address) (value : UInt256)
  | selfdestruct (source target : Address) (value : UInt256)
  | extraGasPressure (amount : Nat)
  | gasUpdate (refund gasLeft : Nat)
  | memoryInspect (offset : Nat) (bytes : List Byte)
  | memoryWrite (offset : Nat) (bytes : List Byte)
  deriving DecidableEq, Repr

structure Environment where
  executingAccount : Address
  caller : Address
  value : UInt256
  callDepth : Nat
  isStatic : Bool
  deriving DecidableEq, Repr

structure WorldFacts where
  callerBalance : Nat
  targetPhysicalExists : Bool
  targetLogicalExists : Bool
  targetDead : Bool
  targetCodeRoute : CodeRoute
  delegatedAddress : Option Address
  beneficiaryPhysicalExists : Bool
  beneficiaryDead : Bool
  creatorNonce : Nat
  createPhysicalExists : Bool
  createLogicalExists : Bool
  createCollision : CollisionKind
  createdInTransaction : Bool
  deriving DecidableEq, Repr

structure WorldToken where
  checkpoint : Nat
  current : Nat
  balances : Nat
  nonces : Nat
  code : Nat
  storage : Nat
  deriving DecidableEq, Repr

structure JournalToken where
  warmAddresses : List Address
  destroyList : List Address
  transferLogCount : Nat
  selfdestructLogCount : Nat
  refundCounter : Int
  deriving DecidableEq, Repr

structure CallOperands where
  requestedGas : UInt256
  codeSource : Address
  value : UInt256
  inputOffset : UInt256
  inputLength : UInt256
  outputOffset : UInt256
  outputLength : UInt256
  deriving DecidableEq, Repr

structure CreateOperands where
  value : UInt256
  initOffset : UInt256
  initLength : UInt256
  salt : Option UInt256
  deriving DecidableEq, Repr

structure ChildFrame where
  opcode : Opcode
  gasEntry : FrameEntry
  target : Address
  codeSource : Address
  caller : Address
  value : UInt256
  input : List Byte
  callDepth : Nat
  isStatic : Bool
  outputOffset : Nat
  outputLength : Nat
  snapshot : WorldToken
  journalSnapshot : JournalToken
  isPrecompile : Bool
  newAccountCharged : Bool
  createStateCharged : Bool
  createOnPhysicalAccount : Bool
  deriving DecidableEq, Repr

structure MachineState where
  pc : Nat
  opcodeCount : Nat
  gas : GasState
  stack : Stack
  memory : Memory
  environment : Environment
  world : WorldToken
  journal : JournalToken
  returnData : List Byte
  stagedChild : Option ChildFrame
  traceActions : Bool
  traceAccess : Bool
  traceRefunds : Bool
  trace : List TraceEvent
  deriving Repr

/-- Result representation shared by the independent and extracted machine-state
    charging functions. It contains no charging policy. -/
inductive Debit where
  | paid (state : MachineState)
  | outOfGas (state : MachineState)
  deriving Repr

/-- Result representation shared by independent and extracted memory expansion. -/
inductive Expansion where
  | invalid
  | prepared (cost : Nat) (memory : Memory)
  deriving Repr

/-- Result representation shared by independent and extracted child-frame charging. -/
inductive FrameDebit where
  | paid (state : FrameGasState)
  | outOfGas (state : FrameGasState)
  deriving Repr

structure HandlerOracle where
  facts : WorldFacts
  derivedCreateAddress : Address
  initCodeReadable : Bool
  precompileSuccess : Bool
  precompileGasRemaining : Nat
  precompileOutput : List Byte
  rollbackWorld : WorldToken
  nextWorld : WorldToken
  nextJournal : JournalToken
  deriving Repr

structure ChildOutcome where
  exit : ChildExit
  exceptionStatus : Status
  gas : FrameGasState
  world : WorldToken
  journal : JournalToken
  output : List Byte
  runtimeCodeKind : RuntimeCodeKind
  deriving Repr

structure Outcome where
  status : Status
  state : MachineState
  deriving Repr

end CallCreateExecution
end Evm
end Eip803x
