-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import FrameJournalExtractor.Specification.FrameJournalState
import WorldJournalExtractor.Specification.WorldJournal

namespace FrameJournalExtractor.Specification

open FrameJournalExtractor
open WorldJournalExtractor

namespace WorldSpec
export WorldJournalExtractor.Specification (step run)
end WorldSpec

def markCreated (world : WorldMachine) (address : Address) : WorldMachine :=
  { world with journal :=
      { world.journal with
        createdThisTx := FiniteSet.insert world.journal.createdThisTx address } }

def enter (kind : FrameKind) (machine : Machine) : Option Machine :=
  let beforeSnapshot := match kind with
    | .call => machine.world
    | .create address => markCreated machine.world address
  match WorldSpec.step .takeSnapshot beforeSnapshot with
  | none => none
  | some world => some { machine with world, frames := kind :: machine.frames }

def retainAndPop (machine : Machine) : Option Machine :=
  match machine.frames, machine.world.snapshots with
  | _ :: frames, _ :: snapshots =>
      some { machine with world := { machine.world with snapshots }, frames }
  | _, _ => none

def reapplyRipemdTouch (machine : Machine) : Option Machine :=
  if machine.ripemdTouchLatched then
    match WorldSpec.step (.readAccount ripemd160Address) machine.world with
    | none => none
    | some readWorld =>
        match FiniteMap.lookup readWorld.journal.accounts.current ripemd160Address with
        | none => some { machine with world := readWorld }
        | some account =>
            if isEmptyAccount account then
              (WorldSpec.step (.updateAccount ripemd160Address account) readWorld).map fun world =>
                { machine with world }
            else
              some { machine with world := readWorld }
  else
    some machine

def restoreAndPop (machine : Machine) : Option Machine :=
  match machine.frames with
  | [] => none
  | _ :: frames =>
      match WorldSpec.step .restoreSnapshot machine.world with
      | none => none
      | some world => reapplyRipemdTouch { machine with world, frames }

def step (operation : Operation) (machine : Machine) : Option Machine :=
  match operation with
  | .applyWorld worldOperation =>
      if isBodyOperation worldOperation then
        (WorldSpec.step worldOperation machine.world).map fun world => { machine with world }
      else
        none
  | .enterCall => enter .call machine
  | .enterCreate address => enter (.create address) machine
  | .preFrameCallFailure => some machine
  | .preFrameCreateFailure => some machine
  | .latchRipemdTouch => some { machine with ripemdTouchLatched := true }
  | .exitSuccess => retainAndPop machine
  | .exitRevert => restoreAndPop machine
  | .exitException => restoreAndPop machine

def run : List Operation → Machine → Option Machine
  | [], machine => some machine
  | operation :: tail, machine =>
      match step operation machine with
      | none => none
      | some next => run tail next

end FrameJournalExtractor.Specification
