-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Generated.SystemTransactionRoutingKernel

namespace Eip803x.Refinement.SystemTransactionRouting

namespace Generated

abbrev Options := Eip803x.Generated.SystemTransactionRoutingKernel.Options
abbrev useSystemProcessor := Eip803x.Generated.SystemTransactionRoutingKernel.useSystemProcessor
abbrev shouldPayOriginalValue := Eip803x.Generated.SystemTransactionRoutingKernel.shouldPayOriginalValue
abbrev getSystemExecutionOptions := Eip803x.Generated.SystemTransactionRoutingKernel.getSystemExecutionOptions
abbrev participatesInNormalBlockCounters :=
  Eip803x.Generated.SystemTransactionRoutingKernel.participatesInNormalBlockCounters

end Generated

namespace Spec

structure Options where
  commit : Bool
  restore : Bool
  skipValidation : Bool
  warmup : Bool
  buildUp : Bool
  deriving DecidableEq, Repr

inductive Route where
  | normal
  | system
  deriving DecidableEq, Repr

def isExactSkipValidation (options : Options) : Bool :=
  options.skipValidation && !options.commit && !options.restore && !options.warmup && !options.buildUp

def selectRoute (isSystemTransaction : Bool) (options : Options) : Route :=
  if isSystemTransaction || isExactSkipValidation options then .system else .normal

def shouldPayOriginalValue (options : Options) : Bool :=
  !options.skipValidation

def systemExecutionOptions (options : Options) : Options :=
  if shouldPayOriginalValue options then
    { options with commit := true, skipValidation := true }
  else
    options

def participatesInNormalBlockCounters (options : Options) (parallel : Bool) : Bool :=
  !options.skipValidation && !parallel

structure Observation where
  route : Route
  payOriginalValue : Bool
  effectiveOptions : Options
  participatesInNormalBlockCounters : Bool
  deriving DecidableEq, Repr

def observe (isSystemTransaction : Bool) (options : Options) (parallel : Bool) : Observation :=
  let payOriginalValue := shouldPayOriginalValue options
  let effectiveOptions := systemExecutionOptions options
  {
    route := selectRoute isSystemTransaction options
    payOriginalValue
    effectiveOptions
    participatesInNormalBlockCounters :=
      participatesInNormalBlockCounters effectiveOptions parallel
  }

end Spec

def toSpecOptions (options : Generated.Options) : Spec.Options :=
  {
    commit := options.commit
    restore := options.restore
    skipValidation := options.skipValidation
    warmup := options.warmup
    buildUp := options.buildUp
  }

def generatedRoute (isSystemTransaction : Bool) (options : Generated.Options) : Spec.Route :=
  if Generated.useSystemProcessor isSystemTransaction options then .system else .normal

def generatedObservation
    (isSystemTransaction : Bool) (options : Generated.Options) (parallel : Bool) : Spec.Observation :=
  let payOriginalValue := Generated.shouldPayOriginalValue options
  let effectiveOptions := Generated.getSystemExecutionOptions options payOriginalValue
  {
    route := generatedRoute isSystemTransaction options
    payOriginalValue
    effectiveOptions := toSpecOptions effectiveOptions
    participatesInNormalBlockCounters :=
      Generated.participatesInNormalBlockCounters effectiveOptions parallel
  }

theorem route_refines_handwritten_spec
    (isSystemTransaction : Bool) (options : Generated.Options) :
    generatedRoute isSystemTransaction options =
      Spec.selectRoute isSystemTransaction (toSpecOptions options) := by
  cases options with
  | mk commit restore skipValidation warmup buildUp =>
      cases isSystemTransaction <;>
      cases commit <;> cases restore <;> cases skipValidation <;> cases warmup <;> cases buildUp <;>
      rfl

theorem pay_original_value_refines_handwritten_spec (options : Generated.Options) :
    Generated.shouldPayOriginalValue options =
      Spec.shouldPayOriginalValue (toSpecOptions options) := by
  rfl

theorem effective_options_refine_handwritten_spec (options : Generated.Options) :
    toSpecOptions
        (Generated.getSystemExecutionOptions options
          (Generated.shouldPayOriginalValue options)) =
      Spec.systemExecutionOptions (toSpecOptions options) := by
  cases options with
  | mk commit restore skipValidation warmup buildUp =>
      cases skipValidation <;> rfl

theorem counter_gate_refines_handwritten_spec
    (options : Generated.Options) (parallel : Bool) :
    Generated.participatesInNormalBlockCounters options parallel =
      Spec.participatesInNormalBlockCounters (toSpecOptions options) parallel := by
  rfl

theorem observation_refines_handwritten_spec
    (isSystemTransaction : Bool) (options : Generated.Options) (parallel : Bool) :
    generatedObservation isSystemTransaction options parallel =
      Spec.observe isSystemTransaction (toSpecOptions options) parallel := by
  cases options with
  | mk commit restore skipValidation warmup buildUp =>
      cases isSystemTransaction <;>
      cases parallel <;>
      cases commit <;> cases restore <;> cases skipValidation <;> cases warmup <;> cases buildUp <;>
      rfl

theorem effective_system_options_exclude_normal_block_counters
    (options : Generated.Options) (parallel : Bool) :
    Generated.participatesInNormalBlockCounters
      (Generated.getSystemExecutionOptions options
        (Generated.shouldPayOriginalValue options))
      parallel = false := by
  cases options with
  | mk commit restore skipValidation warmup buildUp =>
      cases parallel <;> cases skipValidation <;> rfl

theorem classified_system_selects_system_route (options : Generated.Options) :
    generatedRoute true options = .system := by
  cases options <;> rfl

end Eip803x.Refinement.SystemTransactionRouting
