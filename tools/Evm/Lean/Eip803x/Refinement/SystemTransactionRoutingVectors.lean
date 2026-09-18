-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Refinement.SystemTransactionRouting

namespace Eip803x.Refinement.SystemTransactionRouting.Vectors

open Eip803x.Refinement.SystemTransactionRouting

def none : Generated.Options := ⟨false, false, false, false, false⟩
def commit : Generated.Options := ⟨true, false, false, false, false⟩
def skipValidation : Generated.Options := ⟨false, false, true, false, false⟩
def skipValidationAndCommit : Generated.Options := ⟨true, false, true, false, false⟩
def warmup : Generated.Options := ⟨false, false, false, true, false⟩
def allFlags : Generated.Options := ⟨true, true, true, true, true⟩

theorem system_transaction_routes_to_system : generatedRoute true none = .system := by rfl
theorem normal_transaction_routes_normally : generatedRoute false none = .normal := by rfl
theorem exact_skip_routes_to_system : generatedRoute false skipValidation = .system := by rfl
theorem skip_and_commit_does_not_trigger_override_route :
    generatedRoute false skipValidationAndCommit = .normal := by rfl
theorem warmup_does_not_trigger_override_route : generatedRoute false warmup = .normal := by rfl
theorem system_warmup_still_routes_to_system : generatedRoute true warmup = .system := by rfl

theorem none_pays_original_value : Generated.shouldPayOriginalValue none = true := by rfl
theorem warmup_pays_original_value : Generated.shouldPayOriginalValue warmup = true := by rfl
theorem skip_does_not_pay_original_value :
    Generated.shouldPayOriginalValue skipValidation = false := by rfl
theorem all_flags_do_not_pay_original_value :
    Generated.shouldPayOriginalValue allFlags = false := by rfl

theorem none_effective_options_add_commit_and_skip :
    Generated.getSystemExecutionOptions none true = skipValidationAndCommit := by rfl
theorem warmup_effective_options_preserve_warmup :
    Generated.getSystemExecutionOptions warmup true =
      ⟨true, false, true, true, false⟩ := by rfl
theorem skipped_effective_options_are_unchanged :
    Generated.getSystemExecutionOptions allFlags false = allFlags := by rfl

theorem system_none_excludes_normal_counters :
    (generatedObservation true none false).participatesInNormalBlockCounters = false := by rfl
theorem system_warmup_excludes_normal_counters :
    (generatedObservation true warmup false).participatesInNormalBlockCounters = false := by rfl
theorem direct_normal_commit_participates :
    Generated.participatesInNormalBlockCounters commit false = true := by rfl
theorem parallel_normal_commit_is_excluded :
    Generated.participatesInNormalBlockCounters commit true = false := by rfl

namespace Mutations

def routeOnAnySkip (isSystemTransaction : Bool) (options : Generated.Options) : Bool :=
  isSystemTransaction || options.skipValidation

def routeWithoutClassifier (_isSystemTransaction : Bool) (options : Generated.Options) : Bool :=
  options.skipValidation && !options.commit && !options.restore && !options.warmup && !options.buildUp

def routeWarmupAsSystem (isSystemTransaction : Bool) (options : Generated.Options) : Bool :=
  isSystemTransaction || options.warmup

def addCommitOnly (options : Generated.Options) : Generated.Options :=
  { options with commit := true }

def countEverySequential (_options : Generated.Options) (parallel : Bool) : Bool :=
  !parallel

def payOriginalEvenWhenSkipped (_options : Generated.Options) : Bool := true

theorem any_skip_route_mutation_is_observed :
    routeOnAnySkip false skipValidationAndCommit !=
      Generated.useSystemProcessor false skipValidationAndCommit := by decide

theorem lost_classifier_mutation_is_observed :
    routeWithoutClassifier true none != Generated.useSystemProcessor true none := by decide

theorem warmup_route_mutation_is_observed :
    routeWarmupAsSystem false warmup != Generated.useSystemProcessor false warmup := by decide

theorem missing_skip_option_mutation_breaks_counter_exclusion :
    Generated.participatesInNormalBlockCounters (addCommitOnly none) false = true := by rfl

theorem counter_gate_mutation_is_observed :
    countEverySequential skipValidation false !=
      Generated.participatesInNormalBlockCounters skipValidation false := by decide

theorem pay_original_mutation_is_observed :
    payOriginalEvenWhenSkipped skipValidation !=
      Generated.shouldPayOriginalValue skipValidation := by decide

end Mutations

end Eip803x.Refinement.SystemTransactionRouting.Vectors
