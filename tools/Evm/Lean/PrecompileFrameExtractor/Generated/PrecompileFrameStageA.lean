-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

-- This file is generated. Do not edit.
-- Extractor version: precompile-frame-stage-a-1
-- Canonical precompile-frame IR SHA-256: 29b6e2bdd5e2b2782e6c44498f356b698ee592b1668ef0cf7663bb0ce8d67be8
-- Closed source/dependency SHA-256: c58221ab5467b76205dedd69b7d8d2a35c8d438c01ec66297fdf708e97b8fe05
-- This module contains routing and classification only; no leaf transition or crypto theorem is emitted.

namespace Eip803x.Generated.PrecompileFrameStageA

inductive Activation where
  | always
  | eip198
  | eip196And197
  | eip152
  | eip4844
  | eip2537
  | eip7212Or7951
  deriving DecidableEq, Repr

structure ForkFacts where
  eip198 : Bool
  eip196 : Bool
  eip197 : Bool
  eip152 : Bool
  eip4844 : Bool
  eip2537 : Bool
  eip7212 : Bool
  eip7951 : Bool

def active (facts : ForkFacts) (activation : Activation) : Bool :=
  match activation with
  | .always => true
  | .eip198 => facts.eip198
  | .eip196And197 => facts.eip196 && facts.eip197
  | .eip152 => facts.eip152
  | .eip4844 => facts.eip4844
  | .eip2537 => facts.eip2537
  | .eip7212Or7951 => facts.eip7212 || facts.eip7951

inductive RouteMode where
  | inactiveCode
  | fullFrame
  | directStaticCall
  | delegatedPrecompileSuppressed
  deriving DecidableEq, Repr

structure RouteFacts where
  active : Bool
  isStaticCall : Bool
  instructionTracing : Bool
  actionTracing : Bool
  isRipemd160 : Bool
  delegatedPrecompile : Bool

def route (facts : RouteFacts) : RouteMode :=
  if facts.delegatedPrecompile then .delegatedPrecompileSuppressed
  else if !facts.active then .inactiveCode
  else if facts.isStaticCall && !facts.instructionTracing &&
      !facts.actionTracing && !facts.isRipemd160 then .directStaticCall
  else .fullFrame

inductive PricingOutcome where
  | success
  | baseDataOverflow
  | outOfGas
  deriving DecidableEq, Repr

structure PricingResult where
  outcome : PricingOutcome
  remainingGas : Nat
  chargedGas : Nat
  runLeaf : Bool
  deriving DecidableEq, Repr

def uint64Modulus : Nat := 2 ^ 64
def uint64Max : Nat := uint64Modulus - 1
def normalizeUInt64 (value : Nat) : Nat := value % uint64Modulus

def priceNormalized (gas base data : Nat) : PricingResult :=
  if data > uint64Max || base > uint64Max - data then
    { outcome := .baseDataOverflow, remainingGas := gas, chargedGas := 0, runLeaf := false }
  else
    let total := base + data
    if gas < total then
      { outcome := .outOfGas, remainingGas := 0, chargedGas := 0, runLeaf := false }
    else
      { outcome := .success, remainingGas := gas - total, chargedGas := total, runLeaf := true }

def price (gas base data : Nat) : PricingResult :=
  priceNormalized (normalizeUInt64 gas) (normalizeUInt64 base) (normalizeUInt64 data)

inductive CacheOutcome where
  | uncached
  | hit
  | miss
  | invalidInput
  deriving DecidableEq, Repr

inductive CacheEffect where
  | returnUncached
  | normalizeInput
  | buildKey
  | lookup
  | returnCached
  | runOriginalInput
  | updateCache
  | skipCacheUpdate
  deriving DecidableEq, Repr

structure CacheKey where
  address : Nat
  normalizedInput : List Nat
  releaseSpec : Nat
  deriving DecidableEq, Repr

structure CacheFacts where
  supportsCaching : Bool
  partitionAvailable : Bool
  hit : Bool
  invalidInput : Bool
  address : Nat
  originalInput : List Nat
  normalizedInput : List Nat
  releaseSpec : Nat

structure CacheResult where
  outcome : CacheOutcome
  key : CacheKey
  returnedFromCache : Bool
  runLeaf : Bool
  runInput : List Nat
  cacheUpdated : Bool
  effects : List CacheEffect
  deriving Repr

