-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import SimpleTransferCompletionExtractor.StageB.Generated.PrefixProgram

namespace SimpleTransferCompletionExtractor.StageB.FeeHelper

def processorType : String :=
  "global::Nethermind.Evm.TransactionProcessing.TransactionProcessorBase<TGasPolicy>"

def gasConsumedType : String := "global::Nethermind.Evm.TransactionProcessing.GasConsumed"

def parameter (index : Nat) : Parameter :=
  (Generated.feeHelperSignature.parameters[index]?).getD
    { symbol := "", name := "", ordinal := 0, typeName := "", refKind := .none, optional := false }

private def plainBinding : Binding :=
  { capture := -1, refKind := .none, isRef := false, argumentMode := .value,
    callTarget := "", callReceiverChild := -1, callArgumentChildren := [], hasCall := false,
    additionalMembers := [] }

private def conversion : String :=
  "in:exists=True;identity=True;implicit=True;numeric=False;reference=False;nullable=False;user=False;union=False;method=;constrained=;out:exists=True;identity=True;implicit=True;numeric=False;reference=False;nullable=False;user=False;union=False;method=;constrained="

private def sourceParameter (index : Nat) : SourceTerm :=
  let p := parameter index
  .mk "ParameterReference" p.typeName p.symbol "" "" "" "" "" false (-1)
    (some { plainBinding with refKind := p.refKind }) []

private def sourceReceiver : SourceTerm :=
  .mk "InstanceReference" processorType "" "ContainingTypeInstance" "" "" "" ""
    true (-1) (some plainBinding) []

private def sourceProjection (kind typeName symbol : String) (receiver : SourceTerm) : SourceTerm :=
  .mk kind typeName symbol (if kind == "FieldReference" then "declaration=False" else "")
    "" "" "" "" false (-1)
    (some { plainBinding with callTarget := symbol, callReceiverChild := 0, hasCall := true }) [receiver]

private def sourceField (typeName name : String) : SourceTerm :=
  sourceProjection "FieldReference" typeName (typeName ++ " " ++ processorType ++ "." ++ name) sourceReceiver

private def sourceGas (name : String) : SourceTerm :=
  sourceProjection "PropertyReference" "ulong" ("ulong " ++ gasConsumedType ++ "." ++ name) (sourceParameter 6)

private def sourceArgument (index : Nat) (readonly : Bool) (value : SourceTerm) : SourceTerm :=
  .mk "Argument" "" "" "" "" "Explicit" (if readonly then "In" else "None") conversion false index
    (some { plainBinding with
      refKind := (if readonly then .inRef else .none),
      argumentMode := (if readonly then .readOnlyLocation else .value) }) [value]

private def sourceInvocation (typeName symbol operator : String) (receiver : Option SourceTerm)
    (arguments : List (Bool × SourceTerm)) : SourceTerm :=
  .mk "Invocation" typeName symbol operator "" "" "" "" false (-1)
    (some { plainBinding with
      callTarget := symbol, hasCall := true,
      callReceiverChild := (if receiver.isSome then 0 else -1),
      callArgumentChildren := (List.range arguments.length).map (· + (if receiver.isSome then 1 else 0)) })
    (receiver.toList ++ arguments.mapIdx (fun i (readonly, value) => sourceArgument i readonly value))

private def sourceStatement (value : SourceTerm) : SourceTerm :=
  .mk "ExpressionStatement" "" "" "" "" "" "" "" false (-1) (some plainBinding) [value]

private def sourceAdd (target value : SourceTerm) : SourceTerm :=
  sourceStatement (.mk "CompoundAssignment" "ulong" "" "Add;checked=False;lifted=False"
    "" "" "" conversion false (-1) (some plainBinding) [target, value])

def routingSymbol : String :=
  "bool global::Nethermind.Evm.TransactionProcessing.SystemTransactionRoutingKernel.ParticipatesInNormalBlockCounters(global::Nethermind.Evm.TransactionProcessing.ExecutionOptions options, bool parallel)"

