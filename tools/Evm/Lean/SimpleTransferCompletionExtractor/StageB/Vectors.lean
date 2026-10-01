-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import SimpleTransferCompletionExtractor.StageB.Replay

namespace SimpleTransferCompletionExtractor.StageB.Runtime.Vectors

def gas (value : Nat) (reservoir used spill refunded : Int := 0) : Gas :=
  { value := value, stateReservoir := reservoir, stateGasUsed := used,
    stateGasSpill := spill, stateGasSpillRefunded := refunded }

def input (value : Nat) (gasLimit : Nat := 300000)
    (intrinsic : Intrinsic := { standard := gas 21000, floorGas := gas 21000 }) : Input :=
  let transaction : Transaction :=
    { sender := "sender", recipient := some "recipient", value := value, data := [],
      gasLimit := gasLimit, hasAuthorizationList := false }
  let spec : SpecInput := { eip8037Enabled := true, eip7708Enabled := false }
  let tracer : TracerInput := { state := false, actions := false, code := false, logs := false, access := false }
  { tx := transaction,
    spec := spec,
    tracer := tracer,
    intrinsic := intrinsic, restore := false, commit := true, deleteCallerAccount := false, warmup := false,
    opcodeGasPrice := 2, premiumPerGas := 1, senderReservedGasPayment := 100000,
    blobBaseFee := 0, isCodeOverridable := false, forceSimpleTransferDisabled := false,
    executionGasLimitCap := 16777216, newAccountStateCost := 183600 }

def withRecipient (base : Input) (recipient : String) : Input :=
  { base with tx := { base.tx with recipient := some recipient } }

def replayDecoderLine (access actions : Bool) : String :=
  let flag (value : Bool) := if value then "true" else "false"
  s!"[\"{Replay.schema}\",\"decoder-flags\",\"{Generated.program.prefixIntegrity}\",\"1\",\"1\",[[\"sender\",\"recipient\",\"0\",\"\",\"300000\",false],[true,false],[false,{flag actions},false,false,{flag access}],[[\"21000\",\"0\",\"0\",\"0\",\"0\"],[\"21000\",\"0\",\"0\",\"0\",\"0\"]],false,true,false,false,\"2\",\"1\",\"100000\",\"0\",false,false,\"16777216\",\"183600\"],[]]"

def decodedTracerFlags (access actions : Bool) : Except String (Bool × Bool) := do
  let replay ← Replay.decode (replayDecoderLine access actions)
  pure (replay.input.tracer.access, replay.input.tracer.actions)

def decoderPreservesTracerFlags (access actions : Bool) : Bool :=
  match decodedTracerFlags access actions with
  | .ok (decodedAccess, decodedActions) => decodedAccess == access && decodedActions == actions
  | .error _ => false

def codeAt (recipient : String) (empty : Bool := true) (delegation : Option String := none) : Exchange :=
  { request := .codeLookup recipient false, reply := .codeInfo empty delegation }

def code (empty : Bool := true) (delegation : Option String := none) : Exchange :=
  codeAt "recipient" empty delegation

def trace : Exchange := { request := .incrementEmptyCalls, reply := .unit }
def deadAt (recipient : String) (value : Bool) : Exchange :=
  { request := .isDeadAccount recipient, reply := .boolean value }
def dead (value : Bool) : Exchange := deadAt "recipient" value
def subtract (value : Nat) : Exchange := { request := .subtractBalance "sender" value, reply := .unit }
def addAt (recipient : String) (value : Nat) : Exchange :=
  { request := .addBalance recipient value, reply := .boolean true }
def add (value : Nat) : Exchange := addAt "recipient" value
def commit : Exchange := { request := .commit false false, reply := .unit }

def zeroTape : List Exchange := [code, trace, add 0]
def existingTape : List Exchange := [code, trace, dead false, subtract 7, add 7]
def deadTape : List Exchange := [code, trace, dead true, subtract 7, add 7]
def selfTape : List Exchange := [codeAt "sender", trace]
def oogTape : List Exchange := [code, trace, dead true]

inductive OutcomeTag where
  | suspended | returned | outside | rejected | csharpFuel | microFuel
  deriving DecidableEq, Repr

inductive ProvenanceLabel where
  | substate | gasAvailable | opcodeGasPrice | floorGas | standardGas
  deriving DecidableEq, Repr

