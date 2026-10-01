-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Lean.Data.Json
import SimpleTransferCompletionExtractor.StageB.Semantics

open Lean
open SimpleTransferCompletionExtractor.StageB
open SimpleTransferCompletionExtractor.StageB.Runtime

namespace SimpleTransferCompletionExtractor.StageB.Runtime.Replay

def schema := "stage-b-replay-v2"

structure Case where
  id : String
  fuel : Nat
  microFuel : Nat
  input : Input
  tape : List Exchange

private def arr (value : Json) : Except String (List Json) :=
  match value with | .arr values => pure values.toList | _ => throw "array required"

private def text (value : Json) : Except String String :=
  match value with | .str value => pure value | _ => throw "string required"

private def boolean (value : Json) : Except String Bool :=
  match value with | .bool value => pure value | _ => throw "boolean required"

private def optionalText (value : Json) : Except String (Option String) :=
  match value with | .null => pure none | value => some <$> text value

private def unsigned (bits : Nat) (value : Json) : Except String Nat := do
  let source ← text value
  let some number := source.toNat? | throw "unsigned decimal required"
  if source != toString number || number >= 2 ^ bits then throw "unsigned decimal range"
  pure number

private def signed (value : Json) : Except String Int := do
  let source ← text value
  let some number := source.toInt? | throw "signed decimal required"
  if source != toString number || number < -(2 ^ 63) || number >= 2 ^ 63 then throw "signed decimal range"
  pure number

private def hexDigit (value : Char) : Except String Nat :=
  if '0' <= value && value <= '9' then pure (value.toNat - '0'.toNat)
  else if 'a' <= value && value <= 'f' then pure (value.toNat - 'a'.toNat + 10)
  else throw "lowercase byte hex required"

private def escapedScalar (a b c d : Char) : Except String Nat := do
  return (← hexDigit a.toLower) * 4096 + (← hexDigit b.toLower) * 256 +
    (← hexDigit c.toLower) * 16 + (← hexDigit d.toLower)

-- Lean's JSON parser replaces lone surrogate escapes; the shared wire domain rejects them.
private def validateEscapes : List Char → Except String Unit
  | '\\' :: 'u' :: a :: b :: c :: d :: rest => do
      let code ← escapedScalar a b c d
      if 0xD800 <= code && code <= 0xDBFF then
        match rest with
        | '\\' :: 'u' :: a :: b :: c :: d :: rest => do
            let low ← escapedScalar a b c d
            if low < 0xDC00 || low > 0xDFFF then throw "Unicode scalar required"
            validateEscapes rest
        | _ => throw "Unicode scalar required"
      else if 0xDC00 <= code && code <= 0xDFFF then throw "Unicode scalar required"
      else validateEscapes rest
  | '\\' :: _ :: rest => validateEscapes rest
  | _ :: rest => validateEscapes rest
  | [] => pure ()

private def bytes (value : Json) : Except String (List UInt8) := do
  let source ← text value
  let rec loop : List Char → Except String (List UInt8)
    | [] => pure []
    | high :: low :: rest => do
        let high ← hexDigit high
        let low ← hexDigit low
        pure (UInt8.ofNat (high * 16 + low) :: (← loop rest))
    | _ => throw "odd byte hex length"
  loop source.toList

private def gas (value : Json) : Except String Gas := do
  let [amount, reservoir, used, spill, refunded] ← arr value | throw "gas shape"
  return {
    value := ← unsigned 64 amount, stateReservoir := ← signed reservoir,
    stateGasUsed := ← signed used, stateGasSpill := ← signed spill, stateGasSpillRefunded := ← signed refunded }

