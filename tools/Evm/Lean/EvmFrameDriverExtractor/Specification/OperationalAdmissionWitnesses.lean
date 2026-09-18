-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import EvmFrameMachineExtractor.Refinement.StageARouting
import Eip803x.Refinement.PureWordOpcode
import Keccak256OpcodeExtractor.Refinement.Keccak256Opcode
import Eip803x.Refinement.EnvironmentOpcode
import Eip803x.Refinement.AccountReadOpcode
import MemoryCopyOpcodeExtractor.Refinement.MemoryCopyOpcode
import PersistentStorageOpcodeExtractor.Refinement.PersistentStorageOpcode
import TransientStorageOpcodeExtractor.Refinement.TransientStorageOpcode
import Eip803x.Refinement.StackRearrangementOpcode
import PushOpcodeExtractor.Refinement.PushOpcode
import LogOpcodeExtractor.Refinement.LogOpcode
import CallCreateOpcodeExtractor.Refinement.CallCreateOpcode
import ControlFlowOpcodeExtractor.Refinement.ControlFlowOpcode
import ExtendedStackOpcodeExtractor.Refinement.ExtendedStackOpcode
import CallDataLoadOpcodeExtractor.Refinement.CallDataLoadOpcode
import PrecompileFrameExtractor.Refinement.PrecompileFrameStageB
import PrecompileFullFrameExtractor.Refinement.PrecompileFullFrame
import FrameJournalExtractor.Refinement.FrameJournal
import WorldJournalExtractor.Refinement.WorldJournal
import Eip803x.Refinement.StateGasTransition
import Eip803x.Refinement.PrecompileGasPricing
import EvmFrameControlSettlementExtractor.Refinement.FrameControlSettlement

/-!
Exact theorem-name witnesses for the independently accepted packages consumed
by Stage F.  The checks make a renamed or missing theorem fail the package
gate.  They are reference closure checks only: none of these names is used as
a proof that the `Option` adapters in `OperationalSemantics` implement the
production calls.
-/

#check @EvmFrameMachineExtractor.Refinement.StageARouting.generated_route_lookup_refines_independent_spec
#check @Eip803x.Refinement.PureWordOpcode.extracted_execute_refines
#check @Eip803x.Generated.Keccak256OpcodeRefinement.execute_refines
#check @Eip803x.Refinement.EnvironmentOpcode.closed_amsterdam_refines
#check @Eip803x.Refinement.AccountReadOpcode.generated_execution_refines_reference
#check @Eip803x.Generated.MemoryCopyOpcodeRefinement.extracted_amsterdam_closed_refines
#check @PersistentStorageOpcodeExtractor.Refinement.PersistentStorageOpcode.sload_byte_refines
#check @PersistentStorageOpcodeExtractor.Refinement.PersistentStorageOpcode.sstore_byte_refines
#check @TransientStorageOpcodeExtractor.Refinement.TransientStorageOpcode.tload_byte_refines
#check @TransientStorageOpcodeExtractor.Refinement.TransientStorageOpcode.tstore_byte_refines
#check @Eip803x.Evm.Refinement.StackRearrangementOpcode.generated_execute_refines_reference
#check @PushOpcodeExtractor.Refinement.PushOpcode.generated_execute_agrees
#check @LogOpcodeExtractor.Refinement.generated_execute_refines_reference
#check @Eip803x.Generated.CallCreateOpcodeRefinement.closed_amsterdam_refines
#check @Eip803x.Generated.ControlFlowOpcodeRefinement.extracted_amsterdam_refines
#check @ExtendedStackOpcodeExtractor.Refinement.execute_refines_handwritten_extended_stack
#check @Eip803x.Generated.CallDataLoadOpcodeRefinement.closed_amsterdam_refines
#check @Eip803x.PrecompileFrame.StageB.Refinement.source_execute_precompile_refines_reference
#check @Eip803x.PrecompileFullFrame.Refinement.source_full_frame_refines_reference
#check @FrameJournalExtractor.Refinement.FrameJournal.transition_refines
#check @WorldJournalExtractor.Refinement.WorldJournal.transition_refines
#check @Eip803x.Refinement.StateGasTransition.generated_refund_matches_spec
#check @Eip803x.Refinement.PrecompileGasPricing.tryConsume_refines_wrapper
#check @EvmFrameControlSettlementExtractor.Refinement.hash_pinned_canonical_frame_control_settlement_single_iteration_control_agreement

namespace Eip803x.Evm.FrameDriver.Operational.AdmissionWitnesses

def acceptedTheoremIdentities : List String :=
  [ "EvmFrameMachineExtractor.Refinement.StageARouting.generated_route_lookup_refines_independent_spec"
    , "Eip803x.Refinement.PureWordOpcode.extracted_execute_refines"
    , "Eip803x.Generated.Keccak256OpcodeRefinement.execute_refines"
    , "Eip803x.Refinement.EnvironmentOpcode.closed_amsterdam_refines"
    , "Eip803x.Refinement.AccountReadOpcode.generated_execution_refines_reference"
    , "Eip803x.Generated.MemoryCopyOpcodeRefinement.extracted_amsterdam_closed_refines"
    , "PersistentStorageOpcodeExtractor.Refinement.PersistentStorageOpcode.sload_byte_refines"
    , "PersistentStorageOpcodeExtractor.Refinement.PersistentStorageOpcode.sstore_byte_refines"
    , "TransientStorageOpcodeExtractor.Refinement.TransientStorageOpcode.tload_byte_refines"
    , "TransientStorageOpcodeExtractor.Refinement.TransientStorageOpcode.tstore_byte_refines"
    , "Eip803x.Evm.Refinement.StackRearrangementOpcode.generated_execute_refines_reference"
    , "PushOpcodeExtractor.Refinement.PushOpcode.generated_execute_agrees"
    , "LogOpcodeExtractor.Refinement.generated_execute_refines_reference"
    , "Eip803x.Generated.CallCreateOpcodeRefinement.closed_amsterdam_refines"
    , "Eip803x.Generated.ControlFlowOpcodeRefinement.extracted_amsterdam_refines"
    , "ExtendedStackOpcodeExtractor.Refinement.execute_refines_handwritten_extended_stack"
    , "Eip803x.Generated.CallDataLoadOpcodeRefinement.closed_amsterdam_refines"
    , "Eip803x.PrecompileFrame.StageB.Refinement.source_execute_precompile_refines_reference"
    , "Eip803x.PrecompileFullFrame.Refinement.source_full_frame_refines_reference"
    , "FrameJournalExtractor.Refinement.FrameJournal.transition_refines"
    , "WorldJournalExtractor.Refinement.WorldJournal.transition_refines"
    , "Eip803x.Refinement.StateGasTransition.generated_refund_matches_spec"
    , "Eip803x.Refinement.PrecompileGasPricing.tryConsume_refines_wrapper"
    , "EvmFrameControlSettlementExtractor.Refinement.hash_pinned_canonical_frame_control_settlement_single_iteration_control_agreement" ]

end Eip803x.Evm.FrameDriver.Operational.AdmissionWitnesses