structure OperandObservation where
  ordinal : Nat
  mode : PassingMode
  value : String
  locationFields : Option (List String)
  locationReadOnly : Option Bool
  provenance : Option ProvenanceLabel
  deriving DecidableEq, Repr

structure GasObservation where
  value : Nat
  stateReservoir : Int
  stateGasUsed : Int
  stateGasSpill : Int
  stateGasSpillRefunded : Int
  deriving DecidableEq, Repr

structure Observation where
  tag : OutcomeTag
  diagnostic : String
  requests : List Request
  remainingTape : List Exchange
  remainingCSharpFuel : Option Nat
  operands : List OperandObservation
  ordinals : List Nat
  modes : List PassingMode
  gasAvailable : Option GasObservation
  postIntrinsicStateReservoir : Option Int
  substateException : Option Int
  prefixValuesValid : Bool
  provenance : List (Option ProvenanceLabel)
  deriving DecidableEq, Repr

def field? : List (String × Value) → String → Option Value
  | [], _ => none
  | (name, value) :: tail, wanted => if name == wanted then some value else field? tail wanted

def signedField? (value : Value) (name : String) : Option Int :=
  match value with
  | .struct _ fields => match field? fields name with
    | some (.signed result) | some (.enum _ result) => some result
    | _ => none
  | _ => none

def unsignedField? (value : Value) (name : String) : Option Nat :=
  match value with
  | .struct _ fields => match field? fields name with | some (.unsigned result) => some result | _ => none
  | _ => none

def gasObservation? (value : Value) : Option GasObservation := do
  let gasValue ← unsignedField? value "Value"
  let reservoir ← signedField? value "StateReservoir"
  let used ← signedField? value "StateGasUsed"
  let spill ← signedField? value "StateGasSpill"
  let refunded ← signedField? value "StateGasSpillRefunded"
  let observation : GasObservation :=
    { value := gasValue, stateReservoir := reservoir, stateGasUsed := used,
      stateGasSpill := spill, stateGasSpillRefunded := refunded }
  pure observation

def contains (value fragment : String) : Bool := (value.splitOn fragment).length > 1

def provenanceLabel? (value : Option String) : Option ProvenanceLabel :=
  match value with
  | some value =>
      if contains value "::local:substate@" then some .substate
      else if contains value "::parameter:10:gasAvailable:" then some .gasAvailable
      else if contains value "::local:opcodeGasPrice@" then some .opcodeGasPrice
      else if contains value "::local:floorGas@" then some .floorGas
      else if contains value "::local:standardGas@" then some .standardGas
      else none
  | none => none

def encodeString (value : String) : String := s!"{value.length}:{value}"

def encodeValueFuel : Nat → Value → String
  | 0, _ => "depth-exhausted"
  | _fuel + 1, .unit => "u"
  | _fuel + 1, .null typeName => "n" ++ encodeString typeName
  | _fuel + 1, .boolean value => if value then "b1" else "b0"
  | _fuel + 1, .unsigned value => "w" ++ toString value
  | _fuel + 1, .signed value => "i" ++ toString value
  | _fuel + 1, .uint256 value => "q" ++ toString value
  | _fuel + 1, .enum typeName value => "e" ++ encodeString typeName ++ toString value
  | _fuel + 1, .address identity => "a" ++ encodeString identity
  | _fuel + 1, .bytes value => "y" ++ toString value.length ++ ":" ++ String.intercalate "," (value.map toString)
  | _fuel + 1, .reference typeName identity => "r" ++ encodeString typeName ++ encodeString identity
  | fuel + 1, .struct typeName fields =>
      "s" ++ encodeString typeName ++ toString fields.length ++ ":" ++
        String.join (fields.map (fun field => encodeString field.1 ++ encodeString (encodeValueFuel fuel field.2)))

def encodeValue (value : Value) : String := encodeValueFuel 256 value

def observeOperand (operand : RefundOperand) : OperandObservation :=
  { ordinal := operand.ordinal,
    mode := operand.mode,
    value := encodeValue operand.value,
    locationFields := operand.location.map (·.fields),
    locationReadOnly := operand.location.map (·.readOnly),
    provenance := provenanceLabel? operand.provenance }

def exactOperands : Outcome → List OperandObservation
  | .suspended suspension => suspension.operands.map observeOperand
  | _ => []

def getOperand? (operands : List RefundOperand) (ordinal : Nat) : Option RefundOperand :=
  operands.find? (fun operand => operand.ordinal == ordinal)

