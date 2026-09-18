-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.BlockReference

namespace Eip803x
namespace BlockReference
namespace BranchReference

/-!
The outer branch relation keeps the responsibilities which production places
around `BlockProcessor.ProcessOne` explicit. The suggested-block check is
performed only for the terminal selected block. Synchronous branch selection
and preparation happens next; only a selected branch then recovers senders and
EIP-7702 authorities for its selected blocks before the stateful loop.

Production opens one branch scope before the loop. A normal block resets the
active scope after its successful CommitTree call; a BAL retry records an
explicit failed attempt and restores/reopens the scope before the forced
sequential attempt. Long-branch checkpoint reopening is represented by an
abstract lifecycle hook. The final disposal is observable, while persistence
durability remains outside the relation.

CommitTree is a void logical-state-preserving boundary. Its hook result records
completion or an escaping exception, but it never rewrites the logical
BlockWorld. The inclusion-list result is a completed signal: `false` is still
committed; only an escaping exception aborts the branch.
-/

inductive AttemptMode where
  | sequential
  | parallelAttempt
  | retryableBalFailure
  | scopeRestored
  | forcedSequentialAttempt
  | sequentialFailure
  | parallelFailure
  | parallelException
  | parallelSuccessUnknown
  deriving DecidableEq, Repr

inductive ParallelOutcome where
  | successUnknown
  | retryableBalFailure
  | invalid (failure : Failure)
  | escaped (stage : ExceptionStage)
  deriving DecidableEq, Repr

structure ParallelAttemptState where
  index : Nat
  baseline : BlockWorld
  failedState : BlockWorld
  outcome : ParallelOutcome
  failure : Option Failure
  deriving DecidableEq, Repr

structure ScopeRestoration where
  index : Nat
  before : BlockWorld
  after : BlockWorld
  deriving DecidableEq, Repr

inductive ScopeEvent where
  | opened
  | preOpenedForGenesis
  | disposedForBalRetry (index : Nat)
  | reopenedForBalRetry (index : Nat)
  | disposedAtCheckpoint (index : Nat)
  | resetAfterSuccessfulBlock (index : Nat)
  | reopenedAtCheckpoint (index : Nat)
  | disposed
  deriving DecidableEq, Repr

structure RetryPolicy where
  parallelOutcome : Nat → ParallelOutcome
  parallelFailedState : Nat → BlockWorld → BlockWorld

def selectAttempt (policy : RetryPolicy) (index : Nat)
    (eligibility : ParallelEligibility) : AttemptMode :=
  if eligibility.isEligible then
    match policy.parallelOutcome index with
    | .successUnknown => .parallelSuccessUnknown
    | .retryableBalFailure => .retryableBalFailure
    | .invalid failure =>
        match failure with
        | .escaped _ | .parallelExecutionFailure _ => .parallelException
        | _ => .parallelFailure
    | .escaped _ => .parallelException
  else
    .sequential

inductive BranchSelectionDecision where
  | selected
  | skipped
  | escaped (stage : ExceptionStage)
  deriving DecidableEq, Repr

inductive FinalizationStep where
  | totalDifficulty
  | mainChainUpdate
  | markProcessed
  deriving DecidableEq, Repr

structure ChainFinalizationObservation where
  totalDifficulty : Nat
  head : Nat
  updateSucceeded : Bool
  markedProcessed : Bool
  completedSteps : List FinalizationStep
  /-- The ordered hook which escaped, if finalization did not complete. -/
  failedStep : Option FinalizationStep
  deriving DecidableEq, Repr

structure BranchSpec where
  block : BlockSpec
  synchronousBranchSelection : List BlockInput → BranchSelectionDecision
  openWorldStateScope : Nat → HookResult Unit
  checkInclusionList : Nat → BlockInput → BlockWorld → HookResult Bool
  prewarmSucceeded : Nat → BlockInput → BlockWorld → HookResult Unit
  notReadOnly : Bool
  reopenAtCheckpoint : Nat → Bool
  commitTree : Nat → BlockInput → BlockWorld → HookResult Unit
  setTotalDifficulty : List BlockRun → List Nat → HookResult Nat
  updateMainChain : Nat → List BlockRun → List Nat → HookResult (Nat × Bool)
  markProcessed : List BlockRun → HookResult Bool

inductive PreprocessResult where
  | success (trace : List Phase)
  | skipped (trace : List Phase)
  | escaped (index : Nat) (stage : ExceptionStage) (trace : List Phase)
  deriving DecidableEq, Repr

def terminalInput : Nat → List BlockInput → Option (Nat × BlockInput)
  | _, [] => none
  | index, [ input ] => some (index, input)
  | index, _ :: rest => terminalInput (index + 1) rest

def preprocessSenderAuthority : Nat → List BlockInput → PreprocessResult
  | _, [] => .success []
  | index, input :: rest =>
      match input.phaseException .senderAndAuthorityRecovery with
      | some stage => .escaped index stage [ .senderAndAuthorityRecovery ]
      | none =>
          if acceptedPhase input .senderAndAuthorityRecovery then
            match preprocessSenderAuthority (index + 1) rest with
            | .success trace => .success ([ .senderAndAuthorityRecovery ] ++ trace)
            | .skipped trace => .skipped ([ .senderAndAuthorityRecovery ] ++ trace)
            | .escaped failedIndex stage trace => .escaped failedIndex stage
                ([ .senderAndAuthorityRecovery ] ++ trace)
          else
            .escaped index .senderAuthorityRecovery [ .senderAndAuthorityRecovery ]

def suggestedBlockValidation (inputs : List BlockInput) : PreprocessResult :=
  match terminalInput 0 inputs with
  | none => .success []
  | some (terminalIndex, terminal) =>
      match terminal.phaseException .suggestedBlockValidation with
      | some stage => .escaped terminalIndex stage [ .suggestedBlockValidation ]
      | none =>
           if acceptedPhase terminal .suggestedBlockValidation then
             .success [ .suggestedBlockValidation ]
           else
             .skipped [ .suggestedBlockValidation ]

def preprocessInputs (inputs : List BlockInput) : PreprocessResult :=
  preprocessSenderAuthority 0 inputs

def scopePreOpenedForGenesis : List BlockInput → Bool
  | [ input ] =>
      input.scopePreOpened && input.parallelEligibility.isGenesis && input.baseBlock.isNone
  | _ => false

def invalidPreOpenedScope : List BlockInput → Bool
  | [] => false
  | input :: rest =>
      (input.scopePreOpened &&
        !(input.parallelEligibility.isGenesis && input.baseBlock.isNone && rest = [])) ||
        invalidPreOpenedScope rest

structure AttemptResult where
  run : Option BlockRun
  modes : List AttemptMode
  parallelAttempt : Option ParallelAttemptState
  restoration : Option ScopeRestoration
  scopeEvents : List ScopeEvent
  deriving DecidableEq, Repr

def rejectedAttempt (baseline : BlockWorld) (failure : Failure) : BlockRun :=
  { outcome := match failure with
      | .escaped stage => .exception stage
      | _ => .rejected failure
    world := baseline
    trace := []
    finallyAwait := .nonePending }

def parallelAttemptObservation (index : Nat) (baseline failedState : BlockWorld)
    (outcome : ParallelOutcome) : ParallelAttemptState :=
  { index := index
    baseline := baseline
    failedState := failedState
    outcome := outcome
    failure := match outcome with
      | .successUnknown => none
      | .retryableBalFailure => some (.retryableBalFailure index)
      | .invalid failure => some failure
      | .escaped stage => some (.escaped stage) }

def runAttempt (spec : BranchSpec) (policy : RetryPolicy) (index : Nat)
    (input : BlockInput) (baseline : BlockWorld) : AttemptResult :=
  if input.parallelEligibility.isEligible then
    let failedState := policy.parallelFailedState index baseline
    match policy.parallelOutcome index with
    | .successUnknown =>
        { run := none
          modes := [ .parallelAttempt, .parallelSuccessUnknown ]
          parallelAttempt := some
            (parallelAttemptObservation index baseline failedState .successUnknown)
          restoration := none
          scopeEvents := [] }
    | .invalid failure =>
        match failure with
        | .escaped stage =>
            { run := some
                { outcome := .exception stage
                  world := baseline
                  trace := []
                  finallyAwait := .nonePending }
              modes := [ .parallelAttempt, .parallelException ]
              parallelAttempt := some
                (parallelAttemptObservation index baseline failedState (.escaped stage))
              restoration := none
              scopeEvents := [] }
        | .parallelExecutionFailure parallelIndex =>
            { run := some
                { outcome := .exception (.nonBalParallel parallelIndex)
                  world := baseline
                  trace := []
                  finallyAwait := .nonePending }
              modes := [ .parallelAttempt, .parallelException ]
              parallelAttempt := some
                (parallelAttemptObservation index baseline failedState (.invalid failure))
              restoration := none
              scopeEvents := [] }
        | _ =>
            { run := some (rejectedAttempt baseline failure)
              modes := [ .parallelAttempt, .parallelFailure ]
              parallelAttempt := some
                (parallelAttemptObservation index baseline failedState (.invalid failure))
              restoration := none
              scopeEvents := [] }
    | .escaped stage =>
        { run := some
            { outcome := .exception stage
              world := baseline
              trace := []
              finallyAwait := .nonePending }
          modes := [ .parallelAttempt, .parallelException ]
          parallelAttempt := some
            (parallelAttemptObservation index baseline failedState (.escaped stage))
          restoration := none
          scopeEvents := [] }
    | .retryableBalFailure =>
        let sequential := runProcessOneFromWorld spec.block input baseline
        { run := some sequential
          modes := [ .parallelAttempt, .retryableBalFailure, .scopeRestored,
            .forcedSequentialAttempt ] ++
            match sequential.outcome with
            | .accepted => []
            | .rejected _ => [ .sequentialFailure ]
            | .exception _ => [ .sequentialFailure ]
          parallelAttempt := some
            (parallelAttemptObservation index baseline failedState .retryableBalFailure)
          restoration := some { index := index, before := failedState, after := baseline }
          scopeEvents :=
            [ .disposedForBalRetry index, .reopenedForBalRetry index ] }
  else
    { run := some (runProcessOneFromWorld spec.block input baseline)
      modes := [ .sequential ]
      parallelAttempt := none
      restoration := none
      scopeEvents := [] }

