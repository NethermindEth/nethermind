-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import WorldJournalExtractor.Specification.WorldProjection

namespace FrameJournalExtractor

open WorldJournalExtractor

abbrev Address := WorldJournalExtractor.Address
abbrev WorldOperation := WorldJournalExtractor.Operation
abbrev WorldMachine := WorldJournalExtractor.Machine

inductive FrameKind where
  | call
  | create (address : Address)
  deriving DecidableEq, Repr

inductive Operation where
  | applyWorld (operation : WorldOperation)
  | enterCall
  | enterCreate (address : Address)
  | preFrameCallFailure
  | preFrameCreateFailure
  | latchRipemdTouch
  | exitSuccess
  | exitRevert
  | exitException
  deriving DecidableEq, Repr

structure Machine where
  world : WorldMachine
  frames : List FrameKind
  ripemdTouchLatched : Bool
  deriving DecidableEq, Repr

def Machine.WellFormed (machine : Machine) : Prop :=
  machine.frames.length = machine.world.snapshots.length

instance machineWellFormedDecidable (machine : Machine) : Decidable machine.WellFormed := by
  unfold Machine.WellFormed
  infer_instance

def isBodyOperation : WorldOperation → Bool
  | .takeSnapshot => false
  | .restoreSnapshot => false
  | _ => true

def ripemd160Address : Address := 3

def isEmptyAccount (account : AccountView) : Bool :=
  account.codeHash == emptyCodeHash && account.balance == 0 && account.nonce == 0

end FrameJournalExtractor