def refundPrefixValuesValid (operands : List RefundOperand) : Bool :=
  match (getOperand? operands 0).map (·.value), (getOperand? operands 1).map (·.value),
      (getOperand? operands 2).map (·.value), (getOperand? operands 3).map (·.value),
      (getOperand? operands 6).map (·.value), (getOperand? operands 7).map (·.value),
      (getOperand? operands 11).map (·.value) with
  | some (Value.reference _ "tx"), some (Value.reference _ "header"), some (Value.reference _ "spec"),
      some (Value.reference _ "opts"), some (Value.uint256 2), some (Value.unsigned 0), some (Value.boolean false) => true
  | _, _, _, _, _, _, _ => false

def diagnosticClass (reason : String) : String :=
  if contains reason "response type mismatch" then "reply-type"
  else if contains reason "response request mismatch" then "reply-request"
  else if contains reason "C# fuel outside admitted bound" then "csharp-fuel-domain"
  else if contains reason "input domain failed" then "input-domain"
  else reason

def baseObservation (tag : OutcomeTag) (requests : List Request) (remainingTape : List Exchange)
    (diagnostic : String := "") : Observation :=
  { tag := tag, diagnostic := diagnostic, requests := requests, remainingTape := remainingTape,
    remainingCSharpFuel := none, operands := [], ordinals := [], modes := [], gasAvailable := none,
    postIntrinsicStateReservoir := none, substateException := none, prefixValuesValid := false,
    provenance := [] }

def observe : Outcome → Observation
  | .suspended suspension =>
      let operands := suspension.operands
      let gasAvailable := (getOperand? operands 5).bind (fun operand => gasObservation? operand.value)
      let postReservoir := (getOperand? operands 10).bind (fun operand =>
        match operand.value with | .signed value => some value | _ => none)
      let exception := (getOperand? operands 4).bind (fun operand => signedField? operand.value "EvmExceptionType")
      { tag := .suspended, diagnostic := "", requests := suspension.requests,
        remainingTape := suspension.remainingTape,
        remainingCSharpFuel := some suspension.remainingCSharpFuel,
        operands := operands.map observeOperand,
        ordinals := operands.map (·.ordinal), modes := operands.map (·.mode), gasAvailable := gasAvailable,
        postIntrinsicStateReservoir := postReservoir, substateException := exception,
        prefixValuesValid := refundPrefixValuesValid operands,
        provenance := operands.map (fun operand => provenanceLabel? operand.provenance) }
  | .returned _ requests tape => baseObservation .returned requests tape
  | .outside requests tape => baseObservation .outside requests tape
  | .rejected reason requests tape => baseObservation .rejected requests tape (diagnosticClass reason)
  | .fuelExhausted requests tape => baseObservation .csharpFuel requests tape
  | .microFuelExhausted requests tape => baseObservation .microFuel requests tape

def refundModes : List PassingMode :=
  [.value, .value, .value, .value, .readOnlyLocation, .readOnlyLocation,
   .readOnlyLocation, .value, .readOnlyLocation, .readOnlyLocation, .value, .value]

def refundProvenance : List (Option ProvenanceLabel) :=
  [none, none, none, none, some .substate, some .gasAvailable, some .opcodeGasPrice,
   none, some .floorGas, some .standardGas, none, none]

def runtimeGasValue (value : Nat) (reservoir used spill refunded : Int) : Value :=
  .struct "global::Nethermind.Evm.GasPolicy.EthereumGasPolicy"
    [("Value", .unsigned value), ("StateReservoir", .signed reservoir),
     ("StateGasUsed", .signed used), ("StateGasSpill", .signed spill),
     ("StateGasSpillRefunded", .signed refunded)]

def runtimeGas (value : Gas) : Value :=
  runtimeGasValue value.value value.stateReservoir value.stateGasUsed
    value.stateGasSpill value.stateGasSpillRefunded

def substateValue (exception : Int) : Value :=
  .struct "global::Nethermind.Evm.TransactionSubstate"
    [("_logs", .struct "global::Nethermind.Core.Collections.JournalCollection<global::Nethermind.Core.LogEntry>" []),
     ("_destroyList", .null ""), ("Output", .bytes []), ("Refund", .signed 0),
     ("ShouldRevert", .boolean false),
     ("EvmExceptionType", .enum "global::Nethermind.Evm.EvmExceptionType" exception),
     ("Error", .null ""),
     ("_logger", .reference "global::Nethermind.Logging.ILogger" "logger")]

