-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import EvmFrameDriverExtractor.Specification.OperationalTypes

/-!
Small source-derived entry adapter used by the operational vectors.

This adapter closes only the structural action which is visible directly in the
admitted `ExecuteTransaction` loop: fresh execution clears the transaction
return-data buffer.  Continuation preparation is deliberately left unresolved;
preserving a continuation requires the source `PrepareNextCallFrame` stack and
child-result effects and cannot be represented by returning the input machine.
The adapter therefore does not pretend to implement transfer logs, bytecode,
precompiles, settlement, or disposal.  Those remain `Option`-valued production
obligations in `OperationalSemantics`.

Top-level completion is kept as two distinct option-valued boundaries.  The
preparation adapter must materialize every `TransactionSubstate` field before
the settlement adapter may commit status, tracing, effects, and output.  Both
remain fail-closed until their production call/effect simulations are supplied.
-/

namespace Eip803x.Evm.FrameDriver.Operational.Adapters

open Eip803x.Evm.FrameMachineState
open Eip803x.Evm.FrameDriver.Operational

def clearFreshReturnData (machine : Machine) : Option Machine :=
  match machine.current.phase with
  | .fresh => some { machine with returnDataBuffer := [] }
  | .continuation | .running => none

def prepareContinuation (machine : Machine) : Option Machine :=
  match machine.current.phase with
  | .continuation => none
  | .fresh | .running => none

def prepareTopLevelSubstate (_machine : Machine) (_result : FrameResult) :
    Option TransactionSubstate :=
  none

def settleTopLevel (_machine : Machine) (_result : FrameResult)
    (_substate : TransactionSubstate) : Option FrameResult :=
  none

def installEntryAdapters (semantics : OperationalSemantics) : OperationalSemantics :=
  { semantics with
    clearReturnData := clearFreshReturnData
    prepareContinuation := Eip803x.Evm.FrameDriver.Operational.Adapters.prepareContinuation }

end Eip803x.Evm.FrameDriver.Operational.Adapters