structure BranchAccumulator where
  initialState : Nat
  currentState : Nat
  successfulBlocks : List BlockRun
  successfulInputs : List BlockInput
  committedStates : List Nat
  inclusionSignals : List (Nat × Bool)
  attemptModes : List AttemptMode
  parallelAttempts : List ParallelAttemptState
  scopeRestorations : List ScopeRestoration
  scopeEvents : List ScopeEvent
  trace : List Phase
  commitAttempts : Nat
  commitCompletions : Nat
  commitFailure : Option Failure


inductive BranchOutcome where
  | accepted
  | skipped
  | rejected (index : Nat) (failure : Failure)
  | exception (index : Nat) (stage : ExceptionStage)
  | finalizationException (index : Nat) (stage : ExceptionStage)
  | unmodeledParallel (index : Nat)
  deriving DecidableEq, Repr

structure BranchRun where
  outcome : BranchOutcome
  /-- Canonical state returned to the caller; pre-commit invalid/escaping branches
  return the entry state, while a post-commit finalization exception retains the
  committed prefix whose completed effects are recorded in `finalization`. -/
  logicalState : Nat
  /-- Internal evidence of the last committed prefix, not a canonical invalid-branch result. -/
  committedPrefixState : Nat
  finalization : Option ChainFinalizationObservation
  successfulBlocks : List BlockRun
  successfulInputs : List BlockInput
  failedBlock : Option BlockRun
  committedStates : List Nat
  inclusionSignals : List (Nat × Bool)
  attemptModes : List AttemptMode
  parallelAttempts : List ParallelAttemptState
  scopeRestorations : List ScopeRestoration
  scopeEvents : List ScopeEvent
  trace : List Phase
  commitAttempts : Nat
  commitCompletions : Nat
  commitFailure : Option Failure

def finishBranch (outcome : BranchOutcome) (failedBlock : Option BlockRun)
    (accumulator : BranchAccumulator) : BranchRun :=
  { outcome := outcome
    logicalState := match outcome with
      | .accepted => accumulator.currentState
      | .skipped => accumulator.initialState
      | .rejected _ _ => accumulator.initialState
      | .exception _ _ => accumulator.initialState
      | .finalizationException _ _ => accumulator.initialState
      | .unmodeledParallel _ => accumulator.initialState
    committedPrefixState := accumulator.committedStates.getLast?.getD accumulator.initialState
    finalization := none
    successfulBlocks := accumulator.successfulBlocks
    successfulInputs := accumulator.successfulInputs
    failedBlock := failedBlock
    committedStates := accumulator.committedStates
    inclusionSignals := accumulator.inclusionSignals
    attemptModes := accumulator.attemptModes
    parallelAttempts := accumulator.parallelAttempts
    scopeRestorations := accumulator.scopeRestorations
    scopeEvents := accumulator.scopeEvents
    trace := accumulator.trace
    commitAttempts := accumulator.commitAttempts
    commitCompletions := accumulator.commitCompletions
    commitFailure := accumulator.commitFailure }

def isCommitPoint (index total : Nat) : Bool :=
  index > 0 && index + 1 < total && index % 64 = 0

def postSuccessfulBlockScopeEvents (spec : BranchSpec) (index total : Nat) :
    List ScopeEvent :=
  if spec.notReadOnly && isCommitPoint index total && spec.reopenAtCheckpoint index then
    [ .disposedAtCheckpoint index, .reopenedAtCheckpoint index,
      .resetAfterSuccessfulBlock index ]
  else
    [ .resetAfterSuccessfulBlock index ]

/-- The annotation used by this relation for a completed CommitTree call is
logical-state preserving. This is not a proof of trie/database durability or
of the production hook's internal behavior. -/
theorem commitTree_completed_preserves_world
    (spec : BranchSpec) (index : Nat) (input : BlockInput) (blockRun : BlockRun)
    (_hCommit : spec.commitTree index input blockRun.world = .completed ()) :
    ({ blockRun with trace := blockRun.trace ++ [ Phase.commitTree ] } : BlockRun).world =
      blockRun.world := by
  rfl

def processBlocks (spec : BranchSpec) (policy : RetryPolicy) : Nat → Nat →
    List BlockInput → BranchAccumulator → BranchRun
  | _, _, [], accumulator => finishBranch .accepted none accumulator
  | index, total, input :: rest, accumulator =>
      let blockInput := { input with initialState := accumulator.currentState }
      let baseline := initialWorld blockInput
      let attempt := runAttempt spec policy index blockInput baseline
      let attemptedAccumulator :=
        { accumulator with
          attemptModes := accumulator.attemptModes ++ attempt.modes
          parallelAttempts := match attempt.parallelAttempt with
            | none => accumulator.parallelAttempts
            | some observation => accumulator.parallelAttempts ++ [ observation ]
          scopeRestorations := match attempt.restoration with
            | none => accumulator.scopeRestorations
            | some restoration => accumulator.scopeRestorations ++ [ restoration ]
          scopeEvents := accumulator.scopeEvents ++ attempt.scopeEvents }
      match attempt.run with
      | none =>
          finishBranch (.unmodeledParallel index) none attemptedAccumulator
      | some blockRun =>
          let tracedAccumulator :=
            { attemptedAccumulator with trace := attemptedAccumulator.trace ++ blockRun.trace }
          match blockRun.outcome with
          | .rejected failure =>
              finishBranch (.rejected index failure)
                (some { blockRun with world := baseline }) tracedAccumulator
          | .exception stage =>
              finishBranch (.exception index stage)
                (some { blockRun with world := baseline }) tracedAccumulator
          | .accepted =>
              match spec.checkInclusionList index blockInput blockRun.world with
              | .escaped stage =>
                  finishBranch (.exception index stage)
                    (some { blockRun with
                      outcome := .exception stage
                      world := baseline }) tracedAccumulator
              | .completed inclusionSignal =>
                  let inclusionAccumulator :=
                    { tracedAccumulator with
                      inclusionSignals :=
                        tracedAccumulator.inclusionSignals ++ [ (index, inclusionSignal) ] }
                  match spec.prewarmSucceeded index blockInput blockRun.world with
                  | .escaped stage =>
                      finishBranch (.exception index stage)
                        (some { blockRun with
                          outcome := .exception stage
                          world := baseline }) inclusionAccumulator
                  | .completed () =>
                      let commitAccumulator :=
                        { inclusionAccumulator with
                          trace := inclusionAccumulator.trace ++ [ Phase.commitTree ]
                          commitAttempts := inclusionAccumulator.commitAttempts + 1 }
                      match spec.commitTree index blockInput blockRun.world with
                      | .escaped stage =>
                          let failedBlock : BlockRun :=
                            { blockRun with
                              outcome := .exception stage
                              trace := blockRun.trace ++ [ Phase.commitTree ] }
                          finishBranch (.exception index stage)
                            (some { failedBlock with world := baseline })
                            { commitAccumulator with commitFailure :=
                                (.some (.escaped stage)) }
                      | .completed () =>
                          let committedBlock :=
                            { blockRun with trace := blockRun.trace ++ [ Phase.commitTree ] }
                          let nextEvents := postSuccessfulBlockScopeEvents spec index total
                          processBlocks spec policy (index + 1) total rest
                            { initialState := accumulator.initialState
                              currentState := blockRun.world.stateToken
                              successfulBlocks :=
                                inclusionAccumulator.successfulBlocks ++ [ committedBlock ]
                              successfulInputs :=
                                inclusionAccumulator.successfulInputs ++ [ input ]
                              committedStates :=
                                inclusionAccumulator.committedStates ++ [ blockRun.world.stateToken ]
                              inclusionSignals := inclusionAccumulator.inclusionSignals
                              attemptModes := commitAccumulator.attemptModes
                              parallelAttempts := commitAccumulator.parallelAttempts
                              scopeRestorations := commitAccumulator.scopeRestorations
                              scopeEvents := commitAccumulator.scopeEvents ++ nextEvents
                              trace := commitAccumulator.trace
                              commitAttempts := commitAccumulator.commitAttempts
                              commitCompletions := commitAccumulator.commitCompletions + 1
                              commitFailure := commitAccumulator.commitFailure }

def initialAccumulator (state : Nat) (trace : List Phase) (scopeEvents : List ScopeEvent) :
    BranchAccumulator :=
  { initialState := state
    currentState := state
    successfulBlocks := []
    successfulInputs := []
    committedStates := []
    inclusionSignals := []
    attemptModes := []
    parallelAttempts := []
    scopeRestorations := []
    scopeEvents := scopeEvents
    trace := trace
    commitAttempts := 0
    commitCompletions := 0
    commitFailure := none }

def disposeBranch (run : BranchRun) : BranchRun :=
  { run with scopeEvents := run.scopeEvents ++ [ .disposed ] }

def finalizationBase : ChainFinalizationObservation :=
  { totalDifficulty := 0
    head := 0
    updateSucceeded := false
    markedProcessed := false
    completedSteps := []
    failedStep := none }

def finalizationTrace (run : BranchRun) : List Phase :=
  run.trace ++ [ Phase.synchronousResultClassificationAndHeadFinalization ]

def finalizeAcceptedBranch (spec : BranchSpec) (run : BranchRun) : BranchRun :=
  match run.outcome with
  | .accepted =>
      match spec.setTotalDifficulty run.successfulBlocks run.committedStates with
      | .escaped stage =>
          { run with
            outcome := .finalizationException 0 stage
            logicalState := run.committedPrefixState
            finalization := some { finalizationBase with
              failedStep := some .totalDifficulty }
            trace := finalizationTrace run }
      | .completed totalDifficulty =>
          let afterTotalDifficulty : ChainFinalizationObservation :=
            { finalizationBase with
              totalDifficulty := totalDifficulty
              completedSteps := [ .totalDifficulty ] }
          match spec.updateMainChain totalDifficulty run.successfulBlocks run.committedStates with
          | .escaped stage =>
              { run with
                outcome := .finalizationException 1 stage
                logicalState := run.committedPrefixState
                finalization := some { afterTotalDifficulty with
                  failedStep := some .mainChainUpdate }
                trace := finalizationTrace run }
          | .completed (head, updateSucceeded) =>
              let afterHead : ChainFinalizationObservation :=
                { afterTotalDifficulty with
                  head := head
                  updateSucceeded := updateSucceeded
                  completedSteps := [ .totalDifficulty, .mainChainUpdate ] }
              match spec.markProcessed run.successfulBlocks with
              | .escaped stage =>
                  { run with
                    outcome := .finalizationException 2 stage
                    logicalState := run.committedPrefixState
                    finalization := some { afterHead with
                      failedStep := some .markProcessed }
                    trace := finalizationTrace run }
              | .completed markedProcessed =>
                  { run with
                    finalization := some
                      { afterHead with
                        markedProcessed := markedProcessed
                        completedSteps :=
                          [ .totalDifficulty, .mainChainUpdate, .markProcessed ] }
                    trace := finalizationTrace run }
  | .skipped => run
  | .rejected _ _ =>
      { run with trace := finalizationTrace run }
  | .exception _ _ => run
  | .finalizationException _ _ => run
  | .unmodeledParallel _ => run

