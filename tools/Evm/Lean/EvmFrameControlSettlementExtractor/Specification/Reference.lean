-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import EvmFrameControlSettlementExtractor.Specification.Types

/-!
Decision-oriented handwritten reference for one admitted `ExecuteTransaction`
loop iteration. It deliberately uses entry, dispatch, settlement-plan, and
closing-plan data types instead of the generated driver's source-order helper
layout. Both transcriptions consume one `CanonicalLeaves` record: their theorem
is a control-agreement check, while a separate production simulation interface
remains an open obligation.

It does not model the next loop iteration, transaction-processor deployment,
refund/finalization, or external rollback.
-/

namespace EvmFrameControlSettlementExtractor.Reference

open Eip803x.Evm.FrameMachineState
open EvmFrameControlSettlementExtractor

structure Decision where
  frame : Machine
  invocation : InvocationResult
  inlineStaticPrecompile : Option DirectInlineStaticPrecompileOutcome
  deriving Repr

def Decision.asDriverInvocation (decision : Decision) : DriverInvocation :=
  { invocation := decision.invocation
    directInlineStaticPrecompile := decision.inlineStaticPrecompile }

inductive EntryPlan where
  | initializeFresh
  | resumeContinuation
  | unsupportedCarrierState

def planEntry (machine : Machine) : EntryPlan :=
  match machine.current.phase with
  | .fresh => .initializeFresh
  | .continuation => .resumeContinuation
  | .running => .unsupportedCarrierState

def enactEntry (leaves : CanonicalLeaves) (plan : EntryPlan) (machine : Machine) : Machine :=
  match plan with
  | .initializeFresh =>
      leaves.preparation.prepareFresh (leaves.preparation.clearReturnData machine)
  | .resumeContinuation => leaves.preparation.prepareContinuation machine
  | .unsupportedCarrierState => machine

def enterFrame (leaves : CanonicalLeaves) (machine : Machine) : Machine :=
  enactEntry leaves (planEntry machine) machine

inductive DispatchPlan where
  | executeBytecode
  | executeCurrentPrecompile

def planDispatch (machine : Machine) : DispatchPlan :=
  match machine.current.kind with
  | .bytecode => .executeBytecode
  | .precompile _ => .executeCurrentPrecompile

def chooseInvocation (leaves : CanonicalLeaves) (machine : Machine) : Decision :=
  match planDispatch machine with
  | .executeBytecode =>
      let outcome := leaves.dispatch.executeBytecodeFrame machine
      { frame := machine
        invocation := outcome.invocation
        inlineStaticPrecompile := outcome.directInlineStaticPrecompile }
  | .executeCurrentPrecompile =>
      { frame := machine
        invocation := leaves.dispatch.executeFullPrecompileFrame machine
        inlineStaticPrecompile := none }

theorem chooseInvocation_preserves_frame (leaves : CanonicalLeaves) (machine : Machine) :
    (chooseInvocation leaves machine).frame = machine := by
  unfold chooseInvocation planDispatch
  split <;> rfl

def topOrNested (machine : Machine) (topRoute nestedRoute : SettlementRoute) : SettlementRoute :=
  if machine.current.isTopLevel then topRoute else nestedRoute

def routeForFrameResult (machine : Machine) (result : FrameResult) : SettlementRoute :=
  match result.exit with
  | .success =>
      if machine.current.isTopLevel then .topLevelSuccess
      else if machine.current.executionType.isCreate then .createSuccessNested
      else .regularSuccessNested
  | .revert => topOrNested machine .revertTop .revertNested
  | .exception _ => topOrNested machine .exceptionTop .exceptionNested