def normalizeCacheInput (facts : CacheFacts) : List Nat := facts.normalizedInput

def cacheKey (facts : CacheFacts) : CacheKey :=
  { address := facts.address, normalizedInput := normalizeCacheInput facts, releaseSpec := facts.releaseSpec }

def cacheStep (facts : CacheFacts) : CacheResult :=
  let key := cacheKey facts
  if !facts.supportsCaching || !facts.partitionAvailable then
    { outcome := .uncached, key := key, returnedFromCache := false, runLeaf := true, runInput := facts.originalInput, cacheUpdated := false,
      effects := [.returnUncached] }
  else if facts.hit then
    { outcome := .hit, key := key, returnedFromCache := true, runLeaf := false, runInput := [], cacheUpdated := false,
      effects := [.normalizeInput, .buildKey, .lookup, .returnCached] }
  else if facts.invalidInput then
    { outcome := .invalidInput, key := key, returnedFromCache := false, runLeaf := true, runInput := facts.originalInput, cacheUpdated := false,
      effects := [.normalizeInput, .buildKey, .lookup, .runOriginalInput, .skipCacheUpdate] }
  else
    { outcome := .miss, key := key, returnedFromCache := false, runLeaf := true, runInput := facts.originalInput, cacheUpdated := true,
      effects := [.normalizeInput, .buildKey, .lookup, .runOriginalInput, .updateCache] }

inductive LeafSignal where
  | success
  | returnedFailure
  | managedException
  | missingDependency
  deriving DecidableEq, Repr

inductive ResultClassification where
  | success
  | returnedFailureHardException
  | managedExceptionNestedSoftRevert
  | managedExceptionTopLevelFailure
  | missingDependencyProcessExit
  deriving DecidableEq, Repr

def classify (nested : Bool) (signal : LeafSignal) : ResultClassification :=
  match signal with
  | .success => .success
  | .returnedFailure => .returnedFailureHardException
  | .managedException => if nested then .managedExceptionNestedSoftRevert else .managedExceptionTopLevelFailure
  | .missingDependency => .missingDependencyProcessExit

inductive CallKind where
  | call
  | callCode
  | delegateCall
  | staticCall
  deriving DecidableEq, Repr

inductive CallTargetKind where
  | codeSource
  | executingAccount
  deriving DecidableEq, Repr

structure CallFacts where
  kind : CallKind
  codeSource : Nat
  executingAccount : Nat
  cancelable : Bool
  cancelledBeforeDispatch : Bool
  cancelledAtBoundary : Bool
  opcodeCount : Nat
  completedWithoutException : Bool
  nextProgramCounter : Nat
  codeLength : Nat

def callTargetKind (facts : CallFacts) : CallTargetKind :=
  match facts.kind with
  | .call => .codeSource
  | .callCode => .executingAccount
  | .delegateCall => .executingAccount
  | .staticCall => .codeSource

def callTarget (facts : CallFacts) : Nat :=
  match callTargetKind facts with
  | .codeSource => facts.codeSource
  | .executingAccount => facts.executingAccount

def cancellationBoundary (facts : CallFacts) : Bool :=
  facts.cancelable && facts.completedWithoutException && facts.opcodeCount % 1024 = 0 && facts.nextProgramCounter < facts.codeLength

def cancellationAtBoundary (facts : CallFacts) : Bool :=
  cancellationBoundary facts && facts.cancelledAtBoundary

def cancellationRequested (facts : CallFacts) : Bool :=
  facts.cancelable && (facts.cancelledBeforeDispatch || cancellationAtBoundary facts)

inductive Effect where
  | actionTrace
  | transferLog
  | accountTouchOrCreate
  | ripemdTouchLatch
  | pricing
  | run
  | childRefund
  | childCommit
  | stateGasRestore
  | snapshotRestore
  | executionGasClear
  | returnDataClear
  | handleRevert
  | handleReturn
  | returndata
  | returnOutOfGas
  | outputCopy
  | stackFailure
  | stackResult
  deriving DecidableEq, Repr

inductive FrameOutcome where
  | pricingHardFailure
  | returnedLeafHardFailure
  | managedNestedSoftRevert
  | success
  deriving DecidableEq, Repr

