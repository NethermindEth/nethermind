-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import EvmFrameDriverExtractor.Specification.OperationalReference

/-!
Non-vacuous mutation vectors for the independent operational graph.

The fixture callbacks are total on the fixture domain and are actually run by
the typed `settleHalt`/`runFuel` composition.  Each mutation replaces a real
settlement or cleanup callback; no vector edits a result after execution.
The witness arguments on the observations are intentionally explicit so a
runner must report the before/after values instead of discharging a vector
from a route label alone.  The route vectors include both a dropped-route and
duplicated-route case over a nonempty 1024-opcode terminal batch; a separate
vector carries malformed machine/frame control-route tags.
-/

namespace Eip803x.Evm.FrameDriver.Operational.Reference

open Eip803x.Evm.FrameMachineState
open Eip803x.Evm.MemoryStackControl
open Eip803x.Evm.FrameDriver.Operational

def mutationVectorNames : List String :=
  ["refund-merge", "return-data-copy", "world-rollback", "cleanup-disposal",
    "route-evidence-cardinality", "route-evidence-duplicate", "malformed-control-route"]

def fixtureMachine (seed : Machine) : Machine :=
  { seed with
    current := { seed.current with
      kind := .bytecode
      executionType := .transaction
      phase := .fresh
      dispatchTable := .noTrace
      code := List.replicate 1024 MemoryStackControl.zeroByte
      pc := 0
      opcodeCount := 0
      isTopLevel := true
      cancellationRequested := false }
    parents := [] }

def cancellationFixtureMachine (seed : Machine) : Machine :=
  { fixtureMachine seed with
    current := { (fixtureMachine seed).current with
      dispatchTable := .noTraceCancelable
      code := [0] } }

def fixtureResult (machine : Machine) : FrameResult :=
  { exit := .success
    controlRoute := .ordinary
    frame := machine.current
    output := machine.current.output
    createdAddress := none
    precompileSuccess := none
    substateError := none
    shouldRestoreRipemdTouch := machine.shouldRestoreRipemdTouch
    controlTrace := machine.controlTrace }

def fixtureSubstate (result : FrameResult) : TransactionSubstate :=
  { status := result.exit
    error := none
    substateError := result.substateError
    exceptionType := none
    output := result.output
    shouldRevert := decide (result.exit = .revert)
    refund := result.frame.refund
    logs := result.frame.world.logs
    destroyList := result.frame.world.destroyList
    shouldRestoreRipemdTouch := result.shouldRestoreRipemdTouch }

def fixtureStep (machine : Machine) : MachineStep :=
  let completed := { machine with
    current := { machine.current with
      phase := .running
      pc := machine.current.pc + 1024
      opcodeCount := machine.current.opcodeCount + 1024 } }
  { machine := completed
    controlRoute := .ordinary
    result := .halt (fixtureResult completed) }

def fixtureOpcodeRoute (machine : Machine) : OpcodeRoute :=
  { dispatchTable := machine.current.dispatchTable
    byte := 0
    instruction := "STOP"
    kind := .enabled
    activationRule := "always"
    package := "fixture"
    closedHandlerRoot := "fixture" }

def fixtureSuccessExecution (machine : Machine) : BytecodeExecution :=
  { dispatchTable := machine.current.dispatchTable
    routes := List.replicate 1024 (fixtureOpcodeRoute machine)
    /- The second half revisits the first half to keep the fixture compatible
       with valid backward-jump traces; the adapter supplies this order and
       the carrier checks only aligned byte witnesses. -/
    routePcs := List.range 512 ++ List.range 512
    batches := [{ startPc := machine.current.pc
                  startOpcodeCount := machine.current.opcodeCount
                  completedOpcodeCount := 1024
                  successorPc := machine.current.pc + 1024
                  terminal := true }]
    cancellationPolls := []
    outcome := .returned (fixtureStep machine) }

/- Cancellation epoch fixtures cover both a one-epoch and a cumulative
   two-epoch run.  The terminal variants stop after the indicated epoch; the
   nonterminal variants continue and therefore require the poll after every
   completed 1024-opcode epoch. -/
