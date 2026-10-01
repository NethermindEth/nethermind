-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import SimpleTransferCompletionExtractor.StageB.Syntax

namespace SimpleTransferCompletionExtractor.StageB.Runtime

deriving instance DecidableEq for OperationKind
deriving instance Repr for OperationKind

inductive Value where
  | unit | null (typeName : String) | boolean (value : Bool) | unsigned (value : Nat) | signed (value : Int)
  | uint256 (value : Nat) | enum (typeName : String) (value : Int) | address (identity : String)
  | bytes (value : List UInt8) | reference (typeName identity : String) | struct (typeName : String) (fields : List (String × Value))

structure Gas where
  value : Nat
  stateReservoir : Int
  stateGasUsed : Int
  stateGasSpill : Int
  stateGasSpillRefunded : Int
  deriving DecidableEq, Repr

structure Intrinsic where
  standard : Gas
  floorGas : Gas
  deriving DecidableEq, Repr

structure Transaction where
  sender : String
  recipient : Option String
  value : Nat
  data : List UInt8
  gasLimit : Nat
  hasAuthorizationList : Bool
  deriving DecidableEq, Repr

structure SpecInput where
  eip8037Enabled : Bool
  eip7708Enabled : Bool
  deriving DecidableEq, Repr

structure TracerInput where
  state : Bool
  actions : Bool
  code : Bool
  logs : Bool
  access : Bool
  deriving DecidableEq, Repr

structure Input where
  tx : Transaction
  spec : SpecInput
  tracer : TracerInput
  intrinsic : Intrinsic
  restore : Bool
  commit : Bool
  deleteCallerAccount : Bool
  warmup : Bool
  opcodeGasPrice : Nat
  premiumPerGas : Nat
  senderReservedGasPayment : Nat
  blobBaseFee : Nat
  isCodeOverridable : Bool
  forceSimpleTransferDisabled : Bool
  executionGasLimitCap : Nat
  newAccountStateCost : Int
  deriving DecidableEq, Repr

inductive Request where
  | codeLookup (recipient : String) (followDelegation : Bool)
  | incrementEmptyCalls
  | isDeadAccount (recipient : String)
  | subtractBalance (address : String) (amount : Nat)
  | addBalance (address : String) (amount : Nat)
  | commit (tracingState commitRoots : Bool)
  deriving DecidableEq, Repr

inductive Reply where
  | codeInfo (isEmpty : Bool) (delegation : Option String)
  | boolean (value : Bool)
  | unit
  deriving DecidableEq, Repr

structure Exchange where
  request : Request
  reply : Reply
  deriving DecidableEq, Repr

inductive PassingMode where | value | readOnlyLocation deriving DecidableEq, Repr

abbrev FrameId := Nat
abbrev CellId := Nat

structure Location where
  root : CellId
  fields : List String
  readOnly : Bool
  provenance : String
  deriving DecidableEq, Repr

inductive Operand where
  | immediate (value : Value)
  | location (value : Location)

structure RefundOperand where
  ordinal : Nat
  mode : PassingMode
  value : Value
  location : Option Location
  provenance : Option String

structure Suspension where
  operands : List RefundOperand
  requests : List Request
  remainingTape : List Exchange
  remainingCSharpFuel : Nat

inductive Outcome where
  | suspended (value : Suspension)
  | returned (value : Value) (requests : List Request) (remainingTape : List Exchange)
  | outside (requests : List Request) (remainingTape : List Exchange)
  | rejected (reason : String) (requests : List Request) (remainingTape : List Exchange)
  | fuelExhausted (requests : List Request) (remainingTape : List Exchange)
  | microFuelExhausted (requests : List Request) (remainingTape : List Exchange)

inductive RejectionCode where
  | inputDomain | fuelDomain | tapeEmpty | tapeRequest | tapeReply | unexpected
  deriving DecidableEq, Repr

structure Rejection where
  code : RejectionCode
  detail : String

structure Execution where
  outcome : Outcome
  remainingCSharpFuel : Nat
  rejection : Option RejectionCode := none

end SimpleTransferCompletionExtractor.StageB.Runtime