inductive DirectFrameOutcome where
  | pricingHardFailure
  | returnedLeafHardFailure
  | managedNestedSoftRevert
  | outputCopyOutOfGas
  | success
  deriving DecidableEq, Repr

inductive DirectResult where
  | stackFailure
  | outOfGas
  | stackSuccess
  deriving DecidableEq, Repr

structure FrameResidue where
  effects : List Effect
  runsLeaf : Bool
  touchesAccount : Bool
  returnsRefund : Bool
  clearsReturnData : Bool
  pushesSuccess : Bool
  restoresSnapshot : Bool

def fullFrameEffects (actionTracing : Bool) (outcome : FrameOutcome) : List Effect :=
  (if actionTracing then [.actionTrace] else []) ++
    match outcome with
    | .pricingHardFailure => [.transferLog, .accountTouchOrCreate, .ripemdTouchLatch, .pricing, .snapshotRestore, .returnDataClear, .stateGasRestore, .stackFailure]
    | .returnedLeafHardFailure => [.transferLog, .accountTouchOrCreate, .ripemdTouchLatch, .pricing, .run, .snapshotRestore, .returnDataClear, .stateGasRestore, .stackFailure]
    | .managedNestedSoftRevert => [.transferLog, .accountTouchOrCreate, .ripemdTouchLatch, .pricing, .run, .executionGasClear, .stateGasRestore, .handleRevert, .snapshotRestore, .returndata, .stackFailure]
    | .success => [.transferLog, .accountTouchOrCreate, .ripemdTouchLatch, .pricing, .run, .childRefund, .handleReturn, .returndata, .childCommit, .stackResult]

def directEffects (outcome : DirectFrameOutcome) : List Effect :=
  match outcome with
  | .pricingHardFailure => [.pricing, .stateGasRestore, .returnDataClear, .stackFailure]
  | .returnedLeafHardFailure => [.pricing, .run, .executionGasClear, .stateGasRestore, .returnDataClear, .stackFailure]
  | .managedNestedSoftRevert => [.pricing, .run, .executionGasClear, .stateGasRestore, .returnDataClear, .stackFailure]
  | .outputCopyOutOfGas => [.pricing, .run, .accountTouchOrCreate, .childRefund, .returndata, .returnOutOfGas]
  | .success => [.pricing, .run, .accountTouchOrCreate, .childRefund, .returndata, .outputCopy, .stackResult]

def fullFrameResidue (actionTracing : Bool) (outcome : FrameOutcome) : FrameResidue :=
  match outcome with
  | .pricingHardFailure => { effects := fullFrameEffects actionTracing outcome, runsLeaf := false, touchesAccount := true, returnsRefund := false, clearsReturnData := true, pushesSuccess := false, restoresSnapshot := true }
  | .returnedLeafHardFailure => { effects := fullFrameEffects actionTracing outcome, runsLeaf := true, touchesAccount := true, returnsRefund := false, clearsReturnData := true, pushesSuccess := false, restoresSnapshot := true }
  | .managedNestedSoftRevert => { effects := fullFrameEffects actionTracing outcome, runsLeaf := true, touchesAccount := true, returnsRefund := false, clearsReturnData := false, pushesSuccess := false, restoresSnapshot := true }
  | .success => { effects := fullFrameEffects actionTracing outcome, runsLeaf := true, touchesAccount := true, returnsRefund := true, clearsReturnData := false, pushesSuccess := true, restoresSnapshot := false }

structure DirectFrameResidue where
  result : DirectResult
  effects : List Effect
  runsLeaf : Bool
  touchesAccount : Bool
  returnsRefund : Bool
  clearsReturnData : Bool
  pushesSuccess : Bool
  restoresSnapshot : Bool