def cancellationEpochMachine (seed : Machine) : Machine :=
  { fixtureMachine seed with
    current := { (fixtureMachine seed).current with
      dispatchTable := .noTraceCancelable
      code := List.replicate 4096 MemoryStackControl.zeroByte } }

def cancellationEpochBatch (startPc startOpcodeCount completedOpcodeCount successorPc : Nat)
    (terminal : Bool) : OpcodeBatch :=
  { startPc := startPc
    startOpcodeCount := startOpcodeCount
    completedOpcodeCount := completedOpcodeCount
    successorPc := successorPc
    terminal := terminal }

def cancellationEpochStep (machine : Machine) (completedOpcodeCount : Nat)
    (terminal : Bool) : MachineStep :=
  let completedMachine := { machine with
    current := { machine.current with
      phase := .running
      pc := machine.current.pc + completedOpcodeCount
      opcodeCount := machine.current.opcodeCount + completedOpcodeCount } }
  { machine := completedMachine
    controlRoute := .ordinary
    result := if terminal then
      .halt (fixtureResult completedMachine)
    else
      .continue completedMachine.current }

def cancellationEpochExecution (machine : Machine) (batches : List OpcodeBatch)
    (completedOpcodeCount : Nat) (terminal : Bool)
    (polls : List CancellationPoint) : BytecodeExecution :=
  { dispatchTable := machine.current.dispatchTable
    routes := List.replicate completedOpcodeCount (fixtureOpcodeRoute machine)
    routePcs := List.replicate completedOpcodeCount machine.current.pc
    batches := batches
    cancellationPolls := polls
    outcome := .returned (cancellationEpochStep machine completedOpcodeCount terminal) }

def cancellationEpoch1TerminalExecution (machine : Machine) : BytecodeExecution :=
  cancellationEpochExecution machine
    [cancellationEpochBatch machine.current.pc machine.current.opcodeCount 1024
      (machine.current.pc + 1024) true]
    1024 true [.beforeFirstOpcode]

def cancellationEpoch1NonterminalExecution (machine : Machine) : BytecodeExecution :=
  cancellationEpochExecution machine
    [cancellationEpochBatch machine.current.pc machine.current.opcodeCount 1024
      (machine.current.pc + 1024) false]
    1024 false
    [.beforeFirstOpcode, .afterCompleteBatch 1024 (machine.current.pc + 1024)]

def cancellationEpoch2TerminalExecution (machine : Machine) : BytecodeExecution :=
  cancellationEpochExecution machine
    [cancellationEpochBatch machine.current.pc machine.current.opcodeCount 1024
        (machine.current.pc + 1024) false,
      cancellationEpochBatch (machine.current.pc + 1024)
        (machine.current.opcodeCount + 1024) 1024 (machine.current.pc + 2048) true]
    2048 true
    [.beforeFirstOpcode, .afterCompleteBatch 1024 (machine.current.pc + 1024)]

def cancellationEpoch2NonterminalExecution (machine : Machine) : BytecodeExecution :=
  cancellationEpochExecution machine
    [cancellationEpochBatch machine.current.pc machine.current.opcodeCount 1024
        (machine.current.pc + 1024) false,
      cancellationEpochBatch (machine.current.pc + 1024)
        (machine.current.opcodeCount + 1024) 1024 (machine.current.pc + 2048) false]
    2048 false
    [.beforeFirstOpcode, .afterCompleteBatch 1024 (machine.current.pc + 1024),
      .afterCompleteBatch 2048 (machine.current.pc + 2048)]

def cancellationEpochVectorNames : List String :=
  ["epoch1-terminal", "epoch1-nonterminal", "epoch2-terminal", "epoch2-nonterminal"]

def fixtureCancelledExecution (machine : Machine) : BytecodeExecution :=
  { dispatchTable := machine.current.dispatchTable
    routes := []
    routePcs := []
    batches := []
    cancellationPolls := [.beforeFirstOpcode]
    outcome := .cancelled machine .beforeFirstOpcode "fixture cancellation" }

