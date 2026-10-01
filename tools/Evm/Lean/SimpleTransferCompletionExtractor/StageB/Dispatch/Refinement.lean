-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import SimpleTransferCompletionExtractor.StageB.Dispatch.Generated.StandardMainnetRefundDispatch
import SimpleTransferCompletionExtractor.StageB.Dispatch.Specification
import SimpleTransferCompletionExtractor.StageB.RuntimeSyntax

namespace SimpleTransferCompletionExtractor.StageB.Dispatch.Refinement

namespace G
export SimpleTransferCompletionExtractor.StageB.Dispatch.Generated
  (ReceiverKind MethodKind ReceiverEvidence CallEvidence baseProcessor gasPolicy
   sourceClosureSha256 referenceClosureSha256 prefixManifestSha256 ordinaryRefundManifestSha256
   receiverEvidence resolve executeSimpleTransferRefundCall refundPayRefundCall)
end G

namespace S
export SimpleTransferCompletionExtractor.StageB.Dispatch.Specification
  (Receiver Method DeclaringType resolve)
end S

def mapReceiver : G.ReceiverKind → S.Receiver
  | .ethereumTransactionProcessor => .nonBalEthereum
  | .balTransactionProcessor => .balEthereum

def mapMethod : G.MethodKind → S.Method
  | .refund => .refund
  | .payRefund => .payRefund

def encodeDeclaringType : S.DeclaringType → String
  | .transactionProcessorBaseEthereumGasPolicy => G.baseProcessor

structure SourceWitness where
  sourceClosureSha256 : String
  referenceClosureSha256 : String
  prefixManifestSha256 : String
  ordinaryRefundManifestSha256 : String
  deriving DecidableEq, Repr

def acceptedSource : SourceWitness :=
  { sourceClosureSha256 := G.sourceClosureSha256
    referenceClosureSha256 := G.referenceClosureSha256
    prefixManifestSha256 := G.prefixManifestSha256
    ordinaryRefundManifestSha256 := G.ordinaryRefundManifestSha256 }

def SourceAttached (source : SourceWitness) : Prop := source = acceptedSource

def standardRuntimeReceiver : Runtime.Value :=
  .reference "global::Nethermind.Evm.TransactionProcessing.TransactionProcessorBase<TGasPolicy>" "processor"

abbrev RuntimeConcreteTypeObservation := Runtime.Value → G.ReceiverKind → Prop

/-- The runtime receiver kind is an explicit observation; this relation does not derive DI resolution. -/
structure Boundary (source : SourceWitness) (observes : RuntimeConcreteTypeObservation)
    (receiver : G.ReceiverKind) : Prop where
  sourceAttached : SourceAttached source
  concreteTypeObserved : observes standardRuntimeReceiver receiver

theorem both_receivers_are_sealed_and_do_not_shadow (receiver : G.ReceiverKind) :
    (G.receiverEvidence receiver).isSealed = true ∧
    (G.receiverEvidence receiver).declaresRefund = false ∧
    (G.receiverEvidence receiver).declaresPayRefund = false := by
  cases receiver <;> exact ⟨rfl, rfl, rfl⟩

theorem generated_resolution_refines (receiver : G.ReceiverKind) (method : G.MethodKind) :
    G.resolve receiver method = encodeDeclaringType (S.resolve (mapReceiver receiver) (mapMethod method)) := by
  cases receiver <;> cases method <;> rfl

theorem both_receivers_resolve_to_standard_base (receiver : G.ReceiverKind) :
    G.resolve receiver .refund = G.baseProcessor ∧
    G.resolve receiver .payRefund = G.baseProcessor := by
  cases receiver <;> exact ⟨rfl, rfl⟩

theorem execute_simple_transfer_refund_call_is_pinned :
    G.executeSimpleTransferRefundCall.caller =
      "global::Nethermind.Evm.TransactionProcessing.TransactionProcessorBase<TGasPolicy>.ExecuteSimpleTransfer/15" ∧
    G.executeSimpleTransferRefundCall.callee =
      "global::Nethermind.Evm.TransactionProcessing.TransactionProcessorBase<TGasPolicy>.Refund/12" ∧
    G.executeSimpleTransferRefundCall.receiver = "containingInstance" ∧
    G.executeSimpleTransferRefundCall.sourcePath =
      "src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs" ∧
    G.executeSimpleTransferRefundCall.syntaxSha256 =
      "acd70f59fe634d9147c7f67c33c5e0a7c72520f4527624b5ca9d0bc59427a4df" := by
  exact ⟨rfl, rfl, rfl, rfl, rfl⟩

theorem refund_pay_refund_call_is_pinned :
    G.refundPayRefundCall.caller =
      "global::Nethermind.Evm.TransactionProcessing.TransactionProcessorBase<TGasPolicy>.Refund/12" ∧
    G.refundPayRefundCall.callee =
      "global::Nethermind.Evm.TransactionProcessing.TransactionProcessorBase<TGasPolicy>.PayRefund/3" ∧
    G.refundPayRefundCall.receiver = "containingInstance" ∧
    G.refundPayRefundCall.sourcePath =
      "src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs" ∧
    G.refundPayRefundCall.syntaxSha256 =
      "fc5079a4f348d3a81b8d2a4ce897dac6f13ecdbcb9576585e2dc3ad7835a5702" := by
  exact ⟨rfl, rfl, rfl, rfl, rfl⟩

theorem upstream_artifact_identities_are_pinned :
    G.prefixManifestSha256 =
      "d7eb0fedb1ed7f97e18ba030325441e05a8b27d06158aef003808a497b1030ee" ∧
    G.ordinaryRefundManifestSha256 =
      "a0dd398a94994b65c3ea6bc59b6d62cd77958025208c423237e14a44d1ce30a1" := by
  exact ⟨rfl, rfl⟩

theorem boundary_resolves_standard_base {source : SourceWitness}
    {observes : RuntimeConcreteTypeObservation} {receiver : G.ReceiverKind}
    (_boundary : Boundary source observes receiver) :
    G.resolve receiver .refund = G.baseProcessor ∧
    G.resolve receiver .payRefund = G.baseProcessor :=
  both_receivers_resolve_to_standard_base receiver

end SimpleTransferCompletionExtractor.StageB.Dispatch.Refinement