def routeForDecision (decision : Decision) : SettlementRoute :=
  match decision.invocation with
  | .returned step
  | .fullPrecompileSuccess step =>
      match step.result with
      | .continue _ => .continued
      | .suspend _ _ => .suspend
      | .halt result => routeForFrameResult decision.frame result
  | .fullPrecompileOutOfGas =>
      topOrNested decision.frame .fullPrecompileOutOfGasTop .fullPrecompileOutOfGasNested
  | .fullPrecompileReturnedFailure _ =>
      topOrNested decision.frame .fullPrecompileReturnedFailureTop .fullPrecompileReturnedFailureNested
  | .fullPrecompileManagedException _ =>
      topOrNested decision.frame .fullPrecompileManagedExceptionTop .fullPrecompileManagedExceptionNested
  | .thrownEvm _ | .thrownOverflow => topOrNested decision.frame .exceptionTop .exceptionNested
  | .escapedInvocation _ => .escaped
  | .cancelled _ _ => .cancelled

def withRoute (route : SettlementRoute) (outcome : ShellResult) : ShellResult :=
  { outcome with route := route }

/- The reference first chooses an action from a route, then interprets that
action. This is intentionally different from the generated driver's direct
route-to-leaf switch, so a branch or ordering drift has to satisfy an
independent control equation. -/
inductive SettlementAction where
  | continueStep
  | suspendStep
  | regularSuccessNested
  | topLevelSuccess
  | nestedCreateDeposit
  | codeDepositInvalidNested
  | codeDepositOutOfGasNested
  | revertNested
  | revertTop
  | exceptionTop
  | exceptionNested
  | fullPrecompileOutOfGasNested
  | fullPrecompileOutOfGasTop
  | fullPrecompileReturnedFailureNested
  | fullPrecompileReturnedFailureTop
  | fullPrecompileManagedExceptionNested
  | fullPrecompileManagedExceptionTop
  | cancel
  | escape
  | leaveUnsettled

def planSettlement (route : SettlementRoute) : SettlementAction :=
  match route with
  | .continued => .continueStep
  | .suspend => .suspendStep
  | .regularSuccessNested => .regularSuccessNested
  | .topLevelSuccess => .topLevelSuccess
  | .createSuccessNested => .nestedCreateDeposit
  | .revertNested => .revertNested
  | .revertTop => .revertTop
  | .exceptionTop => .exceptionTop
  | .exceptionNested => .exceptionNested
  | .codeDepositInvalidNested => .codeDepositInvalidNested
  | .codeDepositOutOfGasNested => .codeDepositOutOfGasNested
  | .fullPrecompileOutOfGasNested => .fullPrecompileOutOfGasNested
  | .fullPrecompileOutOfGasTop => .fullPrecompileOutOfGasTop
  | .fullPrecompileReturnedFailureNested => .fullPrecompileReturnedFailureNested
  | .fullPrecompileReturnedFailureTop => .fullPrecompileReturnedFailureTop
  | .fullPrecompileManagedExceptionNested => .fullPrecompileManagedExceptionNested
  | .fullPrecompileManagedExceptionTop => .fullPrecompileManagedExceptionTop
  | .cancelled => .cancel
  | .escaped => .escape
  | .noSettlement => .leaveUnsettled

def settleNestedCreation (leaves : CanonicalLeaves) (decision : Decision) : ShellResult :=
  let branch := leaves.settlement.classifyNestedCreateCodeDeposit decision.invocation decision.frame
  match branch with
  | .deposited => withRoute .createSuccessNested
      (leaves.settlement.createSuccessNested decision.invocation decision.frame)
  | .invalidCode => withRoute .codeDepositInvalidNested
      (leaves.settlement.codeDepositInvalidNested decision.invocation decision.frame)
  | .outOfGas => withRoute .codeDepositOutOfGasNested
      (leaves.settlement.codeDepositOutOfGasNested decision.invocation decision.frame)