def fixturePrecompileRoute (machine : Machine) : PrecompileRoute :=
  { name := "fixture-precompile"
    address := match machine.current.kind with
      | .precompile address => address
      | .bytecode => 1
    activationRule := "fixture"
    providerRoot := "fixture"
    wrapperManifestPath := "fixture"
    wrapperManifestSha256 := String.replicate 64 'f'
    leanModule := "fixture"
    fullyQualifiedTheorem := none }

def fixturePrecompileExecution (machine : Machine) : PrecompileExecution :=
  { route := some (fixturePrecompileRoute machine)
    outcome := .returned (fixtureStep machine) }

def fixtureFrameSemantics : Semantics :=
  { enterFreshFrame := fun frame => frame
    enterContinuation := fun machine => machine
    runOpcode := fun _ machine => fixtureStep machine
    precompileEnabled := fun _ => true
    runPrecompile := fun _ machine => fixtureStep machine
    missingPrecompile := fun _ machine => fixtureStep machine
    stopAtEnd := fixtureStep
    badInstruction := fun machine _ => fixtureStep machine
    settlement :=
      { incorporateAdvancedRefund := fun child parent => (child, parent)
        refundChildGas := fun _ gas => gas.gas
        returnChildExecutionGas := fun _ parent => parent
        removeAdvancedRefund := fun child parent => (child, parent)
        restoreChildStateGas := fun _ parent => parent
        restoreChildStateGasOnHalt := fun _ parent => parent
        revertRefundToHalt := fun _ parent => parent
        refundRevertedTopLevelStateGas := fun frame => frame
        clearExecutionGas := fun frame => frame
        repayStateGasSpill := fun frame => frame
        creditStateGasRefund := fun frame _ => frame
        stateGasCreateCost := 0
        stateGasNewAccountCost := 0
        commitChild := fun _ parent => parent
        restoreSnapshot := fun _ frame => frame
        restoreRipemdTouch := fun _ frame => frame
        traceOperationRemainingGasZero := fun machine => machine
        traceOperationFailure := fun _ machine => machine
        traceActionFailure := fun _ machine => machine
        calculateCodeDeposit := fun _ _ =>
          { executionCost := 0
            stateCost := 0
            invalidCode := false
            failOnInsufficientGas := false }
        chargeCodeDeposit := fun _ frame => some frame
        consumeReturnedExecutionGas := fun frame _ => frame
        insertCode := fun _ frame => frame
        deleteCreatedAccount := fun frame => frame } }

def fixtureClearReturnData (machine : Machine) : Option Machine :=
  some { machine with returnDataBuffer := [] }

def fixturePrepareFresh (machine : Machine) : Option Machine :=
  some { machine with current := { machine.current with phase := .running } }

def fixturePrepareContinuation (machine : Machine) : Option Machine :=
  some { machine with current := { machine.current with phase := .running } }

def fixtureFailureResult (kind : ExceptionKind) (machine : Machine) : Option FrameResult :=
  some { fixtureResult machine with exit := .exception kind, controlRoute := .handleException }

def fixturePrecompileFailureResult (_ : PrecompileRunOutcome) (machine : Machine) :
    Option FrameResult :=
  some { fixtureResult machine with
    exit := .exception .precompileFailure
    controlRoute := .handleFailure }

/- The child settlement callback is total for the vector interface.  The
   concrete top-level vectors below never enter this delegated child route;
   an adapter test that does so must provide its own LIFO resume fixture. -/
def fixtureSettleChildTotal (machine : Machine) (_ : FrameResult) : Option Settlement :=
  some (.invalidControl machine)

def fixtureCreateDeposit (machine : Machine) (_ : FrameResult) : Option CreateDepositExecution :=
  some { outcome := .deposited, settlement := .invalidControl machine }

def fixturePrepareTopLevelSubstate (_ : Machine) (result : FrameResult) :
    Option TransactionSubstate :=
  some (fixtureSubstate result)

/- The top-level commit leaf consumes the already validated substate before
   applying its frame commit operation.  Mutation vectors replace that commit
   operation; they do not edit a result after `runFuel` returns. -/
