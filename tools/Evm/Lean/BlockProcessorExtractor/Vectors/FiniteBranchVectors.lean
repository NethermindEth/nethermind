-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import BlockProcessorExtractor.Refinement.NormalFiniteBranchCompletion
import BlockProcessorExtractor.Vectors.BranchAcceptedIterationVectors

namespace BlockProcessorExtractor.Vectors.FiniteBranchVectors

namespace F
export BlockProcessorExtractor.Generated.NormalFiniteBranchCompletion (Input Resources PublicationInput run containsFlag suggested)
end F
namespace RF
export BlockProcessorExtractor.Refinement.NormalFiniteBranchCompletion (SourceWitness NormalAdapter Adapters SourceAttachedNormal generated_normal_refines_spec)
end RF
namespace B
export BlockProcessorExtractor.Vectors.BranchAcceptedIterationVectors (publication source)
end B
namespace P
export SequentialBlockPostTransactionFinalizationExtractor.Refinement.ProcessOneValidatedPublication (NormalReturnAdapter)
end P

def publication (index : Nat) (skip store : Bool) : F.PublicationInput :=
  { B.publication skip with
    suggestedBlock := index + 1,
    processBlock := { normalReturn := true, processedBlock := index + 101, receipts := index + 201 },
    storeReceipts := store }

theorem publicationAdapter (index : Nat) (skip store : Bool) :
    P.NormalReturnAdapter B.source.publicationSource (publication index skip store) := by
  refine {
    sourceAdapter := {
      closureIdentity := rfl, siteIdentities := rfl, exactBase := rfl, standardSequential := rfl,
      balDisabled := rfl, nonparallel := rfl, processBlockNormalReturn := rfl, noAdditionalEffects := rfl },
    validatorObservation := ?_, validatorNormalReturn := Or.inr rfl,
    acceptedOrSkipped := Or.inr rfl, postValidationNormalReturn := rfl, storeNormalReturn := Or.inr rfl }
  cases skip <;> rfl

inductive Scenario where
  | traced | one | two | inclusionFalse | skippedValidation | readOnly
  | markWithoutHead | markAfterHeadFalse | readOnlyMark | forceEmptyPreload
  deriving DecidableEq, Repr

def count : Scenario → Nat
  | .two => 2 | _ => 1
def options : Scenario → Nat
  | .skippedValidation => 12 | .readOnly => 69 | .markWithoutHead => 196
  | .markAfterHeadFalse => 132 | .readOnlyMark => 197 | .forceEmptyPreload => 6 | _ => 4
def finite (scenario : Scenario) : F.Input :=
  { publications := (List.range (count scenario)).map
      (fun index => publication index (F.containsFlag (options scenario) 8) (F.containsFlag (options scenario) 4)),
    originalList := 801, options := options scenario, ownedScope := count scenario != 0,
    scope := 10, baseHeader := some 900, header := fun block => 1000 + block,
    blockNumber := fun _ => 77, hasTransactions := fun _ => true, nullTracer := scenario != .traced, preWarmerPresent := true,
    resources := fun index => {
      cancellation := some (100 + index), prewarm := some (200 + index),
      prefetch := some (300 + index), reopenedScope := 10000 + index, inclusion := scenario != .inclusionFalse },
    preludeNormal := fun _ => true, normal := fun _ _ => true }

def finiteSource : RF.SourceWitness :=
  { closureSha256 := BlockProcessorExtractor.Generated.NormalFiniteBranchCompletion.sourceClosureSha256,
    semanticIrSha256 := BlockProcessorExtractor.Generated.NormalFiniteBranchCompletion.semanticIrSha256,
    iterationSource := B.source }
theorem finiteAdapter (scenario : Scenario) : RF.NormalAdapter finiteSource (finite scenario) := by
  refine { closureIdentity := rfl, irIdentity := rfl, boundary := ?_, steps := ?_ }
  · cases scenario <;> decide
  · cases scenario <;>
      repeat first
        | exact rfl
        | exact publicationAdapter _ _ _
        | decide
        | constructor

def emptyFinite : F.Input := { finite .one with publications := [], ownedScope := false }
theorem emptyFiniteAdapter : RF.NormalAdapter finiteSource emptyFinite := by
  exact { closureIdentity := rfl, irIdentity := rfl, boundary := by decide, steps := True.intro }

theorem empty_finite_executes_theorem :
    BlockProcessorExtractor.Refinement.NormalFiniteBranchCompletion.SourceAttachedNormal finiteSource emptyFinite :=
  BlockProcessorExtractor.Refinement.NormalFiniteBranchCompletion.generated_normal_refines_spec
    finiteSource emptyFinite emptyFiniteAdapter

theorem admitted_finite_vectors_execute_theorem (scenario : Scenario) :
    RF.SourceAttachedNormal finiteSource (finite scenario) :=
  RF.generated_normal_refines_spec finiteSource (finite scenario) (finiteAdapter scenario)

theorem normal_adapter_is_inhabited : ∃ i, RF.NormalAdapter finiteSource i :=
  ⟨finite .two, finiteAdapter .two⟩

theorem empty_returns_empty : (F.run emptyFinite).returnedBlocks = some [] := by decide
theorem traced_options : (F.run (finite .traced)).entries.map (·.blockOptions) = [516] := by decide
theorem two_block_return : (F.run (finite .two)).returnedBlocks = some [101, 102] := by decide
theorem two_block_prefix : (F.run (finite .two)).observedPrefix = 2 := by decide
theorem two_block_base_threading :
    (F.run (finite .two)).entries.map (·.baseHeader) = [some 900, some 1101] := by decide

end BlockProcessorExtractor.Vectors.FiniteBranchVectors
