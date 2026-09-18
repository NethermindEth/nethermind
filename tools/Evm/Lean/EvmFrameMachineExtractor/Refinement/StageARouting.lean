-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import EvmFrameMachineExtractor.Specification.StageARouting

/-!
Theorems are intentionally bounded to Stage A metadata lookup. This module
does not assert an opcode-body, frame-loop, settlement, precompile-body,
transaction-adapter, or fuel-adequacy theorem.
-/

namespace EvmFrameMachineExtractor.Refinement.StageARouting

open EvmFrameMachineExtractor.Generated.EvmFrameMachineRouting

theorem generated_route_lookup_refines_independent_spec :
    EvmFrameMachineExtractor.Specification.StageARouting.allRouteLookupsAgree = true := by
  native_decide

theorem generated_precompile_lookup_refines_independent_spec :
    EvmFrameMachineExtractor.Specification.StageARouting.allPrecompileLookupsAgree = true := by
  native_decide

theorem generated_route_cardinality : opcodeRoutes.size = 1024 := by
  native_decide

theorem generated_precompile_cardinality : precompileRoutes.length = 18 := by
  native_decide

theorem generated_dependency_cardinality : dependencies.length = 32 := by
  native_decide

theorem generated_admitted_opcode_package_count :
    (dependencies.filter fun dependency =>
      dependency.kind == "opcodePackage" && dependency.admitted).length = 14 := by
  native_decide

theorem generated_required_operational_theorem_count :
    (dependencies.flatMap fun dependency => dependency.requiredTheorems).length = 16 := by
  native_decide

theorem generated_admitted_enabled_route_count :
    (opcodeRoutes.toList.filter fun route => route.kind == .enabled && route.admitted).length = 612 := by
  native_decide

theorem generated_incomplete_enabled_route_count :
    (opcodeRoutes.toList.filter fun route => route.kind == .enabled && !route.admitted).length = 0 := by
  native_decide

end EvmFrameMachineExtractor.Refinement.StageARouting