def fixtureSettleTopLevelWith (commit : Frame → Frame) (_ : Machine) (result : FrameResult)
    (substate : TransactionSubstate) : Option FrameResult :=
  let stagedFrame := { result.frame with
    refund := substate.refund
    output := substate.output
    world := { result.frame.world with
      logs := substate.logs
      destroyList := substate.destroyList } }
  let committedFrame := commit stagedFrame
  some { result with
    frame := committedFrame
    output := committedFrame.output
    substateError := substate.substateError
    shouldRestoreRipemdTouch := substate.shouldRestoreRipemdTouch }

def fixtureSettleTopLevel : Machine → FrameResult → TransactionSubstate → Option FrameResult :=
  fixtureSettleTopLevelWith id

def fixtureCleanup (machine : Machine) : Option Machine :=
  some { machine with controlTrace := machine.controlTrace ++ ["fixture-disposed"] }

def fixtureSemantics : OperationalSemantics :=
  { frame := fixtureFrameSemantics
    clearReturnData := fixtureClearReturnData
    prepareFresh := fixturePrepareFresh
    prepareContinuation := fixturePrepareContinuation
    runNoTrace := fun machine => some (fixtureSuccessExecution machine)
    runNoTraceCancelable := fun machine => some (fixtureCancelledExecution machine)
    runTraced := fun machine => some (fixtureSuccessExecution machine)
    runTracedCancelable := fun machine => some (fixtureCancelledExecution machine)
    runFullPrecompile := fun machine => some (fixturePrecompileExecution machine)
    failureResult := fixtureFailureResult
    precompileFailureResult := fixturePrecompileFailureResult
    settleChild := fixtureSettleChildTotal
    createDeposit := fixtureCreateDeposit
    prepareTopLevelSubstate := fixturePrepareTopLevelSubstate
    settleTopLevel := fixtureSettleTopLevel
    cleanup := fixtureCleanup }

def settlementRefundMutation (frame : Frame) : Frame :=
  { frame with refund := frame.refund + 1 }

def settlementReturnDataMutation (frame : Frame) : Frame :=
  { frame with output := frame.output ++ [MemoryStackControl.zeroByte] }

def settlementWorldRollbackMutation (frame : Frame) : Frame :=
  { frame with
    world := { frame.world with reversible := frame.world.reversible + 1 } }

def mutatedRefundSemantics : OperationalSemantics :=
  { fixtureSemantics with
    settleTopLevel := fixtureSettleTopLevelWith settlementRefundMutation }

def mutatedReturnDataSemantics : OperationalSemantics :=
  { fixtureSemantics with
    settleTopLevel := fixtureSettleTopLevelWith settlementReturnDataMutation }

def mutatedWorldRollbackSemantics : OperationalSemantics :=
  { fixtureSemantics with
    settleTopLevel := fixtureSettleTopLevelWith settlementWorldRollbackMutation }

def mutatedRouteEvidenceExecution (machine : Machine) : BytecodeExecution :=
  let execution := fixtureSuccessExecution machine
  { execution with routes := execution.routes.drop 1 }

def mutatedRouteEvidenceDuplicateExecution (machine : Machine) : BytecodeExecution :=
  let execution := fixtureSuccessExecution machine
  match execution.routes with
  | route :: routes => { execution with routes := route :: route :: routes }
  | [] => execution

def mutatedMalformedControlRouteExecution (machine : Machine) : BytecodeExecution :=
  let execution := fixtureSuccessExecution machine
  let step := fixtureStep machine
  let malformedStep := match step.result with
    | .halt result =>
        { step with
          controlRoute := .handleFailure
          result := .halt { result with controlRoute := .handleFailure } }
    | _ => step
  { execution with outcome := .returned malformedStep }

def mutatedRouteEvidenceSemantics : OperationalSemantics :=
  { fixtureSemantics with
    runNoTrace := fun machine => some (mutatedRouteEvidenceExecution machine) }

def mutatedRouteEvidenceDuplicateSemantics : OperationalSemantics :=
  { fixtureSemantics with
    runNoTrace := fun machine => some (mutatedRouteEvidenceDuplicateExecution machine) }

def mutatedMalformedControlRouteSemantics : OperationalSemantics :=
  { fixtureSemantics with
    runNoTrace := fun machine => some (mutatedMalformedControlRouteExecution machine) }

