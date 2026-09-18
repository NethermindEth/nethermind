// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;

namespace Nethermind.Evm.Lean.PrecompileFrameExtractor;

internal static class PrecompileFrameLeanEmitter
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);

    internal static byte[] Emit(IrDocument ir, string irSha256, string sourceSha256)
    {
        if (ir.PrecompileBindings.Length != 18 || ir.Routes.Length != 18 ||
            ir.Registry.ProviderEntries.Length != 18 || ir.Dependencies.Length == 0 ||
            ir.TrySaveBounds is null || ir.TrySaveBounds.FailureEffects is null ||
            ir.Routes.Any(candidate => candidate.FullFrameOutcomes.Length != 4 || candidate.DirectOutcomes.Length != 5))
        {
            throw new ExtractionException("Cannot emit precompile frame Lean for an incomplete typed IR.");
        }

        if (ir.TrySaveBounds.SourcePath != "src/Nethermind/Nethermind.Evm/EvmPooledMemory.cs" ||
            ir.TrySaveBounds.Owner != "EvmPooledMemory" || ir.TrySaveBounds.Member != "TrySave" ||
            ir.TrySaveBounds.ValidationHelper != "CheckMemoryAccessViolation" ||
            ir.TrySaveBounds.FailureFlag != "isViolation" ||
            !ir.TrySaveBounds.BoundsFailurePrecedesMutation ||
            !ir.TrySaveBounds.FailureEffects.SequenceEqual([
                TrySaveFailureEffect.ReturnFalse,
                TrySaveFailureEffect.NoUpdateSize,
                TrySaveFailureEffect.NoSaveAfterGas,
                TrySaveFailureEffect.NoMemoryMutation,
                TrySaveFailureEffect.NoOutputWrite,
            ]))
        {
            throw new ExtractionException("Cannot emit precompile frame Lean for an unadmitted TrySave bounds residue.");
        }

        RouteDescriptor route = ir.Routes[0];
        if (ir.Routes.Any(candidate =>
                candidate.CallKinds.Length != candidate.CallTargets.Length ||
                !candidate.CallKinds.SequenceEqual(route.CallKinds) ||
                !candidate.CallTargets.SequenceEqual(route.CallTargets) ||
                candidate.Cancellation != route.Cancellation ||
                candidate.FullFrameActionTraceConditional != route.FullFrameActionTraceConditional ||
                !candidate.FullFrameOutcomes.SequenceEqual(route.FullFrameOutcomes, FrameOutcomeComparer.Instance) ||
                !candidate.DirectOutcomes.SequenceEqual(route.DirectOutcomes, DirectFrameOutcomeComparer.Instance)))
        {
            throw new ExtractionException("Precompile frame routes disagree on shared frame semantics.");
        }

        StringBuilder source = new();
        source.AppendLine("-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited");
        source.AppendLine("-- SPDX-License-Identifier: LGPL-3.0-only");
        source.AppendLine();
        source.AppendLine("-- This file is generated. Do not edit.");
        source.AppendLine($"-- Extractor version: {ir.ExtractorVersion}");
        source.AppendLine($"-- Canonical precompile-frame IR SHA-256: {irSha256}");
        source.AppendLine($"-- Closed source/dependency SHA-256: {sourceSha256}");
        source.AppendLine("-- This module contains routing and classification only; no leaf transition or crypto theorem is emitted.");
        source.AppendLine();
        source.AppendLine("namespace Eip803x.Generated.PrecompileFrameStageA");
        source.AppendLine();
        source.AppendLine("inductive Activation where");
        source.AppendLine("  | always");
        source.AppendLine("  | eip198");
        source.AppendLine("  | eip196And197");
        source.AppendLine("  | eip152");
        source.AppendLine("  | eip4844");
        source.AppendLine("  | eip2537");
        source.AppendLine("  | eip7212Or7951");
        source.AppendLine("  deriving DecidableEq, Repr");
        source.AppendLine();
        source.AppendLine("structure ForkFacts where");
        source.AppendLine("  eip198 : Bool");
        source.AppendLine("  eip196 : Bool");
        source.AppendLine("  eip197 : Bool");
        source.AppendLine("  eip152 : Bool");
        source.AppendLine("  eip4844 : Bool");
        source.AppendLine("  eip2537 : Bool");
        source.AppendLine("  eip7212 : Bool");
        source.AppendLine("  eip7951 : Bool");
        source.AppendLine();
        source.AppendLine("def active (facts : ForkFacts) (activation : Activation) : Bool :=");
        source.AppendLine("  match activation with");
        source.AppendLine("  | .always => true");
        source.AppendLine("  | .eip198 => facts.eip198");
        source.AppendLine("  | .eip196And197 => facts.eip196 && facts.eip197");
        source.AppendLine("  | .eip152 => facts.eip152");
        source.AppendLine("  | .eip4844 => facts.eip4844");
        source.AppendLine("  | .eip2537 => facts.eip2537");
        source.AppendLine("  | .eip7212Or7951 => facts.eip7212 || facts.eip7951");
        source.AppendLine();
        source.AppendLine("inductive RouteMode where");
        source.AppendLine("  | inactiveCode");
        source.AppendLine("  | fullFrame");
        source.AppendLine("  | directStaticCall");
        source.AppendLine("  | delegatedPrecompileSuppressed");
        source.AppendLine("  deriving DecidableEq, Repr");
        source.AppendLine();
        source.AppendLine("structure RouteFacts where");
        source.AppendLine("  active : Bool");
        source.AppendLine("  isStaticCall : Bool");
        source.AppendLine("  instructionTracing : Bool");
        source.AppendLine("  actionTracing : Bool");
        source.AppendLine("  isRipemd160 : Bool");
        source.AppendLine("  delegatedPrecompile : Bool");
        source.AppendLine();
        source.AppendLine("def route (facts : RouteFacts) : RouteMode :=");
        source.AppendLine("  if facts.delegatedPrecompile then .delegatedPrecompileSuppressed");
        source.AppendLine("  else if !facts.active then .inactiveCode");
        source.AppendLine("  else if facts.isStaticCall && !facts.instructionTracing &&");
        source.AppendLine("      !facts.actionTracing && !facts.isRipemd160 then .directStaticCall");
        source.AppendLine("  else .fullFrame");
        source.AppendLine();
        source.AppendLine("inductive PricingOutcome where");
        source.AppendLine("  | success");
        source.AppendLine("  | baseDataOverflow");
        source.AppendLine("  | outOfGas");
        source.AppendLine("  deriving DecidableEq, Repr");
        source.AppendLine();
        source.AppendLine("structure PricingResult where");
        source.AppendLine("  outcome : PricingOutcome");
        source.AppendLine("  remainingGas : Nat");
        source.AppendLine("  chargedGas : Nat");
        source.AppendLine("  runLeaf : Bool");
        source.AppendLine("  deriving DecidableEq, Repr");
        source.AppendLine();
        source.AppendLine("def uint64Modulus : Nat := 2 ^ 64");
        source.AppendLine("def uint64Max : Nat := uint64Modulus - 1");
        source.AppendLine("def normalizeUInt64 (value : Nat) : Nat := value % uint64Modulus");
        source.AppendLine();
        source.AppendLine("def priceNormalized (gas base data : Nat) : PricingResult :=");
        source.AppendLine("  if data > uint64Max || base > uint64Max - data then");
        source.AppendLine("    { outcome := .baseDataOverflow, remainingGas := gas, chargedGas := 0, runLeaf := false }");
        source.AppendLine("  else");
        source.AppendLine("    let total := base + data");
        source.AppendLine("    if gas < total then");
        source.AppendLine("      { outcome := .outOfGas, remainingGas := 0, chargedGas := 0, runLeaf := false }");
        source.AppendLine("    else");
        source.AppendLine("      { outcome := .success, remainingGas := gas - total, chargedGas := total, runLeaf := true }");
        source.AppendLine();
        source.AppendLine("def price (gas base data : Nat) : PricingResult :=");
        source.AppendLine("  priceNormalized (normalizeUInt64 gas) (normalizeUInt64 base) (normalizeUInt64 data)");
        source.AppendLine();
        source.AppendLine("inductive CacheOutcome where");
        source.AppendLine("  | uncached");
        source.AppendLine("  | hit");
        source.AppendLine("  | miss");
        source.AppendLine("  | invalidInput");
        source.AppendLine("  deriving DecidableEq, Repr");
        source.AppendLine();
        source.AppendLine("inductive CacheEffect where");
        source.AppendLine("  | returnUncached");
        source.AppendLine("  | normalizeInput");
        source.AppendLine("  | buildKey");
        source.AppendLine("  | lookup");
        source.AppendLine("  | returnCached");
        source.AppendLine("  | runOriginalInput");
        source.AppendLine("  | updateCache");
        source.AppendLine("  | skipCacheUpdate");
        source.AppendLine("  deriving DecidableEq, Repr");
        source.AppendLine();
        source.AppendLine("structure CacheKey where");
        source.AppendLine("  address : Nat");
        source.AppendLine("  normalizedInput : List Nat");
        source.AppendLine("  releaseSpec : Nat");
        source.AppendLine("  deriving DecidableEq, Repr");
        source.AppendLine();
        source.AppendLine("structure CacheFacts where");
        source.AppendLine("  supportsCaching : Bool");
        source.AppendLine("  partitionAvailable : Bool");
        source.AppendLine("  hit : Bool");
        source.AppendLine("  invalidInput : Bool");
        source.AppendLine("  address : Nat");
        source.AppendLine("  originalInput : List Nat");
        source.AppendLine("  normalizedInput : List Nat");
        source.AppendLine("  releaseSpec : Nat");
        source.AppendLine();
        source.AppendLine("structure CacheResult where");
        source.AppendLine("  outcome : CacheOutcome");
        source.AppendLine("  key : CacheKey");
        source.AppendLine("  returnedFromCache : Bool");
        source.AppendLine("  runLeaf : Bool");
        source.AppendLine("  runInput : List Nat");
        source.AppendLine("  cacheUpdated : Bool");
        source.AppendLine("  effects : List CacheEffect");
        source.AppendLine("  deriving Repr");
        source.AppendLine();
        source.AppendLine("def normalizeCacheInput (facts : CacheFacts) : List Nat := facts.normalizedInput");
        source.AppendLine();
        source.AppendLine("def cacheKey (facts : CacheFacts) : CacheKey :=");
        source.AppendLine("  { address := facts.address, normalizedInput := normalizeCacheInput facts, releaseSpec := facts.releaseSpec }");
        source.AppendLine();
        source.AppendLine("def cacheStep (facts : CacheFacts) : CacheResult :=");
        source.AppendLine("  let key := cacheKey facts");
        CacheBranch uncached = CacheBranchFor(ir.Cache.Branches, CacheOutcome.Uncached);
        CacheBranch hit = CacheBranchFor(ir.Cache.Branches, CacheOutcome.Hit);
        CacheBranch invalid = CacheBranchFor(ir.Cache.Branches, CacheOutcome.InvalidInput);
        CacheBranch miss = CacheBranchFor(ir.Cache.Branches, CacheOutcome.Miss);
        EmitCacheResult(source, "if !facts.supportsCaching || !facts.partitionAvailable then", uncached);
        EmitCacheResult(source, "else if facts.hit then", hit);
        EmitCacheResult(source, "else if facts.invalidInput then", invalid);
        EmitCacheResult(source, "else", miss);
        source.AppendLine();
        source.AppendLine("inductive LeafSignal where");
        source.AppendLine("  | success");
        source.AppendLine("  | returnedFailure");
        source.AppendLine("  | managedException");
        source.AppendLine("  | missingDependency");
        source.AppendLine("  deriving DecidableEq, Repr");
        source.AppendLine();
        source.AppendLine("inductive ResultClassification where");
        source.AppendLine("  | success");
        source.AppendLine("  | returnedFailureHardException");
        source.AppendLine("  | managedExceptionNestedSoftRevert");
        source.AppendLine("  | managedExceptionTopLevelFailure");
        source.AppendLine("  | missingDependencyProcessExit");
        source.AppendLine("  deriving DecidableEq, Repr");
        source.AppendLine();
        source.AppendLine("def classify (nested : Bool) (signal : LeafSignal) : ResultClassification :=");
        source.AppendLine("  match signal with");
        source.AppendLine("  | .success => .success");
        source.AppendLine("  | .returnedFailure => .returnedFailureHardException");
        source.AppendLine("  | .managedException => if nested then .managedExceptionNestedSoftRevert else .managedExceptionTopLevelFailure");
        source.AppendLine("  | .missingDependency => .missingDependencyProcessExit");
        source.AppendLine();
        source.AppendLine("inductive CallKind where");
        source.AppendLine("  | call");
        source.AppendLine("  | callCode");
        source.AppendLine("  | delegateCall");
        source.AppendLine("  | staticCall");
        source.AppendLine("  deriving DecidableEq, Repr");
        source.AppendLine();
        source.AppendLine("inductive CallTargetKind where");
        source.AppendLine("  | codeSource");
        source.AppendLine("  | executingAccount");
        source.AppendLine("  deriving DecidableEq, Repr");
        source.AppendLine();
        source.AppendLine("structure CallFacts where");
        source.AppendLine("  kind : CallKind");
        source.AppendLine("  codeSource : Nat");
        source.AppendLine("  executingAccount : Nat");
        source.AppendLine("  cancelable : Bool");
        source.AppendLine("  cancelledBeforeDispatch : Bool");
        source.AppendLine("  cancelledAtBoundary : Bool");
        source.AppendLine("  opcodeCount : Nat");
        source.AppendLine("  completedWithoutException : Bool");
        source.AppendLine("  nextProgramCounter : Nat");
        source.AppendLine("  codeLength : Nat");
        source.AppendLine();
        source.AppendLine("def callTargetKind (facts : CallFacts) : CallTargetKind :=");
        source.AppendLine("  match facts.kind with");
        foreach (CallKind kind in route.CallKinds)
        {
            CallTargetKind target = route.CallTargets[Array.IndexOf(route.CallKinds, kind)];
            source.AppendLine($"  | .{LeanCallKind(kind)} => .{LeanCallTarget(target)}");
        }

        source.AppendLine();
        source.AppendLine("def callTarget (facts : CallFacts) : Nat :=");
        source.AppendLine("  match callTargetKind facts with");
        source.AppendLine("  | .codeSource => facts.codeSource");
        source.AppendLine("  | .executingAccount => facts.executingAccount");
        source.AppendLine();
        source.AppendLine("def cancellationBoundary (facts : CallFacts) : Bool :=");
        source.AppendLine(EmitCancellationBoundaryPredicate(route.Cancellation));
        source.AppendLine();
        source.AppendLine("def cancellationAtBoundary (facts : CallFacts) : Bool :=");
        source.AppendLine(EmitCancellationAtBoundary(route.Cancellation));
        source.AppendLine();
        source.AppendLine("def cancellationRequested (facts : CallFacts) : Bool :=");
        source.AppendLine(EmitCancellationRequested(route.Cancellation));
        source.AppendLine();
        source.AppendLine("inductive Effect where");
        source.AppendLine("  | actionTrace");
        source.AppendLine("  | transferLog");
        source.AppendLine("  | accountTouchOrCreate");
        source.AppendLine("  | ripemdTouchLatch");
        source.AppendLine("  | pricing");
        source.AppendLine("  | run");
        source.AppendLine("  | childRefund");
        source.AppendLine("  | childCommit");
        source.AppendLine("  | stateGasRestore");
        source.AppendLine("  | snapshotRestore");
        source.AppendLine("  | executionGasClear");
        source.AppendLine("  | returnDataClear");
        source.AppendLine("  | handleRevert");
        source.AppendLine("  | handleReturn");
        source.AppendLine("  | returndata");
        source.AppendLine("  | returnOutOfGas");
        source.AppendLine("  | outputCopy");
        source.AppendLine("  | stackFailure");
        source.AppendLine("  | stackResult");
        source.AppendLine("  deriving DecidableEq, Repr");
        source.AppendLine();
        source.AppendLine("inductive FrameOutcome where");
        source.AppendLine("  | pricingHardFailure");
        source.AppendLine("  | returnedLeafHardFailure");
        source.AppendLine("  | managedNestedSoftRevert");
        source.AppendLine("  | success");
        source.AppendLine("  deriving DecidableEq, Repr");
        source.AppendLine();
        source.AppendLine("inductive DirectFrameOutcome where");
        foreach (DirectFrameOutcomeDescriptor descriptor in route.DirectOutcomes)
        {
            source.AppendLine($"  | {LeanDirectOutcome(descriptor.Outcome)}");
        }

        source.AppendLine("  deriving DecidableEq, Repr");
        source.AppendLine();
        source.AppendLine("inductive DirectResult where");
        foreach (DirectResult result in route.DirectOutcomes.Select(static descriptor => descriptor.Result).Distinct())
        {
            source.AppendLine($"  | {LeanDirectResult(result)}");
        }

        source.AppendLine("  deriving DecidableEq, Repr");
        source.AppendLine();
        source.AppendLine("structure FrameResidue where");
        source.AppendLine("  effects : List Effect");
        source.AppendLine("  runsLeaf : Bool");
        source.AppendLine("  touchesAccount : Bool");
        source.AppendLine("  returnsRefund : Bool");
        source.AppendLine("  clearsReturnData : Bool");
        source.AppendLine("  pushesSuccess : Bool");
        source.AppendLine("  restoresSnapshot : Bool");
        source.AppendLine();
        source.AppendLine("def fullFrameEffects (actionTracing : Bool) (outcome : FrameOutcome) : List Effect :=");
        if (route.FullFrameActionTraceConditional)
        {
            source.AppendLine("  (if actionTracing then [.actionTrace] else []) ++");
        }
        else
        {
            source.AppendLine("  [] ++");
        }
        source.AppendLine("    match outcome with");
        foreach (FrameOutcomeDescriptor descriptor in route.FullFrameOutcomes)
        {
            source.AppendLine($"    | .{LeanOutcome(descriptor.Outcome)} => {LeanList(descriptor.Effects, LeanEffect)}");
        }

        source.AppendLine();
        source.AppendLine("def directEffects (outcome : DirectFrameOutcome) : List Effect :=");
        source.AppendLine("  match outcome with");
        foreach (DirectFrameOutcomeDescriptor descriptor in route.DirectOutcomes)
        {
            source.AppendLine($"  | .{LeanDirectOutcome(descriptor.Outcome)} => {LeanList(descriptor.Effects, LeanEffect)}");
        }

        source.AppendLine();
        source.AppendLine("def fullFrameResidue (actionTracing : Bool) (outcome : FrameOutcome) : FrameResidue :=");
        source.AppendLine("  match outcome with");
        foreach (FrameOutcomeDescriptor descriptor in route.FullFrameOutcomes)
        {
            source.AppendLine($"  | .{LeanOutcome(descriptor.Outcome)} => {{ effects := fullFrameEffects actionTracing outcome, runsLeaf := {LeanBool(descriptor.RunsLeaf)}, touchesAccount := {LeanBool(descriptor.TouchesAccount)}, returnsRefund := {LeanBool(descriptor.ReturnsRefund)}, clearsReturnData := {LeanBool(descriptor.ClearsReturnData)}, pushesSuccess := {LeanBool(descriptor.PushesSuccess)}, restoresSnapshot := {LeanBool(descriptor.RestoresSnapshot)} }}");
        }

        source.AppendLine();
        source.AppendLine("structure DirectFrameResidue where");
        source.AppendLine("  result : DirectResult");
        source.AppendLine("  effects : List Effect");
        source.AppendLine("  runsLeaf : Bool");
        source.AppendLine("  touchesAccount : Bool");
        source.AppendLine("  returnsRefund : Bool");
        source.AppendLine("  clearsReturnData : Bool");
        source.AppendLine("  pushesSuccess : Bool");
        source.AppendLine("  restoresSnapshot : Bool");
        source.AppendLine();
        source.AppendLine("def directResidue (outcome : DirectFrameOutcome) : DirectFrameResidue :=");
        source.AppendLine("  match outcome with");
        foreach (DirectFrameOutcomeDescriptor descriptor in route.DirectOutcomes)
        {
            source.AppendLine($"  | .{LeanDirectOutcome(descriptor.Outcome)} => {{ result := .{LeanDirectResult(descriptor.Result)}, effects := directEffects outcome, runsLeaf := {LeanBool(descriptor.RunsLeaf)}, touchesAccount := {LeanBool(descriptor.TouchesAccount)}, returnsRefund := {LeanBool(descriptor.ReturnsRefund)}, clearsReturnData := {LeanBool(descriptor.ClearsReturnData)}, pushesSuccess := {LeanBool(descriptor.PushesSuccess)}, restoresSnapshot := {LeanBool(descriptor.RestoresSnapshot)} }}");
        }

        source.AppendLine();
        source.AppendLine("inductive TrySaveFailureEffect where");
        source.AppendLine("  | returnFalse");
        source.AppendLine("  | noUpdateSize");
        source.AppendLine("  | noSaveAfterGas");
        source.AppendLine("  | noMemoryMutation");
        source.AppendLine("  | noOutputWrite");
        source.AppendLine("  deriving DecidableEq, Repr");
        source.AppendLine();
        source.AppendLine("structure TrySaveBounds where");
        source.AppendLine("  sourcePath : String");
        source.AppendLine("  owner : String");
        source.AppendLine("  member : String");
        source.AppendLine("  validationHelper : String");
        source.AppendLine("  failureFlag : String");
        source.AppendLine("  failureEffects : List TrySaveFailureEffect");
        source.AppendLine("  boundsFailurePrecedesMutation : Bool");
        source.AppendLine();
        source.AppendLine("def trySaveBounds : TrySaveBounds :=");
        source.AppendLine($"  {{ sourcePath := \"{Escape(ir.TrySaveBounds.SourcePath)}\", owner := \"{Escape(ir.TrySaveBounds.Owner)}\", member := \"{Escape(ir.TrySaveBounds.Member)}\",");
        source.AppendLine($"    validationHelper := \"{Escape(ir.TrySaveBounds.ValidationHelper)}\", failureFlag := \"{Escape(ir.TrySaveBounds.FailureFlag)}\",");
        source.AppendLine($"    failureEffects := {LeanList(ir.TrySaveBounds.FailureEffects, LeanTrySaveFailureEffect)}, boundsFailurePrecedesMutation := {LeanBool(ir.TrySaveBounds.BoundsFailurePrecedesMutation)} }}");
        source.AppendLine();
        source.AppendLine("structure LeafBinding where");
        source.AppendLine("  name : String");
        source.AppendLine("  address : Nat");
        source.AppendLine("  activation : Activation");
        source.AppendLine("  declaration : String");
        source.AppendLine("  standardImplementations : List String");
        source.AppendLine("  oracleModule : String");
        source.AppendLine("  oracleNamespace : String");
        source.AppendLine("  oracleEntrySymbol : String");
        source.AppendLine("  oracleSymbol : String");
        source.AppendLine("  oracleOnly : Bool");
        source.AppendLine("  deriving Repr");
        source.AppendLine();
        source.AppendLine("def providerRegistry : List (String × Nat) :=");
        source.AppendLine("  [");
        foreach (ProviderEntry entry in ir.Registry.ProviderEntries)
        {
            source.AppendLine($"    (\"{Escape(entry.Name)}\", {entry.Address}),");
        }

        source.AppendLine("  ]");
        source.AppendLine();
        source.AppendLine("def leafBindings : List LeafBinding :=");
        source.AppendLine("  [");
        foreach (PrecompileBinding binding in ir.PrecompileBindings)
        {
            string activation = LeanActivation(binding.Activation);
            string implementations = binding.StandardSourcePaths.Length == 0
                ? "[]"
                : "[" + string.Join(", ", binding.StandardSourcePaths.Select(path => $"\"{Escape(path)}\"")) + "]";
            source.AppendLine($"    {{ name := \"{Escape(binding.Name)}\", address := {binding.Address}, activation := .{activation},");
            source.AppendLine($"      declaration := \"{Escape(binding.BaseSourcePath)}\", standardImplementations := {implementations},");
            source.AppendLine($"      oracleModule := \"{Escape(binding.OracleModule)}\", oracleNamespace := \"{Escape(binding.OracleNamespace)}\", oracleEntrySymbol := \"{Escape(binding.OracleEntrySymbol)}\", oracleSymbol := \"{Escape(binding.OracleSymbol)}\", oracleOnly := true }},");
        }

        source.AppendLine("  ]");
        source.AppendLine();
        source.AppendLine("structure WorldInput where");
        source.AppendLine("  accountRead : Bool");
        source.AppendLine("  accountAccess : Bool");
        source.AppendLine("  snapshotTaken : Bool");
        source.AppendLine("  ripemdLatch : Bool");
        source.AppendLine("  deriving Repr");
        source.AppendLine();
        source.AppendLine("structure WorldOutput where");
        source.AppendLine("  accountTouched : Bool");
        source.AppendLine("  journalCommitted : Bool");
        source.AppendLine("  journalRestored : Bool");
        source.AppendLine("  ripemdRestored : Bool");
        source.AppendLine("  refundReturned : Bool");
        source.AppendLine("  deriving Repr");
        source.AppendLine();
        source.AppendLine("def WorldJournalRelation := WorldInput → WorldOutput → Prop");
        source.AppendLine("def composeWorld (journal : WorldJournalRelation) (input : WorldInput) (output : WorldOutput) : Prop :=");
        source.AppendLine("  journal input output");
        source.AppendLine("def worldCompositionGateOpen : Bool := true");
        source.AppendLine();
        source.AppendLine("def standardBuild : Bool := true");
        source.AppendLine("def lowLookupMaximum : Nat := 0x100");
        source.AppendLine("def noDelegationForActivePrecompile : Bool := true");
        source.AppendLine("def directRipemdExcluded : Bool := true");
        source.AppendLine("def pricingPrecedesRun : Bool := true");
        source.AppendLine();
        source.AppendLine("end Eip803x.Generated.PrecompileFrameStageA");
        return Utf8WithoutBom.GetBytes(source.ToString().Replace("\r\n", "\n", StringComparison.Ordinal) + "\n");
    }

    private static string LeanActivation(ActivationRule activation) => activation switch
    {
        ActivationRule.Always => "always",
        ActivationRule.Eip198 => "eip198",
        ActivationRule.Eip196And197 => "eip196And197",
        ActivationRule.Eip152 => "eip152",
        ActivationRule.Eip4844 => "eip4844",
        ActivationRule.Eip2537 => "eip2537",
        ActivationRule.Eip7212Or7951 => "eip7212Or7951",
        _ => throw new ArgumentOutOfRangeException(nameof(activation)),
    };

    private static FrameOutcomeDescriptor Outcome(FrameOutcomeDescriptor[] descriptors, FrameOutcome outcome) =>
        descriptors.SingleOrDefault(descriptor => descriptor.Outcome == outcome)
        ?? throw new ExtractionException($"Route semantics are missing {outcome}.");

    private sealed class FrameOutcomeComparer : IEqualityComparer<FrameOutcomeDescriptor>
    {
        internal static readonly FrameOutcomeComparer Instance = new();

        public bool Equals(FrameOutcomeDescriptor? left, FrameOutcomeDescriptor? right) =>
            left is not null && right is not null && left.Outcome == right.Outcome &&
            left.Effects.SequenceEqual(right.Effects) && left.RunsLeaf == right.RunsLeaf &&
            left.TouchesAccount == right.TouchesAccount && left.ReturnsRefund == right.ReturnsRefund &&
            left.ClearsReturnData == right.ClearsReturnData && left.PushesSuccess == right.PushesSuccess &&
            left.RestoresSnapshot == right.RestoresSnapshot;

        public int GetHashCode(FrameOutcomeDescriptor value) =>
            HashCode.Combine(value.Outcome, value.RunsLeaf, value.TouchesAccount, value.ReturnsRefund,
                value.ClearsReturnData, value.PushesSuccess, value.RestoresSnapshot);
    }

    private sealed class DirectFrameOutcomeComparer : IEqualityComparer<DirectFrameOutcomeDescriptor>
    {
        internal static readonly DirectFrameOutcomeComparer Instance = new();

        public bool Equals(DirectFrameOutcomeDescriptor? left, DirectFrameOutcomeDescriptor? right) =>
            left is not null && right is not null && left.Outcome == right.Outcome &&
            left.Result == right.Result &&
            left.Effects.SequenceEqual(right.Effects) && left.RunsLeaf == right.RunsLeaf &&
            left.TouchesAccount == right.TouchesAccount && left.ReturnsRefund == right.ReturnsRefund &&
            left.ClearsReturnData == right.ClearsReturnData && left.PushesSuccess == right.PushesSuccess &&
            left.RestoresSnapshot == right.RestoresSnapshot;

        public int GetHashCode(DirectFrameOutcomeDescriptor value) =>
            HashCode.Combine(value.Outcome, value.Result, value.RunsLeaf, value.TouchesAccount, value.ReturnsRefund,
                value.ClearsReturnData, value.PushesSuccess, value.RestoresSnapshot);
    }

    private static string LeanOutcome(FrameOutcome outcome) => outcome switch
    {
        FrameOutcome.PricingHardFailure => "pricingHardFailure",
        FrameOutcome.ReturnedLeafHardFailure => "returnedLeafHardFailure",
        FrameOutcome.ManagedNestedSoftRevert => "managedNestedSoftRevert",
        FrameOutcome.Success => "success",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
    };

    private static string LeanDirectOutcome(DirectFrameOutcome outcome) => outcome switch
    {
        DirectFrameOutcome.PricingHardFailure => "pricingHardFailure",
        DirectFrameOutcome.ReturnedLeafHardFailure => "returnedLeafHardFailure",
        DirectFrameOutcome.ManagedNestedSoftRevert => "managedNestedSoftRevert",
        DirectFrameOutcome.OutputCopyOutOfGas => "outputCopyOutOfGas",
        DirectFrameOutcome.Success => "success",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
    };

    private static string LeanDirectResult(DirectResult result) => result switch
    {
        DirectResult.StackFailure => "stackFailure",
        DirectResult.OutOfGas => "outOfGas",
        DirectResult.StackSuccess => "stackSuccess",
        _ => throw new ArgumentOutOfRangeException(nameof(result)),
    };

    private static string LeanEffect(FrameEffect effect) => effect switch
    {
        FrameEffect.ActionTrace => "actionTrace",
        FrameEffect.TransferLog => "transferLog",
        FrameEffect.AccountTouchOrCreate => "accountTouchOrCreate",
        FrameEffect.RipemdTouchLatch => "ripemdTouchLatch",
        FrameEffect.Pricing => "pricing",
        FrameEffect.Run => "run",
        FrameEffect.ChildRefund => "childRefund",
        FrameEffect.ChildCommit => "childCommit",
        FrameEffect.StateGasRestore => "stateGasRestore",
        FrameEffect.SnapshotRestore => "snapshotRestore",
        FrameEffect.ExecutionGasClear => "executionGasClear",
        FrameEffect.ReturnDataClear => "returnDataClear",
        FrameEffect.HandleRevert => "handleRevert",
        FrameEffect.HandleReturn => "handleReturn",
        FrameEffect.ReturnData => "returndata",
        FrameEffect.ReturnOutOfGas => "returnOutOfGas",
        FrameEffect.OutputCopy => "outputCopy",
        FrameEffect.StackSuccess => "stackResult",
        FrameEffect.StackFailure => "stackFailure",
        _ => throw new ArgumentOutOfRangeException(nameof(effect)),
    };

    private static string LeanList<T>(IEnumerable<T> values, Func<T, string> render) =>
        "[" + string.Join(", ", values.Select(value => "." + render(value))) + "]";

    private static string LeanBool(bool value) => value ? "true" : "false";

    private static string EmitCancellationBoundaryPredicate(CancellationDescriptor cancellation)
    {
        string[] terms = cancellation.BoundaryPredicate.Split(" && ", StringSplitOptions.None);
        if (terms.Length == 0 || terms.Any(static term => string.IsNullOrWhiteSpace(term)))
        {
            throw new ExtractionException("Cancellation boundary predicate is empty.");
        }

        string[] leanTerms = terms.Select(term => term switch
        {
            "cancelable" => "facts.cancelable",
            "completedWithoutException" => "facts.completedWithoutException",
            "(opcodeCount & checkMask) = 0" => $"facts.opcodeCount % {cancellation.CheckMask + 1} = 0",
            "nextProgramCounter < codeLength" => "facts.nextProgramCounter < facts.codeLength",
            _ => throw new ExtractionException($"Unknown cancellation boundary predicate term '{term}'."),
        }).ToArray();
        return $"  {string.Join(" && ", leanTerms)}";
    }

    private static string EmitCancellationAtBoundary(CancellationDescriptor cancellation) =>
        cancellation.ChecksAtBoundary
            ? "  cancellationBoundary facts && facts.cancelledAtBoundary"
            : "  false";

    private static string EmitCancellationRequested(CancellationDescriptor cancellation)
    {
        List<string> observations = [];
        if (cancellation.ChecksBeforeDispatch)
        {
            observations.Add("facts.cancelledBeforeDispatch");
        }

        if (cancellation.ChecksAtBoundary)
        {
            observations.Add("cancellationAtBoundary facts");
        }

        if (observations.Count == 0)
        {
            return "  false";
        }

        string observed = observations.Count == 1
            ? observations[0]
            : $"({string.Join(" || ", observations)})";
        return cancellation.Specialized
            ? $"  facts.cancelable && {observed}"
            : $"  {observed}";
    }

    private static CacheBranch CacheBranchFor(CacheBranch[] branches, CacheOutcome outcome) =>
        branches.SingleOrDefault(branch => branch.Outcome == outcome)
        ?? throw new ExtractionException($"Cache semantics are missing {outcome}.");

    private static void EmitCacheResult(StringBuilder source, string prefix, CacheBranch branch)
    {
        source.AppendLine($"  {prefix}");
        string runInput = branch.RunsLeaf ? "facts.originalInput" : "[]";
        source.AppendLine($"    {{ outcome := .{LeanCacheOutcome(branch.Outcome)}, key := key, returnedFromCache := {LeanBool(branch.ReturnedFromCache)}, runLeaf := {LeanBool(branch.RunsLeaf)}, runInput := {runInput}, cacheUpdated := {LeanBool(branch.CacheUpdated)},");
        source.AppendLine($"      effects := {LeanList(branch.Effects, LeanCacheEffect)} }}");
    }

    private static string LeanCacheOutcome(CacheOutcome outcome) => outcome switch
    {
        CacheOutcome.Uncached => "uncached",
        CacheOutcome.Hit => "hit",
        CacheOutcome.Miss => "miss",
        CacheOutcome.InvalidInput => "invalidInput",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
    };

    private static string LeanCacheEffect(CacheEffect effect) => effect switch
    {
        CacheEffect.ReturnUncached => "returnUncached",
        CacheEffect.NormalizeInput => "normalizeInput",
        CacheEffect.BuildKey => "buildKey",
        CacheEffect.Lookup => "lookup",
        CacheEffect.ReturnCached => "returnCached",
        CacheEffect.RunOriginalInput => "runOriginalInput",
        CacheEffect.UpdateCache => "updateCache",
        CacheEffect.SkipCacheUpdate => "skipCacheUpdate",
        _ => throw new ArgumentOutOfRangeException(nameof(effect)),
    };

    private static string LeanTrySaveFailureEffect(TrySaveFailureEffect effect) => effect switch
    {
        TrySaveFailureEffect.ReturnFalse => "returnFalse",
        TrySaveFailureEffect.NoUpdateSize => "noUpdateSize",
        TrySaveFailureEffect.NoSaveAfterGas => "noSaveAfterGas",
        TrySaveFailureEffect.NoMemoryMutation => "noMemoryMutation",
        TrySaveFailureEffect.NoOutputWrite => "noOutputWrite",
        _ => throw new ArgumentOutOfRangeException(nameof(effect)),
    };

    private static string LeanCallKind(CallKind kind) => kind switch
    {
        CallKind.Call => "call",
        CallKind.CallCode => "callCode",
        CallKind.DelegateCall => "delegateCall",
        CallKind.StaticCall => "staticCall",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static string LeanCallTarget(CallTargetKind target) => target switch
    {
        CallTargetKind.CodeSource => "codeSource",
        CallTargetKind.ExecutingAccount => "executingAccount",
        _ => throw new ArgumentOutOfRangeException(nameof(target)),
    };

    private static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal);
}