/-- Finalization is applied after disposal only for a branch-owned scope.
Genesis uses an externally-owned scope and therefore follows the separate
pre-opened path in `runBranch`. Each hook is ordered and may escape; a partial
observation records completed effects without rewriting the committed state. -/
theorem successful_finalization_follows_scope_disposal
    (spec : BranchSpec) (run : BranchRun)
    (totalDifficulty head : Nat) (updateSucceeded markedProcessed : Bool)
    (hRun : run.outcome = .accepted)
    (hTotal : spec.setTotalDifficulty run.successfulBlocks run.committedStates =
      .completed totalDifficulty)
    (hHead : spec.updateMainChain totalDifficulty run.successfulBlocks run.committedStates =
      .completed (head, updateSucceeded))
    (hMark : spec.markProcessed run.successfulBlocks = .completed markedProcessed) :
    let observation : ChainFinalizationObservation :=
      { totalDifficulty := totalDifficulty
        head := head
        updateSucceeded := updateSucceeded
        markedProcessed := markedProcessed
        completedSteps := [ .totalDifficulty, .mainChainUpdate, .markProcessed ]
        failedStep := none }
    let finalized := finalizeAcceptedBranch spec (disposeBranch run)
    finalized.outcome = .accepted ∧
      finalized.finalization = some observation ∧
      finalized.scopeEvents = run.scopeEvents ++ [ .disposed ] := by
  simp [finalizeAcceptedBranch, finalizationBase, disposeBranch, finalizationTrace, hRun, hTotal, hHead,
    hMark]

theorem finalization_failure_preserves_committed_prefix
    (spec : BranchSpec) (run : BranchRun) (hRun : run.outcome = .accepted) :
    let finalized := finalizeAcceptedBranch spec (disposeBranch run)
    match finalized.outcome with
    | .finalizationException _ _ =>
        finalized.logicalState = run.committedPrefixState ∧
          finalized.finalization.isSome ∧
          finalized.scopeEvents = run.scopeEvents ++ [ .disposed ]
    | _ => True := by
  cases hTotal : spec.setTotalDifficulty run.successfulBlocks run.committedStates with
  | escaped stage =>
      simp [finalizeAcceptedBranch, finalizationBase, disposeBranch, finalizationTrace, hRun, hTotal]
  | completed totalDifficulty =>
      cases hHead : spec.updateMainChain totalDifficulty run.successfulBlocks run.committedStates with
      | escaped stage =>
          simp [finalizeAcceptedBranch, finalizationBase, disposeBranch, finalizationTrace, hRun, hTotal,
            hHead]
      | completed headResult =>
          cases hMark : spec.markProcessed run.successfulBlocks with
          | escaped stage =>
              simp [finalizeAcceptedBranch, finalizationBase, disposeBranch, finalizationTrace, hRun,
                hTotal, hHead, hMark]
          | completed markedProcessed =>
              simp [finalizeAcceptedBranch, finalizationBase, disposeBranch, finalizationTrace, hRun,
                hTotal, hHead, hMark]

def openScope (spec : BranchSpec) (input : BlockInput) (state : Nat) : HookResult Unit :=
  match input.phaseException .openWorldStateScope with
  | some stage => .escaped stage
  | none => spec.openWorldStateScope state

def openScopeForInputs (spec : BranchSpec) (inputs : List BlockInput) (state : Nat) :
    HookResult Unit :=
  match inputs with
  | [] => spec.openWorldStateScope state
  | input :: _ => openScope spec input state

def finishSelectedBranch (spec : BranchSpec) (policy : RetryPolicy) (state : Nat)
    (inputs : List BlockInput) (trace : List Phase) (scopeEvents : List ScopeEvent)
    (externallyOwnedScope : Bool) : BranchRun :=
  let processed := processBlocks spec policy 0 inputs.length inputs
    (initialAccumulator state trace scopeEvents)
  if externallyOwnedScope then
    finalizeAcceptedBranch spec processed
  else
    finalizeAcceptedBranch spec (disposeBranch processed)

def runBranch (spec : BranchSpec) (policy : RetryPolicy) (initialState : Nat)
    (inputs : List BlockInput) : BranchRun :=
    if inputs.isEmpty then
    finishBranch .accepted none (initialAccumulator initialState [] [])
  else
    match suggestedBlockValidation inputs with
    | .skipped trace =>
        finishBranch .skipped none (initialAccumulator initialState trace [])
    | .escaped index stage trace =>
        finishBranch (.exception index stage) none
          (initialAccumulator initialState trace [])
    | .success suggestedTrace =>
        let selectionTrace := suggestedTrace ++
          [ .synchronousBranchSelectionAndPreparation ]
        match spec.synchronousBranchSelection inputs with
        | .skipped =>
            finishBranch .skipped none
              (initialAccumulator initialState selectionTrace [])
        | .escaped stage =>
            finishBranch (.exception 0 stage) none
              (initialAccumulator initialState selectionTrace [])
        | .selected =>
            match preprocessInputs inputs with
            | .skipped senderTrace =>
                finishBranch .skipped none
                  (initialAccumulator initialState (selectionTrace ++ senderTrace) [])
            | .escaped index stage senderTrace =>
                finishBranch (.exception index stage) none
                  (initialAccumulator initialState (selectionTrace ++ senderTrace) [])
            | .success senderTrace =>
                let preparedTrace := selectionTrace ++ senderTrace
                let scopeTrace := preparedTrace ++ [ .openWorldStateScope ]
                if invalidPreOpenedScope inputs then
                  finishBranch (.exception 0 .preOpenedScope) none
                    (initialAccumulator initialState scopeTrace [])
                else if scopePreOpenedForGenesis inputs then
                  finishSelectedBranch spec policy initialState inputs scopeTrace
                    [ .preOpenedForGenesis ] true
                else
                  match openScopeForInputs spec inputs initialState with
                  | .escaped stage =>
                      finishBranch (.exception 0 stage) none
                        (initialAccumulator initialState scopeTrace [])
                  | .completed () =>
                      finishSelectedBranch spec policy initialState inputs scopeTrace
                        [ .opened ] false

def IsListPrefix {α : Type} (pre full : List α) : Prop :=
  ∃ tail, full = pre ++ tail

theorem selectAttempt_sequential_when_ineligible (policy : RetryPolicy) (index : Nat)
    (eligibility : ParallelEligibility) (h : eligibility.isEligible = false) :
    selectAttempt policy index eligibility = .sequential := by
  simp [selectAttempt, h]

theorem runBranch_empty_is_accepted (spec : BranchSpec) (policy : RetryPolicy) (state : Nat) :
    (runBranch spec policy state []).outcome = .accepted := by
  rfl

theorem runBranch_selected_composes
    (spec : BranchSpec) (policy : RetryPolicy) (state : Nat) (inputs : List BlockInput)
    (suggestedTrace senderTrace : List Phase)
    (hNonempty : inputs ≠ [])
    (hSuggested : suggestedBlockValidation inputs = .success suggestedTrace)
    (hSelected : spec.synchronousBranchSelection inputs = .selected)
    (hPreprocess : preprocessInputs inputs = .success senderTrace)
    (hNoInvalidPreOpened : invalidPreOpenedScope inputs = false)
    (hNoPreOpenedGenesis : scopePreOpenedForGenesis inputs = false)
    (hOpen : openScopeForInputs spec inputs state = .completed ()) :
    runBranch spec policy state inputs =
      finishSelectedBranch spec policy state inputs
        (suggestedTrace ++ [ .synchronousBranchSelectionAndPreparation ] ++ senderTrace ++
          [ .openWorldStateScope ]) [ .opened ] false := by
  simp [runBranch, hNonempty, hSuggested, hSelected, hPreprocess,
    hNoInvalidPreOpened, hNoPreOpenedGenesis, hOpen, finishSelectedBranch]