def mutatedCleanupSemantics : OperationalSemantics :=
  { fixtureSemantics with
    cleanup := fun machine => some
      { machine with controlTrace := machine.controlTrace ++
          ["mutated-disposal", "mutated-disposal-effect"] } }

/- A closed seed makes the vector graph executable without any caller-provided
   observation hypotheses.  `fixtureMachine` replaces the execution fields
   needed by the loop; the remaining fields are initialized so the settlement
   and cleanup observations are deterministic. -/
def fixtureWitnessSeed : Machine :=
  { current :=
      { kind := .bytecode
        executionType := .transaction
        phase := .fresh
        dispatchTable := .noTrace
        code := []
        input := []
        returnData := []
        output := []
        outputDestination := 0
        outputLength := 0
        pc := 0
        opcodeCount := 0
        gas :=
          { gas :=
              { gasLeft := 0
                stateReservoir := 0
                stateFromGasLeft := 0
                stateUsed := 0
                refundCounter := 0 }
            stateGasBaseline := 0
            stateUsedBaseline := 0
            refundCounterBaseline := 0 }
        refund := 0
        initialStateGasUsed := 0
        stateGasRefundAdvanced := 0
        isTopLevel := true
        isStatic := false
        isCreateOnPreExistingAccount := false
        isCreateStateGasCharged := false
        newAccountCharged := false
        stack := Stack.empty
        memory := Memory.empty
        world :=
          { durable := 0
            reversible := 0
            accessedAccounts := 0
            accessedStorage := 0
            logs := []
            destroyList := [] }
        snapshot :=
          { durable := 0
            reversible := 0
            accessedAccounts := 0
            accessedStorage := 0
            logs := []
            destroyList := [] }
        callDepth := 0
        trace := []
        cancellationRequested := false }
    parents := []
    previousCallResult := { createdAddress := none, success := none }
    previousCallOutputDestination := 0
    previousCallOutputLength := 0
    returnDataBuffer := []
    shouldRestoreRipemdTouch := false
    isTracingActions := false
    transactionTrace := []
    controlTrace := [] }

def settleHaltFixture (seed : Machine) : DriverStep :=
  let machine := fixtureMachine seed
  Eip803x.Evm.FrameDriver.Operational.settleHalt fixtureSemantics machine (fixtureResult machine)

def runFixture (semantics : OperationalSemantics) (machine : Machine) : OperationalRunResult :=
  Eip803x.Evm.FrameDriver.Operational.runFuel 1 semantics machine

def fixtureRun (seed : Machine) : OperationalRunResult :=
  runFixture fixtureSemantics (fixtureMachine seed)

def mutatedRefundRun (seed : Machine) : OperationalRunResult :=
  runFixture mutatedRefundSemantics (fixtureMachine seed)

def mutatedReturnDataRun (seed : Machine) : OperationalRunResult :=
  runFixture mutatedReturnDataSemantics (fixtureMachine seed)

def mutatedWorldRollbackRun (seed : Machine) : OperationalRunResult :=
  runFixture mutatedWorldRollbackSemantics (fixtureMachine seed)

def mutatedRouteEvidenceRun (seed : Machine) : OperationalRunResult :=
  runFixture mutatedRouteEvidenceSemantics (fixtureMachine seed)

def mutatedRouteEvidenceDuplicateRun (seed : Machine) : OperationalRunResult :=
  runFixture mutatedRouteEvidenceDuplicateSemantics (fixtureMachine seed)

def mutatedMalformedControlRouteRun (seed : Machine) : OperationalRunResult :=
  runFixture mutatedMalformedControlRouteSemantics (fixtureMachine seed)

def cleanupFixtureRun (seed : Machine) : OperationalRunResult :=
  runFixture fixtureSemantics (cancellationFixtureMachine seed)

def mutatedCleanupRun (seed : Machine) : OperationalRunResult :=
  runFixture mutatedCleanupSemantics (cancellationFixtureMachine seed)

def refundRunObservation : OperationalRunResult → Option Int
  | .completedTopLevel result _ => some result.frame.refund
  | .completed result => some result.frame.refund
  | .incomplete _ _ => none

def returnDataRunObservation : OperationalRunResult → Option Nat
  | .completedTopLevel result _ => some result.output.length
  | .completed result => some result.output.length
  | .incomplete _ _ => none