private def input (value : Json) : Except String Input := do
  let [tx, spec, tracer, intrinsic, restore, commit, deleteCaller, warmup, price, premium, reserved, blob,
    overrideCode, forceDisabled, cap, cost] ← arr value | throw "input shape"
  let [sender, recipient, amount, data, limit, authorization] ← arr tx | throw "transaction shape"
  let [eip8037, eip7708] ← arr spec | throw "spec shape"
  let [stateTrace, actionTrace, codeTrace, logTrace, accessTrace] ← arr tracer | throw "tracer shape"
  let [standard, floor] ← arr intrinsic | throw "intrinsic shape"
  let tx : Transaction := {
    sender := ← text sender, recipient := ← optionalText recipient, value := ← unsigned 256 amount,
    data := ← bytes data, gasLimit := ← unsigned 64 limit, hasAuthorizationList := ← boolean authorization }
  let spec : SpecInput := { eip8037Enabled := ← boolean eip8037, eip7708Enabled := ← boolean eip7708 }
  let tracer : TracerInput := {
    state := ← boolean stateTrace, actions := ← boolean actionTrace, code := ← boolean codeTrace,
    logs := ← boolean logTrace, access := ← boolean accessTrace }
  return {
    tx, spec, tracer, intrinsic := { standard := ← gas standard, floorGas := ← gas floor },
    restore := ← boolean restore, commit := ← boolean commit, deleteCallerAccount := ← boolean deleteCaller,
    warmup := ← boolean warmup, opcodeGasPrice := ← unsigned 256 price, premiumPerGas := ← unsigned 256 premium,
    senderReservedGasPayment := ← unsigned 256 reserved, blobBaseFee := ← unsigned 256 blob,
    isCodeOverridable := ← boolean overrideCode, forceSimpleTransferDisabled := ← boolean forceDisabled,
    executionGasLimitCap := ← unsigned 64 cap, newAccountStateCost := ← signed cost }

private def request (value : Json) : Except String Request := do
  match ← arr value with
  | [.str "code", recipient, follow] => return .codeLookup (← text recipient) (← boolean follow)
  | [.str "empty-calls"] => pure .incrementEmptyCalls
  | [.str "dead", recipient] => return .isDeadAccount (← text recipient)
  | [.str "subtract", address, amount] => return .subtractBalance (← text address) (← unsigned 256 amount)
  | [.str "add", address, amount] => return .addBalance (← text address) (← unsigned 256 amount)
  | [.str "commit", tracer, roots] => return .commit (← boolean tracer) (← boolean roots)
  | _ => throw "request shape"

private def reply (value : Json) : Except String Reply := do
  match ← arr value with
  | [.str "code", empty, delegation] => return .codeInfo (← boolean empty) (← optionalText delegation)
  | [.str "bool", value] => return .boolean (← boolean value)
  | [.str "unit"] => pure .unit
  | _ => throw "reply shape"

private def exchange (value : Json) : Except String Exchange := do
  let [req, rep] ← arr value | throw "exchange shape"
  return { request := ← request req, reply := ← reply rep }

def decode (line : String) : Except String Case := do
  if line.utf8ByteSize > 1048576 then throw "line length"
  validateEscapes line.toList
  let [version, id, integrity, fuel, microFuel, inp, tape] ← arr (← Json.parse line) | throw "replay shape"
  if (← text version) != schema || (← text integrity) != Generated.program.prefixIntegrity then throw "schema or program identity"
  let tape ← arr tape
  if tape.length > 4096 then throw "tape length"
  let microFuel ← unsigned 63 microFuel
  if microFuel > 1000000 then throw "micro-fuel limit"
  return {
    id := ← text id, fuel := ← unsigned 63 fuel, microFuel, input := ← input inp,
    tape := ← tape.mapM exchange }

private def array (values : List Json) : Json := .arr values.toArray
private def number (value : Int) : Json := .str (toString value)
private def optional (value : Option String) : Json := value.map Json.str |>.getD .null

private def encodeBytes (values : List UInt8) : String :=
  let digits := "0123456789abcdef".toList.toArray
  String.ofList (values.flatMap (fun value => [digits[value.toNat / 16]!, digits[value.toNat % 16]!]))

private def encodeRequest : Request → Json
  | .codeLookup recipient follow => array [.str "code", .str recipient, .bool follow]
  | .incrementEmptyCalls => array [.str "empty-calls"]
  | .isDeadAccount recipient => array [.str "dead", .str recipient]
  | .subtractBalance address amount => array [.str "subtract", .str address, number amount]
  | .addBalance address amount => array [.str "add", .str address, number amount]
  | .commit tracer roots => array [.str "commit", .bool tracer, .bool roots]

private def encodeReply : Reply → Json
  | .codeInfo empty delegation => array [.str "code", .bool empty, optional delegation]
  | .boolean value => array [.str "bool", .bool value]
  | .unit => array [.str "unit"]

private def encodeExchange (value : Exchange) : Json := array [encodeRequest value.request, encodeReply value.reply]

