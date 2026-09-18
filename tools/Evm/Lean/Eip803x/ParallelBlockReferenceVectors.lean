-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.ParallelBlockReference

namespace Eip803x
namespace ParallelBlockReferenceVectors

open ParallelBlockReference

structure DemoState where
  balance : Nat
  nonce : Nat
  deriving DecidableEq, Repr

def initial : DemoState := { balance := 5, nonce := 0 }
def afterFirst : DemoState := { balance := 12, nonce := 0 }
def final : DemoState := { balance := 24, nonce := 1 }

def firstTransaction : Transaction DemoState String Nat := fun state =>
  .ok ({ state with balance := state.balance + 7 }, 101)

def secondTransaction : Transaction DemoState String Nat := fun state =>
  .ok ({ balance := state.balance * 2, nonce := state.nonce + 1 }, 202)

def firstArtifact : Artifact DemoState Nat where
  applyDelta state := { state with balance := state.balance + 7 }
  receipt := 101

def secondArtifact : Artifact DemoState Nat where
  applyDelta state := { balance := state.balance * 2, nonce := state.nonce + 1 }
  receipt := 202

def staleSecondArtifact : Artifact DemoState Nat where
  applyDelta state := { balance := 10, nonce := state.nonce + 1 }
  receipt := 202

def wrongReceiptArtifact : Artifact DemoState Nat where
  applyDelta state := { balance := state.balance * 2, nonce := state.nonce + 1 }
  receipt := 203

def failingTransaction : Transaction DemoState String Nat := fun _ =>
  .error "invalid"

theorem canonical_artifacts_certify :
    Certifies initial
      [firstTransaction, secondTransaction]
      [firstArtifact, secondArtifact]
      final [101, 202] := by
  apply Certifies.cons (next := afterFirst)
  · rfl
  · rfl
  · rfl
  · apply Certifies.cons (next := final)
    · rfl
    · rfl
    · rfl
    · exact Certifies.nil final

theorem accepted_parallel_matches_sequential :
    runParallelOrFallback true
      [firstTransaction, secondTransaction]
      [firstArtifact, secondArtifact]
      initial = .ok (final, [101, 202]) := by
  have agreement := certifies_sequential_and_merge canonical_artifacts_certify
  simpa [runParallelOrFallback] using agreement.2

theorem rejected_parallel_replays_sequential :
    runParallelOrFallback false
      [firstTransaction, secondTransaction]
      [staleSecondArtifact]
      initial = .ok (final, [101, 202]) := by
  rfl

theorem sequential_failure_is_preserved_by_fallback :
    runParallelOrFallback false
      [firstTransaction, failingTransaction]
      [firstArtifact]
      initial = .error "invalid" := by
  rfl

theorem stale_prefix_mutation_is_observable :
    runParallelOrFallback true
      [firstTransaction, secondTransaction]
      [firstArtifact, staleSecondArtifact]
      initial ≠ runSequential [firstTransaction, secondTransaction] initial := by
  simp [runParallelOrFallback, applyArtifacts, runSequential, firstTransaction,
    secondTransaction, firstArtifact, staleSecondArtifact, initial]

theorem receipt_mutation_is_observable :
    runParallelOrFallback true
      [firstTransaction, secondTransaction]
      [firstArtifact, wrongReceiptArtifact]
      initial ≠ runSequential [firstTransaction, secondTransaction] initial := by
  simp [runParallelOrFallback, applyArtifacts, runSequential, firstTransaction,
    secondTransaction, firstArtifact, wrongReceiptArtifact, initial]

theorem artifact_order_mutation_is_observable :
    runParallelOrFallback true
      [firstTransaction, secondTransaction]
      [secondArtifact, firstArtifact]
      initial ≠ runSequential [firstTransaction, secondTransaction] initial := by
  simp [runParallelOrFallback, applyArtifacts, runSequential, firstTransaction,
    secondTransaction, firstArtifact, secondArtifact, initial]

theorem missing_artifact_mutation_is_observable :
    runParallelOrFallback true
      [firstTransaction, secondTransaction]
      [firstArtifact]
      initial ≠ runSequential [firstTransaction, secondTransaction] initial := by
  simp [runParallelOrFallback, applyArtifacts, runSequential, firstTransaction,
    secondTransaction, firstArtifact, initial]

example :
    (applyArtifacts [firstArtifact, secondArtifact] initial).1 = final := by
  rfl

example :
    (applyArtifacts [firstArtifact, secondArtifact] initial).2 = [101, 202] := by
  rfl

example :
    (applyArtifacts [firstArtifact, staleSecondArtifact] initial).1 =
      { balance := 10, nonce := 1 } := by
  rfl

end ParallelBlockReferenceVectors
end Eip803x