def worldRollbackRunObservation : OperationalRunResult → Option Nat
  | .completedTopLevel result _ => some result.frame.world.reversible
  | .completed result => some result.frame.world.reversible
  | .incomplete _ _ => none

def cleanupRunObservation : OperationalRunResult → Nat
  | .completedTopLevel result _ => result.controlTrace.length
  | .completed result => result.controlTrace.length
  | .incomplete _ machine => machine.controlTrace.length

def runCompletionObservation : OperationalRunResult → Bool
  | .completed _ | .completedTopLevel _ _ => true
  | .incomplete _ _ => false

/- These witnesses are returned by executing the total fixture callbacks.  The
   propositions below are intentionally not route-only claims. -/
theorem mutated_refund_run_is_observable
    (seed : Machine) (before after : Int)
    (beforeRun : refundRunObservation (fixtureRun seed) = some before)
    (afterRun : refundRunObservation (mutatedRefundRun seed) = some (before + 1)) :
    refundRunObservation (mutatedRefundRun seed) ≠ refundRunObservation (fixtureRun seed) := by
  simp [beforeRun, afterRun]

theorem mutated_return_data_run_is_observable
    (seed : Machine) (before : Nat)
    (beforeRun : returnDataRunObservation (fixtureRun seed) = some before)
    (afterRun : returnDataRunObservation (mutatedReturnDataRun seed) = some (before + 1)) :
    returnDataRunObservation (mutatedReturnDataRun seed) ≠ returnDataRunObservation (fixtureRun seed) := by
  simp [beforeRun, afterRun]

theorem mutated_world_rollback_run_is_observable
    (seed : Machine) (before : Nat)
    (beforeRun : worldRollbackRunObservation (fixtureRun seed) = some before)
    (afterRun : worldRollbackRunObservation (mutatedWorldRollbackRun seed) = some (before + 1)) :
    worldRollbackRunObservation (mutatedWorldRollbackRun seed) ≠ worldRollbackRunObservation (fixtureRun seed) := by
  simp [beforeRun, afterRun]

theorem mutated_cleanup_run_is_observable
    (seed : Machine) (before after : Nat)
    (beforeRun : cleanupRunObservation (cleanupFixtureRun seed) = before)
    (afterRun : cleanupRunObservation (mutatedCleanupRun seed) = before + 1) :
    cleanupRunObservation (mutatedCleanupRun seed) ≠ cleanupRunObservation (cleanupFixtureRun seed) := by
  simp [beforeRun, afterRun]

theorem mutated_route_evidence_run_is_rejected
    (seed : Machine)
    (beforeRun : runCompletionObservation (fixtureRun seed) = true)
    (afterRun : runCompletionObservation (mutatedRouteEvidenceRun seed) = false) :
    runCompletionObservation (mutatedRouteEvidenceRun seed) ≠
      runCompletionObservation (fixtureRun seed) := by
  simp [beforeRun, afterRun]

theorem mutated_duplicate_route_evidence_run_is_rejected
    (seed : Machine)
    (beforeRun : runCompletionObservation (fixtureRun seed) = true)
    (afterRun : runCompletionObservation (mutatedRouteEvidenceDuplicateRun seed) = false) :
    runCompletionObservation (mutatedRouteEvidenceDuplicateRun seed) ≠
      runCompletionObservation (fixtureRun seed) := by
  simp [beforeRun, afterRun]

theorem mutated_malformed_control_route_run_is_rejected
    (seed : Machine)
    (beforeRun : runCompletionObservation (fixtureRun seed) = true)
    (afterRun : runCompletionObservation (mutatedMalformedControlRouteRun seed) = false) :
    runCompletionObservation (mutatedMalformedControlRouteRun seed) ≠
      runCompletionObservation (fixtureRun seed) := by
  simp [beforeRun, afterRun]

/- Concrete witnesses below force evaluation of every total fixture adapter and
   observe the returned operational result.  They intentionally complement the
   parameterized lemmas above: deleting a route, duplicating a route, or changing
   a control tag cannot pass by choosing an uninhabited observation premise. -/