def immediateOperand (ordinal : Nat) (mode : PassingMode) (value : Value) : OperandObservation :=
  { ordinal := ordinal, mode := mode, value := encodeValue value,
    locationFields := none, locationReadOnly := none, provenance := none }

def locatedOperand (ordinal : Nat) (value : Value) (provenance : ProvenanceLabel) : OperandObservation :=
  { ordinal := ordinal, mode := .readOnlyLocation, value := encodeValue value,
    locationFields := some [], locationReadOnly := some true, provenance := some provenance }

def expectedRefundOperands (gasAvailable : Value) (postReservoir exception : Int)
    (standardGas floorGas : Gas) : List OperandObservation :=
  [immediateOperand 0 .value (.reference "global::Nethermind.Core.Transaction" "tx"),
   immediateOperand 1 .value (.reference "global::Nethermind.Core.BlockHeader" "header"),
   immediateOperand 2 .value (.reference "global::Nethermind.Core.Specs.IReleaseSpec" "spec"),
   immediateOperand 3 .value (.reference "global::Nethermind.Evm.TransactionProcessing.ExecutionOptions" "opts"),
   locatedOperand 4 (substateValue exception) .substate,
   locatedOperand 5 gasAvailable .gasAvailable,
   locatedOperand 6 (.uint256 2) .opcodeGasPrice,
   immediateOperand 7 .value (.unsigned 0),
   locatedOperand 8 (runtimeGas floorGas) .floorGas,
   locatedOperand 9 (runtimeGas standardGas) .standardGas,
   immediateOperand 10 .value (.signed postReservoir),
   immediateOperand 11 .value (.boolean false)]

def expectedSuspension (requests : List Request) (fuel gasValue : Nat)
    (reservoir used spill refunded postReservoir exception : Int)
    (standardGas : Gas := gas 21000) (floorGas : Gas := gas 21000)
    (remainingTape : List Exchange := []) : Observation :=
  let observedGas : GasObservation :=
    { value := gasValue, stateReservoir := reservoir, stateGasUsed := used,
      stateGasSpill := spill, stateGasSpillRefunded := refunded }
  { tag := .suspended, diagnostic := "", requests := requests, remainingTape := remainingTape,
    remainingCSharpFuel := some fuel,
    operands := expectedRefundOperands
      (runtimeGasValue gasValue reservoir used spill refunded) postReservoir exception standardGas floorGas,
    ordinals := List.range 12, modes := refundModes,
    gasAvailable := some observedGas,
    postIntrinsicStateReservoir := some postReservoir, substateException := some exception,
    prefixValuesValid := true, provenance := refundProvenance }

def fullFuel : Nat := Generated.program.fuelBound

example : fullFuel = 853 := by native_decide
example : commonDomain (input 0) = true := by native_decide

example : decoderPreservesTracerFlags false false = true := by native_decide
example : decoderPreservesTracerFlags false true = true := by native_decide
example : decoderPreservesTracerFlags true false = true := by native_decide
example : decoderPreservesTracerFlags true true = true := by native_decide

example :
    let base := input 0
    commonDomain { base with tx := { base.tx with gasLimit := 2 ^ 63 } } = false := by native_decide

example :
    let base := input 0
    let changed := { base with
      tx := { base.tx with gasLimit := 20000000 }
      intrinsic := { base.intrinsic with
        standard := { base.intrinsic.standard with value := 16777217 } } }
    commonDomain changed = false := by native_decide

example : observe (runGenerated (input 0) zeroTape fullFuel 20000) =
    expectedSuspension [.codeLookup "recipient" false, .incrementEmptyCalls, .addBalance "recipient" 0]
      357 279000 0 0 0 0 0 0 := by native_decide

example : observe (runGenerated (input 7) existingTape fullFuel 20000) =
    expectedSuspension [.codeLookup "recipient" false, .incrementEmptyCalls, .isDeadAccount "recipient",
      .subtractBalance "sender" 7, .addBalance "recipient" 7] 299 279000 0 0 0 0 0 0 := by native_decide

example : observe (runGenerated (withRecipient (input 7) "sender") selfTape fullFuel 20000) =
    expectedSuspension [.codeLookup "sender" false, .incrementEmptyCalls] 393 279000 0 0 0 0 0 0 := by native_decide

example : observe (runGenerated (input 7) deadTape fullFuel 20000) =
    expectedSuspension [.codeLookup "recipient" false, .incrementEmptyCalls, .isDeadAccount "recipient",
      .subtractBalance "sender" 7, .addBalance "recipient" 7] 226 95400 0 183600 183600 0 0 0 := by native_decide