private def encodeValue : Nat → Value → Except String Json
  | 0, _ => throw "value encoding depth exhausted"
  | _ + 1, .unit => pure (array [.str "unit"])
  | _ + 1, .null typeName => pure (array [.str "null", .str typeName])
  | _ + 1, .boolean value => pure (array [.str "bool", .bool value])
  | _ + 1, .unsigned value => pure (array [.str "ulong", number value])
  | _ + 1, .signed value => pure (array [.str "long", number value])
  | _ + 1, .uint256 value => pure (array [.str "uint256", number value])
  | _ + 1, .enum typeName value => pure (array [.str "enum", .str typeName, number value])
  | _ + 1, .address identity => pure (array [.str "address", .str identity])
  | _ + 1, .bytes value => pure (array [.str "bytes", .str (encodeBytes value)])
  | _ + 1, .reference typeName identity => pure (array [.str "reference", .str typeName, .str identity])
  | fuel + 1, .struct typeName fields => do
      let names := fields.map (·.1)
      if names.eraseDups.length != names.length then throw "duplicate value field"
      let fields := fields.mergeSort (fun a b => a.1 <= b.1)
      let encoded ← fields.mapM (fun (name, value) => do pure (array [.str name, ← encodeValue fuel value]))
      pure (array [.str "struct", .str typeName, array encoded])

private def encodeOperands (operands : List RefundOperand) : Except String Json := do
  if operands.map (·.ordinal) != List.range 12 then throw "suspension operand shape"
  let (_, encoded) ← operands.foldlM (fun (roots, encoded) operand => do
    let (roots, location) := match operand.location with
      | none => (roots, Json.null)
      | some location =>
          let roots := if roots.contains location.root then roots else roots ++ [location.root]
          (roots, array [number (roots.idxOf location.root), array (location.fields.map Json.str),
            .bool location.readOnly, .str location.provenance])
    if operand.provenance != operand.location.map (·.provenance) then throw "operand provenance mismatch"
    let mode := match operand.mode with | .value => "value" | .readOnlyLocation => "readonly-location"
    pure (roots, encoded ++ [array [number operand.ordinal, .str mode, ← encodeValue 256 operand.value, location]]))
    (([] : List Nat), ([] : List Json))
  pure (array encoded)

private def rejection : RejectionCode → String
  | .inputDomain => "input-domain"
  | .fuelDomain => "fuel-domain"
  | .tapeEmpty => "tape-empty"
  | .tapeRequest => "tape-request"
  | .tapeReply => "tape-reply"
  | .unexpected => "unexpected"

def observe (id : String) (execution : Execution) : Except String Json := do
  if execution.rejection == some .unexpected then throw "unexpected interpreter rejection"
  let (tag, requests, tape, returned, operands) ← match execution.outcome with
    | .suspended value => do
        if value.remainingCSharpFuel != execution.remainingCSharpFuel then throw "suspension fuel mismatch"
        pure ("suspended", value.requests, value.remainingTape, Json.null, ← encodeOperands value.operands)
    | .returned value requests tape => pure ("returned", requests, tape, ← encodeValue 256 value, array [])
    | .outside requests tape => pure ("outside", requests, tape, Json.null, array [])
    | .rejected _ requests tape => pure ("rejected", requests, tape, Json.null, array [])
    | .fuelExhausted requests tape => pure ("fuel-exhausted", requests, tape, Json.null, array [])
    | .microFuelExhausted _ _ => throw "micro-fuel exhausted"
  pure (array [.str schema, .str id, .str Generated.program.prefixIntegrity, .str tag,
    optional (execution.rejection.map rejection), number execution.remainingCSharpFuel,
    array (requests.map encodeRequest), array (tape.map encodeExchange), returned, operands])

def replay (line : String) : Except String Json := do
  let value ← decode line
  observe value.id (runGeneratedObserved value.input value.tape value.fuel value.microFuel)

end SimpleTransferCompletionExtractor.StageB.Runtime.Replay

def main : IO UInt32 := do
  let stdin ← IO.getStdin
  let stdout ← IO.getStdout
  repeat
    let line ← stdin.getLine
    if line.isEmpty then return 0
    let line := if line.endsWith "\n" then (line.dropEnd 1).toString else line
    match SimpleTransferCompletionExtractor.StageB.Runtime.Replay.replay line with
    | .ok output => stdout.putStrLn output.compress
    | .error reason =>
        IO.eprintln s!"Stage-B replay: {reason}"
        return 1
  return 0