def directResidue (outcome : DirectFrameOutcome) : DirectFrameResidue :=
  match outcome with
  | .pricingHardFailure => { result := .stackFailure, effects := directEffects outcome, runsLeaf := false, touchesAccount := false, returnsRefund := false, clearsReturnData := true, pushesSuccess := false, restoresSnapshot := false }
  | .returnedLeafHardFailure => { result := .stackFailure, effects := directEffects outcome, runsLeaf := true, touchesAccount := false, returnsRefund := false, clearsReturnData := true, pushesSuccess := false, restoresSnapshot := false }
  | .managedNestedSoftRevert => { result := .stackFailure, effects := directEffects outcome, runsLeaf := true, touchesAccount := false, returnsRefund := false, clearsReturnData := true, pushesSuccess := false, restoresSnapshot := false }
  | .outputCopyOutOfGas => { result := .outOfGas, effects := directEffects outcome, runsLeaf := true, touchesAccount := true, returnsRefund := true, clearsReturnData := false, pushesSuccess := false, restoresSnapshot := false }
  | .success => { result := .stackSuccess, effects := directEffects outcome, runsLeaf := true, touchesAccount := true, returnsRefund := true, clearsReturnData := false, pushesSuccess := true, restoresSnapshot := false }

inductive TrySaveFailureEffect where
  | returnFalse
  | noUpdateSize
  | noSaveAfterGas
  | noMemoryMutation
  | noOutputWrite
  deriving DecidableEq, Repr

structure TrySaveBounds where
  sourcePath : String
  owner : String
  member : String
  validationHelper : String
  failureFlag : String
  failureEffects : List TrySaveFailureEffect
  boundsFailurePrecedesMutation : Bool

def trySaveBounds : TrySaveBounds :=
  { sourcePath := "src/Nethermind/Nethermind.Evm/EvmPooledMemory.cs", owner := "EvmPooledMemory", member := "TrySave",
    validationHelper := "CheckMemoryAccessViolation", failureFlag := "isViolation",
    failureEffects := [.returnFalse, .noUpdateSize, .noSaveAfterGas, .noMemoryMutation, .noOutputWrite], boundsFailurePrecedesMutation := true }

structure LeafBinding where
  name : String
  address : Nat
  activation : Activation
  declaration : String
  standardImplementations : List String
  oracleModule : String
  oracleNamespace : String
  oracleEntrySymbol : String
  oracleSymbol : String
  oracleOnly : Bool
  deriving Repr

def providerRegistry : List (String × Nat) :=
  [
    ("ECRECOVER", 1),
    ("SHA256", 2),
    ("RIPEMD160", 3),
    ("IDENTITY", 4),
    ("BN254_ADD", 6),
    ("BN254_MUL", 7),
    ("BN254_PAIRING", 8),
    ("MODEXP", 5),
    ("BLAKE2F", 9),
    ("BLS12_G1ADD", 11),
    ("BLS12_G1MSM", 12),
    ("BLS12_G2ADD", 13),
    ("BLS12_G2MSM", 14),
    ("BLS12_PAIRING", 15),
    ("BLS12_MAP_FP_TO_G1", 16),
    ("BLS12_MAP_FP2_TO_G2", 17),
    ("KZG_POINT_EVALUATION", 10),
    ("P256VERIFY", 256),
  ]