theorem runBranch_selected_rejection_lifts
    (spec : BranchSpec) (policy : RetryPolicy) (state : Nat) (inputs : List BlockInput)
    (suggestedTrace senderTrace : List Phase) (processResult : BranchRun)
    (failedIndex : Nat) (failure : Failure)
    (hNonempty : inputs ≠ [])
    (hSuggested : suggestedBlockValidation inputs = .success suggestedTrace)
    (hSelected : spec.synchronousBranchSelection inputs = .selected)
    (hPreprocess : preprocessInputs inputs = .success senderTrace)
    (hNoInvalidPreOpened : invalidPreOpenedScope inputs = false)
    (hNoPreOpenedGenesis : scopePreOpenedForGenesis inputs = false)
    (hOpen : openScopeForInputs spec inputs state = .completed ())
    (hProcess : processBlocks spec policy 0 inputs.length inputs
        (initialAccumulator state
          (suggestedTrace ++ [ .synchronousBranchSelectionAndPreparation ] ++ senderTrace ++
            [ .openWorldStateScope ]) [ .opened ]) = processResult)
    (hOutcome : processResult.outcome = .rejected failedIndex failure)
    (hCanonical : processResult.logicalState = state) :
    let result := runBranch spec policy state inputs
    result.outcome = .rejected failedIndex failure ∧
      result.logicalState = state ∧
      result.committedPrefixState = processResult.committedPrefixState ∧
      result.successfulBlocks = processResult.successfulBlocks ∧
      result.successfulInputs = processResult.successfulInputs ∧
      result.commitAttempts = processResult.commitAttempts ∧
      result.commitCompletions = processResult.commitCompletions ∧
      result.trace = processResult.trace ++
        [ .synchronousResultClassificationAndHeadFinalization ] := by
  have hBranch := runBranch_selected_composes spec policy state inputs suggestedTrace senderTrace
    hNonempty hSuggested hSelected hPreprocess hNoInvalidPreOpened hNoPreOpenedGenesis hOpen
  dsimp
  rw [hBranch]
  unfold finishSelectedBranch
  rw [hProcess]
  simp only [Bool.false_eq_true, ↓reduceIte]
  have hOutcome' : (disposeBranch processResult).outcome = .rejected failedIndex failure := by
    simpa [disposeBranch] using hOutcome
  rw [show finalizeAcceptedBranch spec (disposeBranch processResult) =
      { disposeBranch processResult with
          trace := (disposeBranch processResult).trace ++
            [ .synchronousResultClassificationAndHeadFinalization ] } by
    simp [finalizeAcceptedBranch, hOutcome', finalizationTrace]]
  simp [hOutcome, disposeBranch, hCanonical]

theorem runBranch_selected_exception_lifts
    (spec : BranchSpec) (policy : RetryPolicy) (state : Nat) (inputs : List BlockInput)
    (suggestedTrace senderTrace : List Phase) (processResult : BranchRun)
    (failedIndex : Nat) (stage : ExceptionStage)
    (hNonempty : inputs ≠ [])
    (hSuggested : suggestedBlockValidation inputs = .success suggestedTrace)
    (hSelected : spec.synchronousBranchSelection inputs = .selected)
    (hPreprocess : preprocessInputs inputs = .success senderTrace)
    (hNoInvalidPreOpened : invalidPreOpenedScope inputs = false)
    (hNoPreOpenedGenesis : scopePreOpenedForGenesis inputs = false)
    (hOpen : openScopeForInputs spec inputs state = .completed ())
    (hProcess : processBlocks spec policy 0 inputs.length inputs
        (initialAccumulator state
          (suggestedTrace ++ [ .synchronousBranchSelectionAndPreparation ] ++ senderTrace ++
            [ .openWorldStateScope ]) [ .opened ]) = processResult)
    (hOutcome : processResult.outcome = .exception failedIndex stage)
    (hCanonical : processResult.logicalState = state) :
    let result := runBranch spec policy state inputs
    result.outcome = .exception failedIndex stage ∧
      result.logicalState = state ∧
      result.committedPrefixState = processResult.committedPrefixState ∧
      result.successfulBlocks = processResult.successfulBlocks ∧
      result.successfulInputs = processResult.successfulInputs ∧
      result.commitAttempts = processResult.commitAttempts ∧
      result.commitCompletions = processResult.commitCompletions ∧
      result.trace = processResult.trace := by
  have hBranch := runBranch_selected_composes spec policy state inputs suggestedTrace senderTrace
    hNonempty hSuggested hSelected hPreprocess hNoInvalidPreOpened hNoPreOpenedGenesis hOpen
  dsimp
  rw [hBranch]
  unfold finishSelectedBranch
  rw [hProcess]
  simp only [Bool.false_eq_true, ↓reduceIte]
  have hOutcome' : (disposeBranch processResult).outcome = .exception failedIndex stage := by
    simpa [disposeBranch] using hOutcome
  rw [show finalizeAcceptedBranch spec (disposeBranch processResult) =
      disposeBranch processResult by
    simp [finalizeAcceptedBranch, hOutcome']]
  simp [hOutcome, disposeBranch, hCanonical]

theorem preprocess_exception_stops_before_scope_and_commit
    (spec : BranchSpec) (policy : RetryPolicy) (state : Nat) (inputs : List BlockInput)
    (failedIndex : Nat) (stage : ExceptionStage) (trace : List Phase)
    (suggestedTrace : List Phase)
    (hSuggested : suggestedBlockValidation inputs = .success suggestedTrace)
    (hSelected : spec.synchronousBranchSelection inputs = .selected)
    (hPreprocess : preprocessInputs inputs = .escaped failedIndex stage trace) :
    let result := runBranch spec policy state inputs
    result.outcome = .exception failedIndex stage ∧
      result.trace = suggestedTrace ++ [ .synchronousBranchSelectionAndPreparation ] ++ trace ∧
      result.commitAttempts = 0 ∧ result.commitCompletions = 0 := by
  have hNonempty : inputs ≠ [] := by
    intro hEmpty
    subst inputs
    have impossible : PreprocessResult.success [] =
        PreprocessResult.escaped failedIndex stage trace := by
      simp [preprocessInputs, preprocessSenderAuthority] at hPreprocess
    cases impossible
  simp [runBranch, hNonempty, hSuggested, hSelected, hPreprocess, finishBranch,
    initialAccumulator]

theorem isListPrefix_refl {α : Type} (items : List α) : IsListPrefix items items := by
  exact ⟨[], by simp⟩

theorem isListPrefix_append {α : Type} (items suffix : List α) :
    IsListPrefix items (items ++ suffix) := by
  exact ⟨suffix, rfl⟩

theorem isListPrefix_trans {α : Type} {first second third : List α}
    (hFirst : IsListPrefix first second) (hSecond : IsListPrefix second third) :
    IsListPrefix first third := by
  rcases hFirst with ⟨middle, rfl⟩
  rcases hSecond with ⟨tail, rfl⟩
  exact ⟨middle ++ tail, by simp [List.append_assoc]⟩

def accumulatorCommitAligned (accumulator : BranchAccumulator) : Prop :=
  accumulator.commitAttempts = accumulator.successfulBlocks.length ∧
    accumulator.commitCompletions = accumulator.successfulBlocks.length ∧
    accumulator.committedStates.length = accumulator.successfulBlocks.length ∧
    accumulator.successfulBlocks.map (fun block => block.world.stateToken) =
      accumulator.committedStates

def branchRunCommitAligned (run : BranchRun) : Prop :=
  run.commitAttempts = run.successfulBlocks.length ∧
    run.commitCompletions = run.successfulBlocks.length ∧
    run.committedStates.length = run.successfulBlocks.length ∧
    run.successfulBlocks.map (fun block => block.world.stateToken) = run.committedStates

def lastCommittedState (fallback : Nat) (states : List Nat) : Nat :=
  states.getLast?.getD fallback

theorem processBlocks_accepted_commit_aligned
    (spec : BranchSpec) (policy : RetryPolicy) (index total : Nat)
    (inputs : List BlockInput) (accumulator : BranchAccumulator)
    (hAccumulator : accumulatorCommitAligned accumulator)
    (hAccepted : (processBlocks spec policy index total inputs accumulator).outcome = .accepted) :
    branchRunCommitAligned (processBlocks spec policy index total inputs accumulator) := by
  induction inputs generalizing index total accumulator with
  | nil =>
      simpa [processBlocks, finishBranch, branchRunCommitAligned,
        accumulatorCommitAligned] using hAccumulator
  | cons input rest inductionHypothesis =>
      let blockInput := { input with initialState := accumulator.currentState }
      let baseline := initialWorld blockInput
      let attempt := runAttempt spec policy index blockInput baseline
      cases hAttempt : attempt.run with
      | none =>
          have hAttempt' : (runAttempt spec policy index blockInput baseline).run = none := by
            simpa [attempt] using hAttempt
          have hFalse : False := by
            simp [processBlocks, blockInput, baseline, hAttempt', finishBranch] at hAccepted
          exact hFalse.elim
      | some blockRun =>
          have hAttempt' : (runAttempt spec policy index blockInput baseline).run = some blockRun := by
            simpa [attempt] using hAttempt
          cases hOutcome : blockRun.outcome with
          | rejected failure =>
              have hFalse : False := by
                simp [processBlocks, blockInput, baseline, hAttempt', hOutcome, finishBranch] at hAccepted
              exact hFalse.elim
          | exception stage =>
              have hFalse : False := by
                simp [processBlocks, blockInput, baseline, hAttempt', hOutcome, finishBranch] at hAccepted
              exact hFalse.elim
          | accepted =>
              cases hInclusion : spec.checkInclusionList index blockInput blockRun.world with
              | escaped stage =>
                  have hFalse : False := by
                    simp [processBlocks, blockInput, baseline, hAttempt', hOutcome, hInclusion,
                      finishBranch] at hAccepted
                  exact hFalse.elim
              | completed inclusionSignal =>
                  cases hPrewarm : spec.prewarmSucceeded index blockInput blockRun.world with
                  | escaped stage =>
                      have hFalse : False := by
                        simp [processBlocks, blockInput, baseline, hAttempt', hOutcome, hInclusion,
                          hPrewarm, finishBranch] at hAccepted
                      exact hFalse.elim
                  | completed _ =>
                      cases hCommit : spec.commitTree index blockInput blockRun.world with
                      | escaped stage =>
                          have hFalse : False := by
                            simp [processBlocks, blockInput, baseline, hAttempt', hOutcome, hInclusion,
                              hPrewarm, hCommit, finishBranch] at hAccepted
                          exact hFalse.elim
                      | completed _ =>
                          let committedBlock : BlockRun :=
                            { blockRun with trace := blockRun.trace ++ [ Phase.commitTree ] }
                          let nextAccumulator : BranchAccumulator :=
                            { initialState := accumulator.initialState
                              currentState := blockRun.world.stateToken
                              successfulBlocks := accumulator.successfulBlocks ++ [ committedBlock ]
                              successfulInputs := accumulator.successfulInputs ++ [ input ]
                              committedStates :=
                                accumulator.committedStates ++ [ blockRun.world.stateToken ]
                              inclusionSignals :=
                                accumulator.inclusionSignals ++ [ (index, inclusionSignal) ]
                              attemptModes :=
                                accumulator.attemptModes ++ attempt.modes
                              parallelAttempts :=
                                match attempt.parallelAttempt with
                                | none => accumulator.parallelAttempts
                                | some observation => accumulator.parallelAttempts ++ [ observation ]
                              scopeRestorations :=
                                match attempt.restoration with
                                | none => accumulator.scopeRestorations
                                | some restoration => accumulator.scopeRestorations ++ [ restoration ]
                              scopeEvents := accumulator.scopeEvents ++ attempt.scopeEvents ++
                                postSuccessfulBlockScopeEvents spec index total
                              trace := accumulator.trace ++ blockRun.trace ++ [ Phase.commitTree ]
                              commitAttempts := accumulator.commitAttempts + 1
                              commitCompletions := accumulator.commitCompletions + 1
                              commitFailure := accumulator.commitFailure }
                          have hNextAccumulator : accumulatorCommitAligned nextAccumulator := by
                            rcases hAccumulator with
                              ⟨hAttempts, hCompletions, hStates, hBlocks⟩
                            simp [accumulatorCommitAligned, nextAccumulator, committedBlock,
                              hAttempts, hCompletions, hStates, hBlocks, List.map_append,
                              List.length_append]
                          have hAcceptedTail :
                              (processBlocks spec policy (index + 1) total rest nextAccumulator).outcome =
                                .accepted := by
                            simpa [processBlocks, blockInput, baseline, attempt, hAttempt', hOutcome,
                              hInclusion, hPrewarm, hCommit, finishBranch, committedBlock,
                              nextAccumulator] using hAccepted
                          have hTail := inductionHypothesis (index + 1) total nextAccumulator
                            hNextAccumulator hAcceptedTail
                          simpa [processBlocks, blockInput, baseline, attempt, hAttempt', hOutcome,
                            hInclusion, hPrewarm, hCommit, finishBranch, committedBlock,
                            nextAccumulator, branchRunCommitAligned] using hTail

theorem processBlocks_rejected_preserves_successful_prefix
    (spec : BranchSpec) (policy : RetryPolicy) (index total : Nat)
    (inputs : List BlockInput) (accumulator : BranchAccumulator) (result : BranchRun)
    (hRun : processBlocks spec policy index total inputs accumulator = result)
    (hRejected : ∃ failedIndex failure, result.outcome = .rejected failedIndex failure) :
    IsListPrefix accumulator.successfulBlocks result.successfulBlocks := by
  subst result
  induction inputs generalizing index total accumulator with
  | nil =>
      exact isListPrefix_refl _
  | cons input rest inductionHypothesis =>
      let blockInput := { input with initialState := accumulator.currentState }
      let baseline := initialWorld blockInput
      let attempt := runAttempt spec policy index blockInput baseline
      cases hAttempt : attempt.run with
      | none =>
          have hAttempt' : (runAttempt spec policy index blockInput baseline).run = none := by
            simpa [attempt] using hAttempt
          simpa [processBlocks, blockInput, baseline, hAttempt', finishBranch] using
            (isListPrefix_refl accumulator.successfulBlocks)
      | some blockRun =>
          have hAttempt' : (runAttempt spec policy index blockInput baseline).run = some blockRun := by
            simpa [attempt] using hAttempt
          cases hOutcome : blockRun.outcome with
          | rejected failure =>
              simpa [processBlocks, blockInput, baseline, hAttempt', hOutcome, finishBranch] using
                (isListPrefix_refl accumulator.successfulBlocks)
          | exception stage =>
              simpa [processBlocks, blockInput, baseline, hAttempt', hOutcome, finishBranch] using
                (isListPrefix_refl accumulator.successfulBlocks)
          | accepted =>
              cases hInclusion : spec.checkInclusionList index blockInput blockRun.world with
              | escaped stage =>
                   simpa [processBlocks, blockInput, baseline, hAttempt', hOutcome, hInclusion,
                     finishBranch] using
                     (isListPrefix_refl accumulator.successfulBlocks)
              | completed inclusionSignal =>
                  cases hPrewarm : spec.prewarmSucceeded index blockInput blockRun.world with
                  | escaped stage =>
                       simpa [processBlocks, blockInput, baseline, hAttempt', hOutcome, hInclusion, hPrewarm,
                         finishBranch] using (isListPrefix_refl accumulator.successfulBlocks)
                  | completed _ =>
                      cases hCommit : spec.commitTree index blockInput blockRun.world with
                      | escaped stage =>
                           simpa [processBlocks, blockInput, baseline, hAttempt', hOutcome, hInclusion,
                             hPrewarm, hCommit, finishBranch] using
                             (isListPrefix_refl accumulator.successfulBlocks)
                      | completed _ =>
                          let committedBlock : BlockRun :=
                            { blockRun with trace := blockRun.trace ++ [ Phase.commitTree ] }
                          let nextAccumulator : BranchAccumulator :=
                            { initialState := accumulator.initialState
                              currentState := blockRun.world.stateToken
                              successfulBlocks := accumulator.successfulBlocks ++ [ committedBlock ]
                              successfulInputs := accumulator.successfulInputs ++ [ input ]
                              committedStates :=
                                accumulator.committedStates ++ [ blockRun.world.stateToken ]
                              inclusionSignals :=
                                accumulator.inclusionSignals ++ [ (index, inclusionSignal) ]
                              attemptModes :=
                                accumulator.attemptModes ++ attempt.modes
                              parallelAttempts :=
                                match attempt.parallelAttempt with
                                | none => accumulator.parallelAttempts
                                | some observation => accumulator.parallelAttempts ++ [ observation ]
                              scopeRestorations :=
                                match attempt.restoration with
                                | none => accumulator.scopeRestorations
                                | some restoration => accumulator.scopeRestorations ++ [ restoration ]
                              scopeEvents := accumulator.scopeEvents ++ attempt.scopeEvents ++
                                postSuccessfulBlockScopeEvents spec index total
                              trace := accumulator.trace ++ blockRun.trace ++ [ Phase.commitTree ]
                              commitAttempts := accumulator.commitAttempts + 1
                              commitCompletions := accumulator.commitCompletions + 1
                              commitFailure := accumulator.commitFailure }
                          have hRejectedTail :
                              ∃ failedIndex failure,
                                (processBlocks spec policy (index + 1) total rest
                                  nextAccumulator).outcome = .rejected failedIndex failure := by
                            simpa [processBlocks, blockInput, baseline, attempt, hAttempt', hOutcome,
                              hInclusion, hPrewarm, hCommit, finishBranch, committedBlock, nextAccumulator]
                              using hRejected
                          have hTail := inductionHypothesis (index + 1) total nextAccumulator
                            hRejectedTail
                          have hPrefix := isListPrefix_trans
                            (isListPrefix_append accumulator.successfulBlocks [ committedBlock ]) hTail
                          have hProcess :
                              processBlocks spec policy index total (input :: rest) accumulator =
                                processBlocks spec policy (index + 1) total rest nextAccumulator := by
                            simp [processBlocks, blockInput, baseline, attempt, hAttempt,
                              hOutcome, hInclusion, hPrewarm, hCommit, committedBlock,
                              nextAccumulator]
                          rw [hProcess]
                          exact hPrefix

theorem processBlocks_exception_preserves_successful_prefix
    (spec : BranchSpec) (policy : RetryPolicy) (index total : Nat)
    (inputs : List BlockInput) (accumulator : BranchAccumulator) (result : BranchRun)
    (hRun : processBlocks spec policy index total inputs accumulator = result)
    (hException : ∃ failedIndex stage, result.outcome = .exception failedIndex stage) :
    IsListPrefix accumulator.successfulBlocks result.successfulBlocks := by
  subst result
  induction inputs generalizing index total accumulator with
  | nil =>
      exact isListPrefix_refl _
  | cons input rest inductionHypothesis =>
      let blockInput := { input with initialState := accumulator.currentState }
      let baseline := initialWorld blockInput
      let attempt := runAttempt spec policy index blockInput baseline
      cases hAttempt : attempt.run with
      | none =>
          have hFalse : False := by
            simp [processBlocks, blockInput, baseline, attempt, hAttempt, finishBranch] at hException
          exact hFalse.elim
      | some blockRun =>
          have hAttempt' : (runAttempt spec policy index blockInput baseline).run =
              some blockRun := by
            simpa [attempt] using hAttempt
          cases hOutcome : blockRun.outcome with
          | rejected failure =>
              have hFalse : False := by
                simp [processBlocks, blockInput, baseline, hAttempt', hOutcome, finishBranch] at hException
              exact hFalse.elim
          | exception stage =>
              simpa [processBlocks, blockInput, baseline, hAttempt', hOutcome, finishBranch] using
                (isListPrefix_refl accumulator.successfulBlocks)
          | accepted =>
              cases hInclusion : spec.checkInclusionList index blockInput blockRun.world with
              | escaped stage =>
                  simpa [processBlocks, blockInput, baseline, hAttempt', hOutcome, hInclusion,
                    finishBranch] using (isListPrefix_refl accumulator.successfulBlocks)
              | completed inclusionSignal =>
                  cases hPrewarm : spec.prewarmSucceeded index blockInput blockRun.world with
                  | escaped stage =>
                      simpa [processBlocks, blockInput, baseline, hAttempt', hOutcome,
                        hInclusion, hPrewarm, finishBranch] using
                        (isListPrefix_refl accumulator.successfulBlocks)
                  | completed _ =>
                      cases hCommit : spec.commitTree index blockInput blockRun.world with
                      | escaped stage =>
                          simpa [processBlocks, blockInput, baseline, hAttempt', hOutcome,
                            hInclusion, hPrewarm, hCommit, finishBranch] using
                            (isListPrefix_refl accumulator.successfulBlocks)
                      | completed _ =>
                          let committedBlock : BlockRun :=
                            { blockRun with trace := blockRun.trace ++ [ Phase.commitTree ] }
                          let nextAccumulator : BranchAccumulator :=
                            { initialState := accumulator.initialState
                              currentState := blockRun.world.stateToken
                              successfulBlocks := accumulator.successfulBlocks ++ [ committedBlock ]
                              successfulInputs := accumulator.successfulInputs ++ [ input ]
                              committedStates :=
                                accumulator.committedStates ++ [ blockRun.world.stateToken ]
                              inclusionSignals :=
                                accumulator.inclusionSignals ++ [ (index, inclusionSignal) ]
                              attemptModes := accumulator.attemptModes ++ attempt.modes
                              parallelAttempts :=
                                match attempt.parallelAttempt with
                                | none => accumulator.parallelAttempts
                                | some observation => accumulator.parallelAttempts ++ [ observation ]
                              scopeRestorations :=
                                match attempt.restoration with
                                | none => accumulator.scopeRestorations
                                | some restoration => accumulator.scopeRestorations ++ [ restoration ]
                              scopeEvents := accumulator.scopeEvents ++ attempt.scopeEvents ++
                                postSuccessfulBlockScopeEvents spec index total
                              trace := accumulator.trace ++ blockRun.trace ++ [ Phase.commitTree ]
                              commitAttempts := accumulator.commitAttempts + 1
                              commitCompletions := accumulator.commitCompletions + 1
                              commitFailure := accumulator.commitFailure }
                          have hExceptionTail :
                                ∃ failedIndex stage,
                                  (processBlocks spec policy (index + 1) total rest
                                    nextAccumulator).outcome = .exception failedIndex stage := by
                            simpa [processBlocks, blockInput, baseline, attempt, hAttempt,
                              hAttempt', hOutcome, hInclusion, hPrewarm, hCommit, finishBranch,
                              committedBlock, nextAccumulator] using hException
                          have hTail := inductionHypothesis (index + 1) total nextAccumulator
                            hExceptionTail
                          have hPrefix := isListPrefix_trans
                            (isListPrefix_append accumulator.successfulBlocks [ committedBlock ]) hTail
                          have hProcess :
                              processBlocks spec policy index total (input :: rest) accumulator =
                                processBlocks spec policy (index + 1) total rest nextAccumulator := by
                            simp [processBlocks, blockInput, baseline, attempt, hAttempt,
                              hOutcome, hInclusion, hPrewarm, hCommit, committedBlock,
                              nextAccumulator]
                          rw [hProcess]
                          exact hPrefix

/-! A rejected process returns an explicit first-`k` input prefix. The
successful inputs are the original prefix in order, and the reported index is
the starting index plus `k`; no canonical state is inferred from this internal
evidence. -/
theorem processBlocks_rejected_returns_first_k_inputs
    (spec : BranchSpec) (policy : RetryPolicy) (index total : Nat)
    (inputs : List BlockInput) (accumulator : BranchAccumulator) (result : BranchRun)
    (hRun : processBlocks spec policy index total inputs accumulator = result)
    (hRejected : ∃ failedIndex failure, result.outcome = .rejected failedIndex failure) :
    ∃ processedPrefix failedInput suffix failure,
      inputs = processedPrefix ++ failedInput :: suffix ∧
      result.successfulInputs = accumulator.successfulInputs ++ processedPrefix ∧
      result.outcome = .rejected (index + processedPrefix.length) failure := by
  subst result
  rcases hRejected with ⟨failedIndex, failure, hRejected⟩
  induction inputs generalizing index total accumulator with
  | nil =>
      simp [processBlocks, finishBranch] at hRejected
  | cons input rest inductionHypothesis =>
      let blockInput := { input with initialState := accumulator.currentState }
      let baseline := initialWorld blockInput
      let attempt := runAttempt spec policy index blockInput baseline
      cases hAttempt : attempt.run with
      | none =>
          have hImpossible :
              BranchOutcome.unmodeledParallel index =
                BranchOutcome.rejected failedIndex failure := by
            simp [processBlocks, blockInput, baseline, attempt, hAttempt, finishBranch] at hRejected
          cases hImpossible
      | some blockRun =>
          have hAttempt' : (runAttempt spec policy index blockInput baseline).run =
              some blockRun := by
            simpa [attempt] using hAttempt
          cases hOutcome : blockRun.outcome with
          | rejected blockFailure =>
              have hOutcome' :
                  BranchOutcome.rejected index blockFailure =
                    BranchOutcome.rejected failedIndex failure := by
                simpa [processBlocks, blockInput, baseline, hAttempt', hOutcome, finishBranch]
                  using hRejected
              cases hOutcome'
              refine ⟨[], input, rest, failure, by simp, ?_, ?_⟩
              · simp [processBlocks, blockInput, baseline, hAttempt', hOutcome, finishBranch]
              · simp [processBlocks, blockInput, baseline, hAttempt', hOutcome, finishBranch]
          | exception stage =>
              have hImpossible :
                  BranchOutcome.exception index stage =
                    BranchOutcome.rejected failedIndex failure := by
                simp [processBlocks, blockInput, baseline, hAttempt', hOutcome, finishBranch] at hRejected
              cases hImpossible
          | accepted =>
              cases hInclusion : spec.checkInclusionList index blockInput blockRun.world with
              | escaped stage =>
                  have hImpossible :
                      BranchOutcome.exception index stage =
                        BranchOutcome.rejected failedIndex failure := by
                    simp [processBlocks, blockInput, baseline, hAttempt', hOutcome, hInclusion,
                      finishBranch] at hRejected
                  cases hImpossible
              | completed inclusionSignal =>
                  cases hPrewarm : spec.prewarmSucceeded index blockInput blockRun.world with
                  | escaped stage =>
                      have hImpossible :
                          BranchOutcome.exception index stage =
                            BranchOutcome.rejected failedIndex failure := by
                        simp [processBlocks, blockInput, baseline, hAttempt', hOutcome,
                          hInclusion, hPrewarm, finishBranch] at hRejected
                      cases hImpossible
                  | completed _ =>
                      cases hCommit : spec.commitTree index blockInput blockRun.world with
                      | escaped stage =>
                          have hImpossible :
                              BranchOutcome.exception index stage =
                                BranchOutcome.rejected failedIndex failure := by
                            simp [processBlocks, blockInput, baseline, hAttempt', hOutcome,
                              hInclusion, hPrewarm, hCommit, finishBranch] at hRejected
                          cases hImpossible
                      | completed _ =>
                          let committedBlock : BlockRun :=
                            { blockRun with trace := blockRun.trace ++ [ Phase.commitTree ] }
                          let nextAccumulator : BranchAccumulator :=
                            { initialState := accumulator.initialState
                              currentState := blockRun.world.stateToken
                              successfulBlocks := accumulator.successfulBlocks ++ [ committedBlock ]
                              successfulInputs := accumulator.successfulInputs ++ [ input ]
                              committedStates :=
                                accumulator.committedStates ++ [ blockRun.world.stateToken ]
                              inclusionSignals :=
                                accumulator.inclusionSignals ++ [ (index, inclusionSignal) ]
                              attemptModes := accumulator.attemptModes ++ attempt.modes
                              parallelAttempts :=
                                match attempt.parallelAttempt with
                                | none => accumulator.parallelAttempts
                                | some observation => accumulator.parallelAttempts ++ [ observation ]
                              scopeRestorations :=
                                match attempt.restoration with
                                | none => accumulator.scopeRestorations
                                | some restoration => accumulator.scopeRestorations ++ [ restoration ]
                              scopeEvents := accumulator.scopeEvents ++ attempt.scopeEvents ++
                                postSuccessfulBlockScopeEvents spec index total
                              trace := accumulator.trace ++ blockRun.trace ++ [ Phase.commitTree ]
                              commitAttempts := accumulator.commitAttempts + 1
                              commitCompletions := accumulator.commitCompletions + 1
                              commitFailure := accumulator.commitFailure }
                          have hRejectedTail :
                                (processBlocks spec policy (index + 1) total rest
                                  nextAccumulator).outcome = .rejected failedIndex failure := by
                            simpa [processBlocks, blockInput, baseline, attempt, hAttempt, hAttempt', hOutcome,
                              hInclusion, hPrewarm, hCommit, finishBranch, committedBlock,
                              nextAccumulator] using hRejected
                          obtain ⟨processedPrefix, failedInput, suffix, tailFailure, hInputs,
                            hSuccessfulInputs, hTailOutcome⟩ :=
                            inductionHypothesis (index + 1) total nextAccumulator hRejectedTail
                          refine ⟨input :: processedPrefix, failedInput, suffix, tailFailure, ?_, ?_, ?_⟩
                          · simp [hInputs]
                          · simpa [processBlocks, blockInput, baseline, attempt, hAttempt, hAttempt',
                              hOutcome, hInclusion, hPrewarm, hCommit, finishBranch,
                              committedBlock, nextAccumulator, List.append_assoc] using
                              hSuccessfulInputs
                          · calc
                              (processBlocks spec policy index total (input :: rest) accumulator).outcome =
                                  (processBlocks spec policy (index + 1) total rest nextAccumulator).outcome := by
                                simp [processBlocks, blockInput, baseline, attempt, hAttempt,
                                  hOutcome, hInclusion, hPrewarm, hCommit,
                                  committedBlock, nextAccumulator]
                              _ = .rejected (index + 1 + processedPrefix.length) tailFailure := hTailOutcome
                              _ = .rejected (index + (input :: processedPrefix).length) tailFailure := by
                                simp
                                omega


/-! An escaping process also has a first failing input. Unlike the rejected
path, this includes exceptions from the per-block hooks as well as the
`ProcessOne` result. The suffix is untouched and the reported index is the
starting index plus the successful prefix length. -/
theorem processBlocks_exception_returns_first_k_inputs
    (spec : BranchSpec) (policy : RetryPolicy) (index total : Nat)
    (inputs : List BlockInput) (accumulator : BranchAccumulator) (result : BranchRun)
    (hRun : processBlocks spec policy index total inputs accumulator = result)
    (hException : ∃ failedIndex stage, result.outcome = .exception failedIndex stage) :
    ∃ processedPrefix failedInput suffix stage,
      inputs = processedPrefix ++ failedInput :: suffix ∧
      result.successfulInputs = accumulator.successfulInputs ++ processedPrefix ∧
      result.outcome = .exception (index + processedPrefix.length) stage := by
  subst result
  rcases hException with ⟨failedIndex, stage, hException⟩
  induction inputs generalizing index total accumulator with
  | nil =>
      simp [processBlocks, finishBranch] at hException
  | cons input rest inductionHypothesis =>
      let blockInput := { input with initialState := accumulator.currentState }
      let baseline := initialWorld blockInput
      let attempt := runAttempt spec policy index blockInput baseline
      cases hAttempt : attempt.run with
      | none =>
          have hImpossible :
              BranchOutcome.unmodeledParallel index =
                BranchOutcome.exception failedIndex stage := by
            simp [processBlocks, blockInput, baseline, attempt, hAttempt, finishBranch] at hException
          cases hImpossible
      | some blockRun =>
          have hAttempt' : (runAttempt spec policy index blockInput baseline).run =
              some blockRun := by
            simpa [attempt] using hAttempt
          cases hOutcome : blockRun.outcome with
          | rejected blockFailure =>
              have hImpossible :
                  BranchOutcome.rejected index blockFailure =
                    BranchOutcome.exception failedIndex stage := by
                simp [processBlocks, blockInput, baseline, hAttempt', hOutcome, finishBranch] at hException
              cases hImpossible
          | exception blockStage =>
              have hOutcome' :
                  BranchOutcome.exception index blockStage =
                    BranchOutcome.exception failedIndex stage := by
                simpa [processBlocks, blockInput, baseline, hAttempt', hOutcome, finishBranch]
                  using hException
              refine ⟨[], input, rest, blockStage, by simp, ?_, ?_⟩
              · simp [processBlocks, blockInput, baseline, hAttempt', hOutcome, finishBranch]
              · simp [processBlocks, blockInput, baseline, hAttempt', hOutcome, finishBranch]
          | accepted =>
              cases hInclusion : spec.checkInclusionList index blockInput blockRun.world with
              | escaped inclusionStage =>
                  have hOutcome' :
                      BranchOutcome.exception index inclusionStage =
                        BranchOutcome.exception failedIndex stage := by
                    simpa [processBlocks, blockInput, baseline, hAttempt', hOutcome,
                      hInclusion, finishBranch] using hException
                  refine ⟨[], input, rest, inclusionStage, by simp, ?_, ?_⟩
                  · simp [processBlocks, blockInput, baseline, hAttempt', hOutcome,
                      hInclusion, finishBranch]
                  · simp [processBlocks, blockInput, baseline, hAttempt', hOutcome,
                      hInclusion, finishBranch]
              | completed inclusionSignal =>
                  cases hPrewarm : spec.prewarmSucceeded index blockInput blockRun.world with
                  | escaped prewarmStage =>
                      have hOutcome' :
                          BranchOutcome.exception index prewarmStage =
                            BranchOutcome.exception failedIndex stage := by
                        simpa [processBlocks, blockInput, baseline, hAttempt', hOutcome,
                          hInclusion, hPrewarm, finishBranch] using hException
                      refine ⟨[], input, rest, prewarmStage, by simp, ?_, ?_⟩
                      · simp [processBlocks, blockInput, baseline, hAttempt', hOutcome,
                          hInclusion, hPrewarm, finishBranch]
                      · simp [processBlocks, blockInput, baseline, hAttempt', hOutcome,
                          hInclusion, hPrewarm, finishBranch]
                  | completed _ =>
                      cases hCommit : spec.commitTree index blockInput blockRun.world with
                      | escaped commitStage =>
                          have hOutcome' :
                              BranchOutcome.exception index commitStage =
                                BranchOutcome.exception failedIndex stage := by
                            simpa [processBlocks, blockInput, baseline, hAttempt', hOutcome,
                              hInclusion, hPrewarm, hCommit, finishBranch] using hException
                          refine ⟨[], input, rest, commitStage, by simp, ?_, ?_⟩
                          · simp [processBlocks, blockInput, baseline, hAttempt', hOutcome,
                              hInclusion, hPrewarm, hCommit, finishBranch]
                          · simp [processBlocks, blockInput, baseline, hAttempt', hOutcome,
                              hInclusion, hPrewarm, hCommit, finishBranch]
                      | completed _ =>
                          let committedBlock : BlockRun :=
                            { blockRun with trace := blockRun.trace ++ [ Phase.commitTree ] }
                          let nextAccumulator : BranchAccumulator :=
                            { initialState := accumulator.initialState
                              currentState := blockRun.world.stateToken
                              successfulBlocks := accumulator.successfulBlocks ++ [ committedBlock ]
                              successfulInputs := accumulator.successfulInputs ++ [ input ]
                              committedStates :=
                                accumulator.committedStates ++ [ blockRun.world.stateToken ]
                              inclusionSignals :=
                                accumulator.inclusionSignals ++ [ (index, inclusionSignal) ]
                              attemptModes := accumulator.attemptModes ++ attempt.modes
                              parallelAttempts :=
                                match attempt.parallelAttempt with
                                | none => accumulator.parallelAttempts
                                | some observation => accumulator.parallelAttempts ++ [ observation ]
                              scopeRestorations :=
                                match attempt.restoration with
                                | none => accumulator.scopeRestorations
                                | some restoration => accumulator.scopeRestorations ++ [ restoration ]
                              scopeEvents := accumulator.scopeEvents ++ attempt.scopeEvents ++
                                postSuccessfulBlockScopeEvents spec index total
                              trace := accumulator.trace ++ blockRun.trace ++ [ Phase.commitTree ]
                              commitAttempts := accumulator.commitAttempts + 1
                              commitCompletions := accumulator.commitCompletions + 1
                              commitFailure := accumulator.commitFailure }
                          have hExceptionTail :
                                (processBlocks spec policy (index + 1) total rest
                                  nextAccumulator).outcome = .exception failedIndex stage := by
                            simpa [processBlocks, blockInput, baseline, attempt, hAttempt,
                              hAttempt', hOutcome, hInclusion, hPrewarm, hCommit, finishBranch,
                              committedBlock, nextAccumulator] using hException
                          obtain ⟨processedPrefix, failedInput, suffix, tailStage, hInputs,
                            hSuccessfulInputs, hTailOutcome⟩ :=
                            inductionHypothesis (index + 1) total nextAccumulator hExceptionTail
                          refine ⟨input :: processedPrefix, failedInput, suffix, tailStage,
                            ?_, ?_, ?_⟩
                          · simp [hInputs]
                          · simpa [processBlocks, blockInput, baseline, attempt, hAttempt,
                              hAttempt', hOutcome, hInclusion, hPrewarm, hCommit, finishBranch,
                              committedBlock, nextAccumulator, List.append_assoc] using
                              hSuccessfulInputs
                          · calc
                              (processBlocks spec policy index total (input :: rest) accumulator).outcome =
                                  (processBlocks spec policy (index + 1) total rest nextAccumulator).outcome := by
                                simp [processBlocks, blockInput, baseline, attempt, hAttempt,
                                  hOutcome, hInclusion, hPrewarm, hCommit,
                                  committedBlock, nextAccumulator]
                              _ = .exception (index + 1 + processedPrefix.length) tailStage :=
                                hTailOutcome
                              _ = .exception (index + (input :: processedPrefix).length) tailStage := by
                                simp
                                omega

theorem processBlocks_rejected_canonical_state
    (spec : BranchSpec) (policy : RetryPolicy) (index total : Nat)
    (inputs : List BlockInput) (accumulator : BranchAccumulator) (result : BranchRun)
    (hRun : processBlocks spec policy index total inputs accumulator = result)
    (hRejected : ∃ failedIndex failure, result.outcome = .rejected failedIndex failure) :
    result.logicalState = accumulator.initialState := by
  subst result
  rcases hRejected with ⟨failedIndex, failure, hRejected⟩
  induction inputs generalizing index total accumulator with
  | nil =>
      simp [processBlocks, finishBranch] at hRejected
  | cons input rest inductionHypothesis =>
      let blockInput := { input with initialState := accumulator.currentState }
      let baseline := initialWorld blockInput
      let attempt := runAttempt spec policy index blockInput baseline
      cases hAttempt : attempt.run with
      | none =>
          have hAttempt' : (runAttempt spec policy index blockInput baseline).run = none := by
            simpa [attempt] using hAttempt
          have hFalse : False := by
            simp [processBlocks, blockInput, baseline, hAttempt', finishBranch] at hRejected
          exact hFalse.elim
      | some blockRun =>
          have hAttempt' : (runAttempt spec policy index blockInput baseline).run = some blockRun := by
            simpa [attempt] using hAttempt
          cases hOutcome : blockRun.outcome with
          | rejected failure =>
              simp [processBlocks, blockInput, baseline, hAttempt', hOutcome, finishBranch]
          | exception stage =>
              have hFalse : False := by
                simp [processBlocks, blockInput, baseline, hAttempt', hOutcome, finishBranch] at hRejected
              exact hFalse.elim
          | accepted =>
              cases hInclusion : spec.checkInclusionList index blockInput blockRun.world with
              | escaped stage =>
                  have hFalse : False := by
                    simp [processBlocks, blockInput, baseline, hAttempt', hOutcome, hInclusion,
                      finishBranch] at hRejected
                  exact hFalse.elim
              | completed inclusionSignal =>
                  cases hPrewarm : spec.prewarmSucceeded index blockInput blockRun.world with
                  | escaped stage =>
                      have hFalse : False := by
                        simp [processBlocks, blockInput, baseline, hAttempt', hOutcome, hInclusion,
                          hPrewarm, finishBranch] at hRejected
                      exact hFalse.elim
                  | completed _ =>
                      cases hCommit : spec.commitTree index blockInput blockRun.world with
                      | escaped stage =>
                          have hFalse : False := by
                            simp [processBlocks, blockInput, baseline, hAttempt', hOutcome, hInclusion,
                              hPrewarm, hCommit, finishBranch] at hRejected
                          exact hFalse.elim
                      | completed _ =>
                          let committedBlock : BlockRun :=
                            { blockRun with trace := blockRun.trace ++ [ Phase.commitTree ] }
                          let nextAccumulator : BranchAccumulator :=
                            { initialState := accumulator.initialState
                              currentState := blockRun.world.stateToken
                              successfulBlocks := accumulator.successfulBlocks ++ [ committedBlock ]
                              successfulInputs := accumulator.successfulInputs ++ [ input ]
                              committedStates :=
                                accumulator.committedStates ++ [ blockRun.world.stateToken ]
                              inclusionSignals :=
                                accumulator.inclusionSignals ++ [ (index, inclusionSignal) ]
                              attemptModes := accumulator.attemptModes ++ attempt.modes
                              parallelAttempts :=
                                match attempt.parallelAttempt with
                                | none => accumulator.parallelAttempts
                                | some observation => accumulator.parallelAttempts ++ [ observation ]
                              scopeRestorations :=
                                match attempt.restoration with
                                | none => accumulator.scopeRestorations
                                | some restoration => accumulator.scopeRestorations ++ [ restoration ]
                              scopeEvents := accumulator.scopeEvents ++ attempt.scopeEvents ++
                                postSuccessfulBlockScopeEvents spec index total
                              trace := accumulator.trace ++ blockRun.trace ++ [ Phase.commitTree ]
                              commitAttempts := accumulator.commitAttempts + 1
                              commitCompletions := accumulator.commitCompletions + 1
                              commitFailure := accumulator.commitFailure }
                          have hRejectedTail :
                                (processBlocks spec policy (index + 1) total rest
                                  nextAccumulator).outcome = .rejected failedIndex failure := by
                            simpa [processBlocks, blockInput, baseline, attempt, hAttempt, hOutcome,
                              hInclusion, hPrewarm, hCommit, finishBranch, committedBlock,
                              nextAccumulator] using hRejected
                          have hTail := inductionHypothesis (index + 1) total nextAccumulator
                            hRejectedTail
                          simpa [processBlocks, blockInput, baseline, attempt, hAttempt, hOutcome,
                            hInclusion, hPrewarm, hCommit, finishBranch, committedBlock,
                            nextAccumulator] using hTail

theorem processBlocks_exception_canonical_state
    (spec : BranchSpec) (policy : RetryPolicy) (index total : Nat)
    (inputs : List BlockInput) (accumulator : BranchAccumulator) (result : BranchRun)
    (hRun : processBlocks spec policy index total inputs accumulator = result)
    (hException : ∃ failedIndex stage, result.outcome = .exception failedIndex stage) :
    result.logicalState = accumulator.initialState := by
  subst result
  rcases hException with ⟨failedIndex, stage, hException⟩
  induction inputs generalizing index total accumulator with
  | nil =>
      simp [processBlocks, finishBranch] at hException
  | cons input rest inductionHypothesis =>
      let blockInput := { input with initialState := accumulator.currentState }
      let baseline := initialWorld blockInput
      let attempt := runAttempt spec policy index blockInput baseline
      cases hAttempt : attempt.run with
      | none =>
          have hImpossible :
              BranchOutcome.unmodeledParallel index =
                BranchOutcome.exception failedIndex stage := by
            simp [processBlocks, blockInput, baseline, attempt, hAttempt, finishBranch] at hException
          cases hImpossible
      | some blockRun =>
          have hAttempt' : (runAttempt spec policy index blockInput baseline).run =
              some blockRun := by
            simpa [attempt] using hAttempt
          cases hOutcome : blockRun.outcome with
          | rejected failure =>
              have hImpossible :
                  BranchOutcome.rejected index failure =
                    BranchOutcome.exception failedIndex stage := by
                simp [processBlocks, blockInput, baseline, hAttempt', hOutcome, finishBranch] at hException
              cases hImpossible
          | exception blockStage =>
              simp [processBlocks, blockInput, baseline, hAttempt', hOutcome, finishBranch]
          | accepted =>
              cases hInclusion : spec.checkInclusionList index blockInput blockRun.world with
              | escaped inclusionStage =>
                  simp [processBlocks, blockInput, baseline, hAttempt', hOutcome, hInclusion,
                    finishBranch]
              | completed inclusionSignal =>
                  cases hPrewarm : spec.prewarmSucceeded index blockInput blockRun.world with
                  | escaped prewarmStage =>
                      simp [processBlocks, blockInput, baseline, hAttempt', hOutcome,
                        hInclusion, hPrewarm, finishBranch]
                  | completed _ =>
                      cases hCommit : spec.commitTree index blockInput blockRun.world with
                      | escaped commitStage =>
                          simp [processBlocks, blockInput, baseline, hAttempt', hOutcome,
                            hInclusion, hPrewarm, hCommit, finishBranch]
                      | completed _ =>
                          let committedBlock : BlockRun :=
                            { blockRun with trace := blockRun.trace ++ [ Phase.commitTree ] }
                          let nextAccumulator : BranchAccumulator :=
                            { initialState := accumulator.initialState
                              currentState := blockRun.world.stateToken
                              successfulBlocks := accumulator.successfulBlocks ++ [ committedBlock ]
                              successfulInputs := accumulator.successfulInputs ++ [ input ]
                              committedStates :=
                                accumulator.committedStates ++ [ blockRun.world.stateToken ]
                              inclusionSignals :=
                                accumulator.inclusionSignals ++ [ (index, inclusionSignal) ]
                              attemptModes := accumulator.attemptModes ++ attempt.modes
                              parallelAttempts :=
                                match attempt.parallelAttempt with
                                | none => accumulator.parallelAttempts
                                | some observation => accumulator.parallelAttempts ++ [ observation ]
                              scopeRestorations :=
                                match attempt.restoration with
                                | none => accumulator.scopeRestorations
                                | some restoration => accumulator.scopeRestorations ++ [ restoration ]
                              scopeEvents := accumulator.scopeEvents ++ attempt.scopeEvents ++
                                postSuccessfulBlockScopeEvents spec index total
                              trace := accumulator.trace ++ blockRun.trace ++ [ Phase.commitTree ]
                              commitAttempts := accumulator.commitAttempts + 1
                              commitCompletions := accumulator.commitCompletions + 1
                              commitFailure := accumulator.commitFailure }
                          have hExceptionTail :
                                (processBlocks spec policy (index + 1) total rest
                                  nextAccumulator).outcome = .exception failedIndex stage := by
                            simpa [processBlocks, blockInput, baseline, attempt, hAttempt,
                              hAttempt', hOutcome, hInclusion, hPrewarm, hCommit, finishBranch,
                              committedBlock, nextAccumulator] using hException
                          have hTail := inductionHypothesis (index + 1) total nextAccumulator
                            hExceptionTail
                          simpa [processBlocks, blockInput, baseline, attempt, hAttempt, hOutcome,
                            hInclusion, hPrewarm, hCommit, finishBranch, committedBlock,
                            nextAccumulator] using hTail

theorem runBranch_selected_rejection_lifts_canonical
    (spec : BranchSpec) (policy : RetryPolicy) (state : Nat) (inputs : List BlockInput)
    (suggestedTrace senderTrace : List Phase) (processResult : BranchRun)
    (failedIndex : Nat) (failure : Failure)
    (hNonempty : inputs ≠ [])
    (hSuggested : suggestedBlockValidation inputs = .success suggestedTrace)
    (hSelected : spec.synchronousBranchSelection inputs = .selected)
    (hPreprocess : preprocessInputs inputs = .success senderTrace)
    (hNoInvalidPreOpened : invalidPreOpenedScope inputs = false)
    (hNoPreOpenedGenesis : scopePreOpenedForGenesis inputs = false)
    (hOpen : openScopeForInputs spec inputs state = .completed ())
    (hProcess : processBlocks spec policy 0 inputs.length inputs
        (initialAccumulator state
          (suggestedTrace ++ [ .synchronousBranchSelectionAndPreparation ] ++ senderTrace ++
            [ .openWorldStateScope ]) [ .opened ]) = processResult)
    (hOutcome : processResult.outcome = .rejected failedIndex failure) :
    let result := runBranch spec policy state inputs
    result.outcome = .rejected failedIndex failure ∧
      result.logicalState = state ∧
      result.committedPrefixState = processResult.committedPrefixState ∧
      result.successfulBlocks = processResult.successfulBlocks ∧
      result.successfulInputs = processResult.successfulInputs ∧
      result.commitAttempts = processResult.commitAttempts ∧
      result.commitCompletions = processResult.commitCompletions ∧
      result.trace = processResult.trace ++
        [ .synchronousResultClassificationAndHeadFinalization ] := by
  have hCanonical := processBlocks_rejected_canonical_state spec policy 0 inputs.length inputs
    (initialAccumulator state
      (suggestedTrace ++ [ .synchronousBranchSelectionAndPreparation ] ++ senderTrace ++
        [ .openWorldStateScope ]) [ .opened ]) processResult hProcess
    ⟨failedIndex, failure, hOutcome⟩
  have hCanonical' : processResult.logicalState = state := by
    simpa [initialAccumulator] using hCanonical
  exact runBranch_selected_rejection_lifts spec policy state inputs suggestedTrace senderTrace
    processResult failedIndex failure hNonempty hSuggested hSelected hPreprocess
    hNoInvalidPreOpened hNoPreOpenedGenesis hOpen hProcess hOutcome hCanonical'

theorem runBranch_selected_exception_lifts_canonical
    (spec : BranchSpec) (policy : RetryPolicy) (state : Nat) (inputs : List BlockInput)
    (suggestedTrace senderTrace : List Phase) (processResult : BranchRun)
    (failedIndex : Nat) (stage : ExceptionStage)
    (hNonempty : inputs ≠ [])
    (hSuggested : suggestedBlockValidation inputs = .success suggestedTrace)
    (hSelected : spec.synchronousBranchSelection inputs = .selected)
    (hPreprocess : preprocessInputs inputs = .success senderTrace)
    (hNoInvalidPreOpened : invalidPreOpenedScope inputs = false)
    (hNoPreOpenedGenesis : scopePreOpenedForGenesis inputs = false)
    (hOpen : openScopeForInputs spec inputs state = .completed ())
    (hProcess : processBlocks spec policy 0 inputs.length inputs
        (initialAccumulator state
          (suggestedTrace ++ [ .synchronousBranchSelectionAndPreparation ] ++ senderTrace ++
            [ .openWorldStateScope ]) [ .opened ]) = processResult)
    (hOutcome : processResult.outcome = .exception failedIndex stage) :
    let result := runBranch spec policy state inputs
    result.outcome = .exception failedIndex stage ∧
      result.logicalState = state ∧
      result.committedPrefixState = processResult.committedPrefixState ∧
      result.successfulBlocks = processResult.successfulBlocks ∧
      result.successfulInputs = processResult.successfulInputs ∧
      result.commitAttempts = processResult.commitAttempts ∧
      result.commitCompletions = processResult.commitCompletions ∧
      result.trace = processResult.trace := by
  have hCanonical := processBlocks_exception_canonical_state spec policy 0 inputs.length inputs
    (initialAccumulator state
      (suggestedTrace ++ [ .synchronousBranchSelectionAndPreparation ] ++ senderTrace ++
        [ .openWorldStateScope ]) [ .opened ]) processResult hProcess
    ⟨failedIndex, stage, hOutcome⟩
  have hCanonical' : processResult.logicalState = state := by
    simpa [initialAccumulator] using hCanonical
  exact runBranch_selected_exception_lifts spec policy state inputs suggestedTrace senderTrace
    processResult failedIndex stage hNonempty hSuggested hSelected hPreprocess
    hNoInvalidPreOpened hNoPreOpenedGenesis hOpen hProcess hOutcome hCanonical'

theorem finishBranch_committed_prefix_is_last
    (outcome : BranchOutcome) (failedBlock : Option BlockRun)
    (accumulator : BranchAccumulator) :
    (finishBranch outcome failedBlock accumulator).committedPrefixState =
      (finishBranch outcome failedBlock accumulator).committedStates.getLast?.getD
        accumulator.initialState := by
  rfl

theorem retry_restoration_is_explicit (spec : BranchSpec) (policy : RetryPolicy)
    (index : Nat) (input : BlockInput) (baseline : BlockWorld)
    (hEligible : input.parallelEligibility.isEligible = true)
    (hRetry : policy.parallelOutcome index = .retryableBalFailure) :
    (runAttempt spec policy index input baseline).restoration =
      some { index := index, before := policy.parallelFailedState index baseline, after := baseline } := by
  simp [runAttempt, hEligible, hRetry]

end BranchReference
end BlockReference
end Eip803x