def enactSettlement (leaves : CanonicalLeaves) (decision : Decision) : SettlementAction → ShellResult
  | .continueStep => withRoute .continued (leaves.settlement.continueStep decision.invocation decision.frame)
  | .suspendStep => withRoute .suspend (leaves.settlement.suspendStep decision.invocation decision.frame)
  | .regularSuccessNested => withRoute .regularSuccessNested
      (leaves.settlement.regularSuccessNested decision.invocation decision.frame)
  | .topLevelSuccess => withRoute .topLevelSuccess
      (leaves.settlement.topLevelSuccess decision.invocation decision.frame)
  | .nestedCreateDeposit => settleNestedCreation leaves decision
  | .codeDepositInvalidNested => withRoute .codeDepositInvalidNested
      (leaves.settlement.codeDepositInvalidNested decision.invocation decision.frame)
  | .codeDepositOutOfGasNested => withRoute .codeDepositOutOfGasNested
      (leaves.settlement.codeDepositOutOfGasNested decision.invocation decision.frame)
  | .revertNested => withRoute .revertNested (leaves.settlement.revertNested decision.invocation decision.frame)
  | .revertTop => withRoute .revertTop (leaves.settlement.revertTop decision.invocation decision.frame)
  | .exceptionTop => withRoute .exceptionTop (leaves.settlement.exceptionTop decision.invocation decision.frame)
  | .exceptionNested => withRoute .exceptionNested (leaves.settlement.exceptionNested decision.invocation decision.frame)
  | .fullPrecompileOutOfGasNested => withRoute .fullPrecompileOutOfGasNested
      (leaves.settlement.fullPrecompileOutOfGasNested decision.invocation decision.frame)
  | .fullPrecompileOutOfGasTop => withRoute .fullPrecompileOutOfGasTop
      (leaves.settlement.fullPrecompileOutOfGasTop decision.invocation decision.frame)
  | .fullPrecompileReturnedFailureNested => withRoute .fullPrecompileReturnedFailureNested
      (leaves.settlement.fullPrecompileReturnedFailureNested decision.invocation decision.frame)
  | .fullPrecompileReturnedFailureTop => withRoute .fullPrecompileReturnedFailureTop
      (leaves.settlement.fullPrecompileReturnedFailureTop decision.invocation decision.frame)
  | .fullPrecompileManagedExceptionNested => withRoute .fullPrecompileManagedExceptionNested
      (leaves.settlement.fullPrecompileManagedExceptionNested decision.invocation decision.frame)
  | .fullPrecompileManagedExceptionTop => withRoute .fullPrecompileManagedExceptionTop
      (leaves.settlement.fullPrecompileManagedExceptionTop decision.invocation decision.frame)
  | .cancel =>
      { termination := .cancelled
        route := .cancelled
        machine := decision.frame
        result := none
        reason := some (cancellationReasonOf decision.invocation)
        status := none
        directInlineStaticPrecompile := none
        disposal := DisposalObservation.notObserved }
  | .escape =>
      { termination := .escaped
        route := .escaped
        machine := decision.frame
        result := none
        reason := some (escapedReasonOf decision.invocation)
        status := none
        directInlineStaticPrecompile := none
        disposal := DisposalObservation.notObserved }
  | .leaveUnsettled =>
      { termination := .incomplete
        route := .noSettlement
        machine := decision.frame
        result := none
        reason := some "unsettledRoute"
        status := none
        directInlineStaticPrecompile := none
        disposal := DisposalObservation.notObserved }

def settleDecision (leaves : CanonicalLeaves) (decision : Decision) : ShellResult :=
  enactSettlement leaves decision (planSettlement (routeForDecision decision))

inductive ClosingPlan where
  | retain
  | releaseAbruptly

def planClosing (outcome : ShellResult) : ClosingPlan :=
  match outcome.termination with
  | .completed | .suspended => .retain
  | .cancelled | .escaped | .incomplete => .releaseAbruptly

def closeTerminal (leaves : CanonicalLeaves) (outcome : ShellResult) : ShellResult :=
  match planClosing outcome with
  | .retain => outcome
  | .releaseAbruptly =>
      let released := leaves.cleanup outcome
      { outcome with machine := released.2, disposal := released.1 }

def evaluateIteration (leaves : CanonicalLeaves) (machine : Machine) : ShellResult :=
  let entered := enterFrame leaves machine
  let decision := chooseInvocation leaves entered
  let settled := settleDecision leaves decision
  let retained := { settled with directInlineStaticPrecompile := decision.inlineStaticPrecompile }
  closeTerminal leaves retained

end EvmFrameControlSettlementExtractor.Reference