def leafBindings : List LeafBinding :=
  [
    { name := "ECRECOVER", address := 1, activation := .always,
      declaration := "src/Nethermind/Nethermind.Evm.Precompiles/ECRecoverPrecompile.cs", standardImplementations := [],
      oracleModule := "Ecrecover", oracleNamespace := "Eip803x.Precompiles.Ecrecover", oracleEntrySymbol := "run", oracleSymbol := "EthereumEcdsa.RecoverAddressRaw", oracleOnly := true },
    { name := "SHA256", address := 2, activation := .always,
      declaration := "src/Nethermind/Nethermind.Evm.Precompiles/Sha256Precompile.cs", standardImplementations := ["std/Sha256Precompile.cs"],
      oracleModule := "Sha256", oracleNamespace := "Eip803x.Precompiles.Sha256", oracleEntrySymbol := "run", oracleSymbol := "SHA256.TryHashData", oracleOnly := true },
    { name := "RIPEMD160", address := 3, activation := .always,
      declaration := "src/Nethermind/Nethermind.Evm.Precompiles/Ripemd160Precompile.cs", standardImplementations := ["std/Ripemd160Precompile.cs"],
      oracleModule := "Ripemd160", oracleNamespace := "Eip803x.Precompiles.Ripemd160", oracleEntrySymbol := "run", oracleSymbol := "Ripemd.Compute", oracleOnly := true },
    { name := "IDENTITY", address := 4, activation := .always,
      declaration := "src/Nethermind/Nethermind.Evm.Precompiles/IdentityPrecompile.cs", standardImplementations := [],
      oracleModule := "Identity", oracleNamespace := "Eip803x.Precompiles.Identity", oracleEntrySymbol := "run", oracleSymbol := "inputData.ToArray", oracleOnly := true },
    { name := "MODEXP", address := 5, activation := .eip198,
      declaration := "src/Nethermind/Nethermind.Evm.Precompiles/ModExpPrecompile.cs", standardImplementations := ["std/ModExpPrecompile.cs", "ModExpPrecompilePreEip2565.std.cs"],
      oracleModule := "ModExp", oracleNamespace := "Eip803x.Precompiles.ModExp", oracleEntrySymbol := "run", oracleSymbol := "Gmp.mpz_powm", oracleOnly := true },
    { name := "BN254_ADD", address := 6, activation := .eip196And197,
      declaration := "src/Nethermind/Nethermind.Evm.Precompiles/BN254AddPrecompile.cs", standardImplementations := ["std/BN254AddPrecompile.cs"],
      oracleModule := "Bn254Add", oracleNamespace := "Eip803x.Precompiles.Bn254Add", oracleEntrySymbol := "run", oracleSymbol := "BN254.Add", oracleOnly := true },
    { name := "BN254_MUL", address := 7, activation := .eip196And197,
      declaration := "src/Nethermind/Nethermind.Evm.Precompiles/BN254MulPrecompile.cs", standardImplementations := ["std/BN254MulPrecompile.cs"],
      oracleModule := "Bn254Mul", oracleNamespace := "Eip803x.Precompiles.Bn254Mul", oracleEntrySymbol := "run", oracleSymbol := "BN254.Mul", oracleOnly := true },
    { name := "BN254_PAIRING", address := 8, activation := .eip196And197,
      declaration := "src/Nethermind/Nethermind.Evm.Precompiles/BN254PairingCheckPrecompile.cs", standardImplementations := ["std/BN254PairingCheckPrecompile.cs"],
      oracleModule := "Bn254Pairing", oracleNamespace := "Eip803x.Precompiles.Bn254Pairing", oracleEntrySymbol := "run", oracleSymbol := "BN254.CheckPairing", oracleOnly := true },
    { name := "BLAKE2F", address := 9, activation := .eip152,
      declaration := "src/Nethermind/Nethermind.Evm.Precompiles/Blake2FPrecompile.cs", standardImplementations := ["std/Blake2FPrecompile.cs"],
      oracleModule := "Blake2F", oracleNamespace := "Eip803x.Precompiles.Blake2F", oracleEntrySymbol := "run", oracleSymbol := "Blake2Compression::_blake.Compress", oracleOnly := true },
    { name := "KZG_POINT_EVALUATION", address := 10, activation := .eip4844,
      declaration := "src/Nethermind/Nethermind.Evm.Precompiles/KzgPointEvaluationPrecompile.cs", standardImplementations := ["std/KzgPointEvaluationPrecompile.cs"],
      oracleModule := "KzgPointEvaluation", oracleNamespace := "Eip803x.Precompiles.KzgPointEvaluation", oracleEntrySymbol := "run", oracleSymbol := "KzgPolynomialCommitments.VerifyProof", oracleOnly := true },
    { name := "BLS12_G1ADD", address := 11, activation := .eip2537,
      declaration := "src/Nethermind/Nethermind.Evm.Precompiles/Bls12381G1AddPrecompile.cs", standardImplementations := ["std/Bls12381G1AddPrecompile.cs"],
      oracleModule := "Bls12381G1Add", oracleNamespace := "Eip803x.Precompiles.Bls12381G1Add", oracleEntrySymbol := "run", oracleSymbol := "Nethermind.Crypto.Bls.P1::Add", oracleOnly := true },
    { name := "BLS12_G1MSM", address := 12, activation := .eip2537,
      declaration := "src/Nethermind/Nethermind.Evm.Precompiles/Bls12381G1MsmPrecompile.cs", standardImplementations := ["std/Bls12381G1MsmPrecompile.cs"],
      oracleModule := "Bls12381G1Msm", oracleNamespace := "Eip803x.Precompiles.Bls12381G1Msm", oracleEntrySymbol := "run", oracleSymbol := "Nethermind.Crypto.Bls.P1::MultiMultAffine", oracleOnly := true },
    { name := "BLS12_G2ADD", address := 13, activation := .eip2537,
      declaration := "src/Nethermind/Nethermind.Evm.Precompiles/Bls12381G2AddPrecompile.cs", standardImplementations := ["std/Bls12381G2AddPrecompile.cs"],
      oracleModule := "Bls12381G2Add", oracleNamespace := "Eip803x.Precompiles.Bls12381G2Add", oracleEntrySymbol := "run", oracleSymbol := "Nethermind.Crypto.Bls.P2::Add", oracleOnly := true },
    { name := "BLS12_G2MSM", address := 14, activation := .eip2537,
      declaration := "src/Nethermind/Nethermind.Evm.Precompiles/Bls12381G2MsmPrecompile.cs", standardImplementations := ["std/Bls12381G2MsmPrecompile.cs"],
      oracleModule := "Bls12381G2Msm", oracleNamespace := "Eip803x.Precompiles.Bls12381G2Msm", oracleEntrySymbol := "run", oracleSymbol := "Nethermind.Crypto.Bls.P2::MultiMultAffine", oracleOnly := true },
    { name := "BLS12_PAIRING", address := 15, activation := .eip2537,
      declaration := "src/Nethermind/Nethermind.Evm.Precompiles/Bls12381PairingCheckPrecompile.cs", standardImplementations := ["std/Bls12381PairingCheckPrecompile.cs"],
      oracleModule := "Bls12381Pairing", oracleNamespace := "Eip803x.Precompiles.Bls12381Pairing", oracleEntrySymbol := "run", oracleSymbol := "Nethermind.Crypto.Bls.PT::MillerLoopN", oracleOnly := true },
    { name := "BLS12_MAP_FP_TO_G1", address := 16, activation := .eip2537,
      declaration := "src/Nethermind/Nethermind.Evm.Precompiles/Bls12381FpToG1Precompile.cs", standardImplementations := ["std/Bls12381FpToG1Precompile.cs"],
      oracleModule := "Bls12381FpToG1", oracleNamespace := "Eip803x.Precompiles.Bls12381FpToG1", oracleEntrySymbol := "run", oracleSymbol := "Nethermind.Crypto.Bls.P1::MapTo", oracleOnly := true },
    { name := "BLS12_MAP_FP2_TO_G2", address := 17, activation := .eip2537,
      declaration := "src/Nethermind/Nethermind.Evm.Precompiles/Bls12381Fp2ToG2Precompile.cs", standardImplementations := ["std/Bls12381Fp2ToG2Precompile.cs"],
      oracleModule := "Bls12381Fp2ToG2", oracleNamespace := "Eip803x.Precompiles.Bls12381Fp2ToG2", oracleEntrySymbol := "run", oracleSymbol := "Nethermind.Crypto.Bls.P2::MapTo", oracleOnly := true },
    { name := "P256VERIFY", address := 256, activation := .eip7212Or7951,
      declaration := "src/Nethermind/Nethermind.Evm.Precompiles/SecP256r1Precompile.cs", standardImplementations := ["std/SecP256r1Precompile.cs"],
      oracleModule := "P256Verify", oracleNamespace := "Eip803x.Precompiles.P256Verify", oracleEntrySymbol := "run", oracleSymbol := "SecP256r1.VerifySignature", oracleOnly := true },
  ]

structure WorldInput where
  accountRead : Bool
  accountAccess : Bool
  snapshotTaken : Bool
  ripemdLatch : Bool
  deriving Repr

structure WorldOutput where
  accountTouched : Bool
  journalCommitted : Bool
  journalRestored : Bool
  ripemdRestored : Bool
  refundReturned : Bool
  deriving Repr

def WorldJournalRelation := WorldInput → WorldOutput → Prop
def composeWorld (journal : WorldJournalRelation) (input : WorldInput) (output : WorldOutput) : Prop :=
  journal input output
def worldCompositionGateOpen : Bool := true

def standardBuild : Bool := true
def lowLookupMaximum : Nat := 0x100
def noDelegationForActivePrecompile : Bool := true
def directRipemdExcluded : Bool := true
def pricingPrecedesRun : Bool := true

end Eip803x.Generated.PrecompileFrameStageA

