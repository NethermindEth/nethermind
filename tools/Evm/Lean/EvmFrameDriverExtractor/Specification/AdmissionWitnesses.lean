-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import EvmFrameMachineExtractor.Refinement.StageARouting
import FrameJournalExtractor.Refinement.FrameJournal
import PrecompileFullFrameExtractor.Refinement.PrecompileFullFrame
import EvmFrameControlSettlementExtractor.Refinement.FrameControlSettlement

/-!
The Stage E source manifest names these accepted leaf proof modules as inputs.
The checks below keep their fully-qualified identities visible to the Lean
build; they are not a proof that an arbitrary production adapter implements a
leaf. That production simulation remains open; this package only uses
same-leaf equality in its algebra agreement.
-/

#check @EvmFrameMachineExtractor.Refinement.StageARouting.generated_route_lookup_refines_independent_spec
#check @FrameJournalExtractor.Refinement.FrameJournal.transition_refines
#check @Eip803x.PrecompileFullFrame.Refinement.source_full_frame_refines_reference
#check @EvmFrameControlSettlementExtractor.Refinement.hash_pinned_canonical_frame_control_settlement_single_iteration_control_agreement

namespace Eip803x.Evm.FrameDriver.AdmissionWitnesses

def acceptedLeafTheoremIdentities : List String :=
  [ "EvmFrameMachineExtractor.Refinement.StageARouting.generated_route_lookup_refines_independent_spec"
    , "FrameJournalExtractor.Refinement.FrameJournal.transition_refines"
    , "Eip803x.PrecompileFullFrame.Refinement.source_full_frame_refines_reference"
    , "EvmFrameControlSettlementExtractor.Refinement.hash_pinned_canonical_frame_control_settlement_single_iteration_control_agreement" ]

end Eip803x.Evm.FrameDriver.AdmissionWitnesses