def combineSymbol : String :=
  "ulong global::Nethermind.Evm.GasPolicy.IGasPolicy<TGasPolicy>.CombineBlockGas(ulong blockExecutionGas, ulong blockStateGas)"

def payFeesSymbol : String := "void " ++ processorType ++
  ".PayFees(global::Nethermind.Core.Transaction tx, global::Nethermind.Core.BlockHeader header, global::Nethermind.Core.Specs.IReleaseSpec spec, global::Nethermind.Evm.Tracing.ITxTracer tracer, in global::Nethermind.Evm.TransactionSubstate substate, ulong spentGas, in global::Nethermind.Int256.UInt256 premiumPerGas, in global::Nethermind.Int256.UInt256 effectiveGasPrice, in global::Nethermind.Int256.UInt256 blobBaseFee, int statusCode)"

private def regular (destination : Int) : Option Edge :=
  some { destination, semantics := "Regular", leavingRegions := [], enteringRegions := [], finallyRegions := [] }

def routingBlock : SourceBlock :=
  { ordinal := 1, kind := "Block", reachable := true, conditionKind := "WhenFalse", operations := [],
    branchValue := some (sourceInvocation "bool" routingSymbol "virtual=False" none
      [(false, sourceParameter 4), (false, sourceField "bool" "_parallel")]),
    containsExcludedOperations := false, fallThrough := regular 2, conditional := regular 5 }

def forkBlock : SourceBlock :=
  { ordinal := 2, kind := "Block", reachable := true, conditionKind := "WhenFalse", operations := [],
    branchValue := some (sourceProjection "PropertyReference" "bool"
      "bool global::Nethermind.Core.Specs.IReleaseSpec.IsEip8037Enabled" (sourceParameter 2)),
    containsExcludedOperations := false, fallThrough := regular 3, conditional := regular 4 }

def counterBlock : SourceBlock :=
  let execution := sourceField "ulong" "_blockCumulativeExecutionGas"
  let state := sourceField "ulong" "_blockCumulativeStateGas"
  let header := sourceProjection "PropertyReference" "ulong" "ulong global::Nethermind.Core.BlockHeader.GasUsed"
    (sourceParameter 1)
  { ordinal := 3, kind := "Block", reachable := true, conditionKind := "None",
    operations := [sourceAdd execution (sourceGas "EffectiveBlockGas"), sourceAdd state (sourceGas "BlockStateGas"),
      sourceStatement (.mk "SimpleAssignment" "ulong" "" "ref=False" "" "" "" "" false (-1)
        (some plainBinding) [header, sourceInvocation "ulong" combineSymbol "virtual=True;constrained=TGasPolicy"
          none [(false, execution), (false, state)]])],
    branchValue := none, containsExcludedOperations := false, fallThrough := regular 5, conditional := none }

def payFeesInvocation : SourceTerm :=
  sourceInvocation "void" payFeesSymbol "virtual=True" (some sourceReceiver)
    [(false, sourceParameter 0), (false, sourceParameter 1), (false, sourceParameter 2),
     (false, sourceParameter 3), (true, sourceParameter 5), (false, sourceGas "SpentGas"),
     (true, sourceParameter 7), (true, sourceParameter 8), (true, sourceParameter 9), (false, sourceParameter 10)]

def payFeesBlock : SourceBlock :=
  { ordinal := 5, kind := "Block", reachable := true, conditionKind := "None",
    operations := [sourceStatement payFeesInvocation], branchValue := none,
    containsExcludedOperations := false, fallThrough := regular 6, conditional := none }

set_option maxRecDepth 20000 in
set_option maxHeartbeats 8000000 in
theorem selected_source_admitted :
    Generated.feeHelperSourceBlock1 = routingBlock ∧
    Generated.feeHelperSourceBlock2 = forkBlock ∧
    Generated.feeHelperSourceBlock3 = counterBlock ∧
    Generated.feeHelperSourceBlock5 = payFeesBlock := by
  exact ⟨rfl, rfl, rfl, rfl⟩

end SimpleTransferCompletionExtractor.StageB.FeeHelper