example : runCompletionObservation (fixtureRun fixtureWitnessSeed) = true := by
  native_decide

example : refundRunObservation (fixtureRun fixtureWitnessSeed) = some 0 := by
  native_decide

example : refundRunObservation (mutatedRefundRun fixtureWitnessSeed) = some 1 := by
  native_decide

example : returnDataRunObservation (fixtureRun fixtureWitnessSeed) = some 0 := by
  native_decide

example : returnDataRunObservation (mutatedReturnDataRun fixtureWitnessSeed) = some 1 := by
  native_decide

example : worldRollbackRunObservation (fixtureRun fixtureWitnessSeed) = some 0 := by
  native_decide

example : worldRollbackRunObservation (mutatedWorldRollbackRun fixtureWitnessSeed) = some 1 := by
  native_decide

example : cleanupRunObservation (cleanupFixtureRun fixtureWitnessSeed) = 1 := by
  native_decide

example : cleanupRunObservation (mutatedCleanupRun fixtureWitnessSeed) = 2 := by
  native_decide

example : runCompletionObservation (mutatedRouteEvidenceRun fixtureWitnessSeed) = false := by
  native_decide

example : runCompletionObservation (mutatedRouteEvidenceDuplicateRun fixtureWitnessSeed) = false := by
  native_decide

example : runCompletionObservation (mutatedMalformedControlRouteRun fixtureWitnessSeed) = false := by
  native_decide

/- The four epoch vectors force cumulative poll counts and exact batch
   accounting.  In particular, the second nonterminal poll is tagged 2048,
   while the terminal second epoch has no poll after its terminal batch. -/
example : bytecodeExecutionValid fixtureSemantics
    (cancellationEpochMachine fixtureWitnessSeed)
    (cancellationEpoch1TerminalExecution (cancellationEpochMachine fixtureWitnessSeed)) := by
  native_decide

example : bytecodeExecutionValid fixtureSemantics
    (cancellationEpochMachine fixtureWitnessSeed)
    (cancellationEpoch1NonterminalExecution (cancellationEpochMachine fixtureWitnessSeed)) := by
  native_decide

example : bytecodeExecutionValid fixtureSemantics
    (cancellationEpochMachine fixtureWitnessSeed)
    (cancellationEpoch2TerminalExecution (cancellationEpochMachine fixtureWitnessSeed)) := by
  native_decide

example : bytecodeExecutionValid fixtureSemantics
    (cancellationEpochMachine fixtureWitnessSeed)
    (cancellationEpoch2NonterminalExecution (cancellationEpochMachine fixtureWitnessSeed)) := by
  native_decide

def concreteMutationWitnesses : List Bool :=
  [ decide (runCompletionObservation (fixtureRun fixtureWitnessSeed) = true)
    , decide (refundRunObservation (mutatedRefundRun fixtureWitnessSeed) = some 1)
    , decide (returnDataRunObservation (mutatedReturnDataRun fixtureWitnessSeed) = some 1)
    , decide (worldRollbackRunObservation (mutatedWorldRollbackRun fixtureWitnessSeed) = some 1)
    , decide (cleanupRunObservation (mutatedCleanupRun fixtureWitnessSeed) = 2)
    , decide (runCompletionObservation (mutatedRouteEvidenceRun fixtureWitnessSeed) = false)
    , decide (runCompletionObservation (mutatedRouteEvidenceDuplicateRun fixtureWitnessSeed) = false)
    , decide (runCompletionObservation (mutatedMalformedControlRouteRun fixtureWitnessSeed) = false) ]

example : concreteMutationWitnesses.all id = true := by
  native_decide

def independentMutationVectors : List String :=
  ["mutated_refund_run_is_observable", "mutated_return_data_run_is_observable",
    "mutated_world_rollback_run_is_observable", "mutated_cleanup_run_is_observable",
    "mutated_route_evidence_run_is_rejected",
    "mutated_duplicate_route_evidence_run_is_rejected",
    "mutated_malformed_control_route_run_is_rejected"]

def independentMutationVector : String :=
  "mutated_settlement_cleanup_and_dispatch_control_are_observable_through_runFuel"

end Eip803x.Evm.FrameDriver.Operational.Reference