def spillInput : Input := input 7 300000
  { standard := gas 21000 2 2 0 0, floorGas := gas 21000 }

example : observe (runGenerated spillInput deadTape fullFuel 20000) =
    expectedSuspension [.codeLookup "recipient" false, .incrementEmptyCalls, .isDeadAccount "recipient",
      .subtractBalance "sender" 7, .addBalance "recipient" 7] 226 95398 0 183602 183600 0 0 0
      (standardGas := gas 21000 2 2 0 0) := by native_decide

def reservoirInput : Input := input 7 17277216

example : observe (runGenerated reservoirInput deadTape fullFuel 20000) =
    expectedSuspension [.codeLookup "recipient" false, .incrementEmptyCalls, .isDeadAccount "recipient",
      .subtractBalance "sender" 7, .addBalance "recipient" 7] 226 16756216 316400 183600 0 0 316400 0 := by native_decide

def oogInput : Input := input 7 100000
  { standard := gas 21000 2 2 0 0, floorGas := gas 21000 }

example : observe (runGenerated oogInput oogTape fullFuel 20000) =
    expectedSuspension [.codeLookup "recipient" false, .incrementEmptyCalls, .isDeadAccount "recipient"]
      328 0 0 2 0 0 0 4 (standardGas := gas 21000 2 2 0 0) := by native_decide

example : observe (runGenerated (input 7 204600) deadTape fullFuel 20000) =
    expectedSuspension [.codeLookup "recipient" false, .incrementEmptyCalls, .isDeadAccount "recipient",
      .subtractBalance "sender" 7, .addBalance "recipient" 7] 226 0 0 183600 183600 0 0 0 := by native_decide

example : observe (runGenerated (input 7 204599) oogTape fullFuel 20000) =
    expectedSuspension [.codeLookup "recipient" false, .incrementEmptyCalls, .isDeadAccount "recipient"]
      328 0 0 0 0 0 0 4 := by native_decide

example : observe (runGenerated (input 7) [code false, commit] fullFuel 20000) =
    baseObservation .outside [.codeLookup "recipient" false, .commit false false] [] := by native_decide

example : observe (runGenerated (input 7) [code true (some "delegate"), commit] fullFuel 20000) =
    baseObservation .outside [.codeLookup "recipient" false, .commit false false] [] := by native_decide

def wrongReply : Exchange := { request := .codeLookup "recipient" false, reply := .boolean true }
def wrongRequest : Exchange := { request := .incrementEmptyCalls, reply := .unit }

example : observe (runGenerated (input 0) [wrongReply] fullFuel 20000) =
    baseObservation .rejected [] [wrongReply] "reply-type" := by native_decide

example : observe (runGenerated (input 0) [wrongRequest] fullFuel 20000) =
    baseObservation .rejected [] [wrongRequest] "reply-request" := by native_decide

example : observe (runGenerated (input 0) (zeroTape ++ [commit]) fullFuel 20000) =
    expectedSuspension [.codeLookup "recipient" false, .incrementEmptyCalls, .addBalance "recipient" 0]
      357 279000 0 0 0 0 0 0 (remainingTape := [commit]) := by native_decide

example : observe (runGenerated (input 0) zeroTape 0 20000) =
    baseObservation .rejected [] zeroTape "csharp-fuel-domain" := by native_decide

example : observe (runGenerated (input 0) zeroTape (fullFuel + 1) 20000) =
    baseObservation .rejected [] zeroTape "csharp-fuel-domain" := by native_decide

example : observe (runGenerated (input 0) zeroTape 1 20000) =
    baseObservation .csharpFuel [] zeroTape := by native_decide

example : observe (runGenerated (input 0) zeroTape fullFuel 0) =
    baseObservation .microFuel [] zeroTape := by native_decide

example :
    let changed := { input 0 with executionGasLimitCap := 1 }
    observe (runGenerated changed zeroTape fullFuel 20000) =
      baseObservation .rejected [] zeroTape "input-domain" := by native_decide

example :
    let changed := { input 0 with newAccountStateCost := 1 }
    observe (runGenerated changed zeroTape fullFuel 20000) =
      baseObservation .rejected [] zeroTape "input-domain" := by native_decide

end SimpleTransferCompletionExtractor.StageB.Runtime.Vectors
