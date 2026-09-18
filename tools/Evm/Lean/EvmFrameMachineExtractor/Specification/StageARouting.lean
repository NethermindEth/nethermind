-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import EvmFrameMachineExtractor.Generated.EvmFrameMachineKernel

/-!
Independent bounded lookup specification for the Stage A routing artifact.
It deliberately defines lookup by linear search rather than reusing the
generated array-index and `List.find?` implementations.
-/

namespace EvmFrameMachineExtractor.Specification.StageARouting

open EvmFrameMachineExtractor.Generated.EvmFrameMachineRouting

def routeMatches (table : DispatchTable) (byte : Nat)
    (route : OpcodeRoute) : Bool :=
  route.table == table && route.byte == byte

def routeAt (table : DispatchTable) (byte : Nat) : Option OpcodeRoute :=
  opcodeRoutes.toList.find? (routeMatches table byte)

def precompileAt : Nat -> List PrecompileRoute -> Option PrecompileRoute
  | _, [] => none
  | address, route :: tail =>
      if route.address = address then some route else precompileAt address tail

def allTables : List DispatchTable :=
  [.noTrace, .noTraceCancelable, .traced, .tracedCancelable]

def allRouteLookupsAgree : Bool :=
  allTables.all fun table =>
    (List.range 256).all fun byte =>
      decide (EvmFrameMachineExtractor.Generated.EvmFrameMachineRouting.routeAt table byte =
        routeAt table byte)

def allPrecompileLookupsAgree : Bool :=
  (List.range 257).all fun address =>
    decide (EvmFrameMachineExtractor.Generated.EvmFrameMachineRouting.precompileAt address =
      precompileAt address precompileRoutes)

end EvmFrameMachineExtractor.Specification.StageARouting
