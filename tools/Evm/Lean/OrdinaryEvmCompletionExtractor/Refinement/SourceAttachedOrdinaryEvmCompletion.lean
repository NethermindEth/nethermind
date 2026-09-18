-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import OrdinaryEvmCompletionExtractor.Generated.OrdinaryEvmCompletion
import OrdinaryEvmCompletionExtractor.Refinement.OrdinaryEvmCompletion

namespace OrdinaryEvmCompletionExtractor.Refinement.SourceAttachedOrdinaryEvmCompletion

open OrdinaryEvmCompletionExtractor.Specification.OrdinaryEvmCompletion

namespace G
export OrdinaryEvmCompletionExtractor.Generated.OrdinaryEvmCompletion
  (sourceClosure sourceIr refundConstants prepared preparationGas shouldRevert isError refundCounter
   rollbackRequired terminalStatus refundInput refundResult consumedGas processorAfter fees
   receiptTransaction recipient stateRoot finalizeInput receiptEntry receiptResult events evaluate)
end G

namespace C
export OrdinaryEvmCompletionExtractor.Refinement.OrdinaryEvmCompletion
  (Input mapInput Domain AcceptedStages accepted_stages_at_computed_inputs)
end C

private theorem refund_constants_refines : G.refundConstants = refundConstants := rfl

private theorem prepared_refines (i : Input) : G.prepared i = prepared i := rfl

private theorem preparation_gas_refines (gas : P.Gas) :
    G.preparationGas gas = preparationGas gas := rfl

private theorem should_revert_refines (terminal : VmTerminal) :
    G.shouldRevert terminal = shouldRevert terminal := rfl

private theorem is_error_refines (terminal : VmTerminal) :
    G.isError terminal = isError terminal := rfl

private theorem refund_counter_refines (terminal : VmTerminal) :
    G.refundCounter terminal = refundCounter terminal := rfl

private theorem rollback_refines (terminal : VmTerminal) :
    G.rollbackRequired terminal = rollbackRequired terminal := by
  cases terminal <;> rfl

private theorem terminal_status_refines (terminal : VmTerminal) :
    G.terminalStatus terminal = terminalStatus terminal := by
  cases terminal <;> rfl

private theorem refund_input_refines (i : Input) : G.refundInput i = refundInput i := by
  simp only [G.refundInput, refundInput, is_error_refines, should_revert_refines,
    refund_counter_refines, prepared_refines, preparation_gas_refines]

private theorem refund_result_refines (i : Input) : G.refundResult i = refundResult i := by
  simp only [G.refundResult, refundResult, refund_constants_refines, refund_input_refines]

private theorem consumed_gas_refines (i : Input) : G.consumedGas i = consumedGas i := by
  simp only [G.consumedGas, consumedGas, refund_result_refines]

private theorem processor_refines (i : Input) : G.processorAfter i = processorAfter i := by
  simp only [G.processorAfter, processorAfter, consumed_gas_refines]

private theorem fees_refines (i : Input) : G.fees i = fees i := by
  simp only [G.fees, fees, consumed_gas_refines]

private theorem receipt_transaction_refines (i : Input) :
    G.receiptTransaction i = receiptTransaction i := rfl

private theorem recipient_refines (i : Input) : G.recipient i = recipient i := by
  simp only [G.recipient, recipient, prepared_refines]

private theorem state_root_refines (i : Input) : G.stateRoot i = stateRoot i := rfl

private theorem finalize_input_refines (i : Input) : G.finalizeInput i = finalizeInput i := by
  cases terminal : i.tail.vm.terminal <;>
    simp only [G.finalizeInput, finalizeInput, terminal, state_root_refines]

private theorem receipt_entry_refines (i : Input) : G.receiptEntry i = receiptEntry i := by
  simp only [G.receiptEntry, receiptEntry, processor_refines]

private theorem receipt_result_refines (i : Input) : G.receiptResult i = receiptResult i := by
  simp only [G.receiptResult, receiptResult, receipt_entry_refines, receipt_transaction_refines,
    recipient_refines, consumed_gas_refines, finalize_input_refines]

private theorem events_refine (i : Input) : G.events i = events i := by
  simp only [G.events, events, rollback_refines, refund_result_refines, processor_refines,
    fees_refines, consumed_gas_refines, receipt_result_refines]

/-- Equality of the generated continuation model, not a theorem about opaque production hooks. -/
theorem generated_refines_spec (i : Input) : G.evaluate i = evaluate i := by
  simp only [G.evaluate, evaluate, prepared_refines, terminal_status_refines, rollback_refines,
    preparation_gas_refines, refund_result_refines, processor_refines, fees_refines,
    receipt_result_refines, events_refine]

structure SourceWitness where
  sourceClosure : String
  sourceIr : String

/-- Artifact identities are checked by the external source-admission gate; its correctness is not assumed here as an output equality. -/
structure Adapter (source : SourceWitness) (predicates : ExternalPredicates) (i : C.Input) : Prop where
  closureIdentity : source.sourceClosure = G.sourceClosure
  irIdentity : source.sourceIr = G.sourceIr
  domain : C.Domain predicates i

/-- Preparation retains its accepted model-to-model boundary and the VM/hook contracts remain external. -/
structure SourceAttached (source : SourceWitness) (i : C.Input) : Prop where
  closureIdentity : source.sourceClosure = G.sourceClosure
  irIdentity : source.sourceIr = G.sourceIr
  continuation : G.evaluate (C.mapInput i) = evaluate (C.mapInput i)
  stages : C.AcceptedStages i

theorem source_attached_refines (source : SourceWitness) (predicates : ExternalPredicates)
    (i : C.Input) (adapter : Adapter source predicates i) : SourceAttached source i :=
  { closureIdentity := adapter.closureIdentity,
    irIdentity := adapter.irIdentity,
    continuation := generated_refines_spec (C.mapInput i),
    stages := C.accepted_stages_at_computed_inputs predicates i adapter.domain }

theorem generated_outputs_refine_accepted_stages (predicates : ExternalPredicates) (i : C.Input)
    (domain : C.Domain predicates i) :
    let output := G.evaluate (C.mapInput i)
    EvmTransactionPreparationExtractor.Refinement.mapResult
        (EvmTransactionPreparationExtractor.Generated.prepare i.preparation) = output.preparation ∧
      OrdinaryTransactionRefundAdapterExtractor.Refinement.OrdinaryTransactionRefund.mapResult
          (OrdinaryTransactionRefundAdapterExtractor.Generated.OrdinaryTransactionRefund.evaluate
            (OrdinaryEvmCompletionExtractor.Refinement.OrdinaryEvmCompletion.derivedRefundInput i)) =
        output.refund ∧
      ReceiptTerminalFoldExtractor.Refinement.ReceiptTerminalFold.mapFinalizationObservation
          (OrdinaryEvmCompletionExtractor.Refinement.OrdinaryEvmCompletion.acceptedReceiptRun i) =
        output.receipt := by
  have stages := C.accepted_stages_at_computed_inputs predicates i domain
  rw [generated_refines_spec]
  exact ⟨stages.preparation, stages.refund, stages.receipt⟩

end OrdinaryEvmCompletionExtractor.Refinement.SourceAttachedOrdinaryEvmCompletion
