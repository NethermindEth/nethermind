-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

/-!
  A handwritten composition lemma for ordered parallel transaction artifacts.

  The production BAL executor runs transaction workers out of order, validates
  their observations in canonical transaction order, then publishes receipts
  in that order.  This model isolates the algebraic obligation at that join:
  every accepted artifact must reproduce the real transaction transition at
  the exact state prefix produced by all earlier accepted artifacts.

  The model does not establish that Nethermind's workers, BAL representation,
  validator, or world-state merge satisfy `Certifies`.  Those are separate
  production-extraction and adapter obligations.
-/

namespace Eip803x
namespace ParallelBlockReference

universe u v w

abbrev Transaction (State : Type u) (Failure : Type v) (Receipt : Type w) :=
  State → Except Failure (State × Receipt)

structure Artifact (State : Type u) (Receipt : Type w) where
  applyDelta : State → State
  receipt : Receipt

def runSequential
    {State : Type u} {Failure : Type v} {Receipt : Type w} :
    List (Transaction State Failure Receipt) → State →
      Except Failure (State × List Receipt)
  | [], state => .ok (state, [])
  | transaction :: rest, state =>
      match transaction state with
      | .error failure => .error failure
      | .ok (next, receipt) =>
          match runSequential rest next with
          | .error failure => .error failure
          | .ok (final, receipts) => .ok (final, receipt :: receipts)

def applyArtifacts
    {State : Type u} {Receipt : Type w} :
    List (Artifact State Receipt) → State → State × List Receipt
  | [], state => (state, [])
  | artifact :: rest, state =>
      let (final, receipts) := applyArtifacts rest (artifact.applyDelta state)
      (final, artifact.receipt :: receipts)

inductive Certifies
    {State : Type u} {Failure : Type v} {Receipt : Type w} :
    State → List (Transaction State Failure Receipt) →
      List (Artifact State Receipt) → State → List Receipt → Prop
  | nil (initial : State) : Certifies initial [] [] initial []
  | cons
      {initial next final : State}
      {transaction : Transaction State Failure Receipt}
      {transactions : List (Transaction State Failure Receipt)}
      {artifact : Artifact State Receipt}
      {artifacts : List (Artifact State Receipt)}
      {receipt : Receipt}
      {receipts : List Receipt}
      (step : transaction initial = .ok (next, receipt))
      (delta : artifact.applyDelta initial = next)
      (reported : artifact.receipt = receipt)
      (rest : Certifies next transactions artifacts final receipts) :
      Certifies initial (transaction :: transactions) (artifact :: artifacts)
        final (receipt :: receipts)

theorem certifies_lengths
    {State : Type u} {Failure : Type v} {Receipt : Type w}
    {initial final : State}
    {transactions : List (Transaction State Failure Receipt)}
    {artifacts : List (Artifact State Receipt)}
    {receipts : List Receipt}
    (certificate : Certifies initial transactions artifacts final receipts) :
    transactions.length = artifacts.length ∧
      artifacts.length = receipts.length := by
  induction certificate with
  | nil => simp
  | cons _ _ _ _ inductionHypothesis =>
      constructor <;> simp [inductionHypothesis]

theorem certifies_sequential_and_merge
    {State : Type u} {Failure : Type v} {Receipt : Type w}
    {initial final : State}
    {transactions : List (Transaction State Failure Receipt)}
    {artifacts : List (Artifact State Receipt)}
    {receipts : List Receipt}
    (certificate : Certifies initial transactions artifacts final receipts) :
    runSequential transactions initial = .ok (final, receipts) ∧
      applyArtifacts artifacts initial = (final, receipts) := by
  induction certificate with
  | nil => simp [runSequential, applyArtifacts]
  | cons step delta reported rest inductionHypothesis =>
      rcases inductionHypothesis with ⟨sequentialRest, mergedRest⟩
      constructor
      · simp [runSequential, step, sequentialRest]
      · simp [applyArtifacts, delta, reported, mergedRest]

def runParallelOrFallback
    {State : Type u} {Failure : Type v} {Receipt : Type w}
    (accepted : Bool)
    (transactions : List (Transaction State Failure Receipt))
    (artifacts : List (Artifact State Receipt))
    (initial : State) : Except Failure (State × List Receipt) :=
  if accepted then
    .ok (applyArtifacts artifacts initial)
  else
    runSequential transactions initial

theorem rejected_parallel_uses_sequential
    {State : Type u} {Failure : Type v} {Receipt : Type w}
    (transactions : List (Transaction State Failure Receipt))
    (artifacts : List (Artifact State Receipt))
    (initial : State) :
    runParallelOrFallback false transactions artifacts initial =
      runSequential transactions initial := by
  rfl

theorem sound_acceptance_matches_sequential
    {State : Type u} {Failure : Type v} {Receipt : Type w}
    (accepted : Bool)
    (transactions : List (Transaction State Failure Receipt))
    (artifacts : List (Artifact State Receipt))
    (initial : State)
    (sound : accepted = true →
      ∃ final receipts, Certifies initial transactions artifacts final receipts) :
    runParallelOrFallback accepted transactions artifacts initial =
      runSequential transactions initial := by
  cases accepted with
  | false => rfl
  | true =>
      rcases sound rfl with ⟨final, receipts, certificate⟩
      have agreement := certifies_sequential_and_merge certificate
      simp [runParallelOrFallback, agreement.1, agreement.2]

theorem accepted_parallel_preserves_failure_freedom
    {State : Type u} {Failure : Type v} {Receipt : Type w}
    {initial final : State}
    {transactions : List (Transaction State Failure Receipt)}
    {artifacts : List (Artifact State Receipt)}
    {receipts : List Receipt}
    (certificate : Certifies initial transactions artifacts final receipts) :
    runSequential transactions initial = .ok (applyArtifacts artifacts initial) := by
  have agreement := certifies_sequential_and_merge certificate
  rw [agreement.1, agreement.2]

end ParallelBlockReference
end Eip803x
