-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import OrdinaryStatefulAdmissionPrefixExtractor.Generated.OrdinaryStatefulAdmissionPrefix

/-!
This is deliberately handwritten rather than generated. It has its own transition decomposition
and observation type, while sharing the finite input and state vocabulary so the refinement can
compare every intermediate mutation without an adapter loss. It never calls the generated runner.
-/

namespace OrdinaryStatefulAdmissionPrefixExtractor.Reference

open OrdinaryStatefulAdmissionPrefixExtractor.Generated

abbrev Input := OrdinaryStatefulAdmissionPrefixExtractor.Generated.Input
abbrev Outcome := OrdinaryStatefulAdmissionPrefixExtractor.Generated.Outcome
abbrev ReturnSite := OrdinaryStatefulAdmissionPrefixExtractor.Generated.ReturnSite
abbrev ErrorType := OrdinaryStatefulAdmissionPrefixExtractor.Generated.ErrorType
abbrev ThrowSite := OrdinaryStatefulAdmissionPrefixExtractor.Generated.ThrowSite
abbrev Stage := OrdinaryStatefulAdmissionPrefixExtractor.Generated.Stage
abbrev LogEvent := OrdinaryStatefulAdmissionPrefixExtractor.Generated.LogEvent
abbrev TransactionState := OrdinaryStatefulAdmissionPrefixExtractor.Generated.TransactionState
abbrev PartialWorldState := OrdinaryStatefulAdmissionPrefixExtractor.Generated.PartialWorldState
abbrev MetricsState := OrdinaryStatefulAdmissionPrefixExtractor.Generated.MetricsState
abbrev JournalState := OrdinaryStatefulAdmissionPrefixExtractor.Generated.JournalState

/-! The reference owns this terminal vocabulary rather than treating the generated runner's
    constructors as its semantic API. The shared `Outcome` carrier remains the deliberately
    explicit comparison boundary; `terminalVocabulary` records the independently named view. -/
inductive TerminalVocabulary where
  | continued
  | returned
  | threw
  deriving DecidableEq, Repr

def terminalVocabulary (outcome : Outcome) : TerminalVocabulary :=
  match outcome with
  | .continue => .continued
  | .returned _ _ => .returned
  | .threw _ => .threw

structure ReferenceState where
  transaction : TransactionState
  world : PartialWorldState
  metrics : MetricsState
  journal : JournalState
  effectiveGasPrice : Nat
  opcodeGasPrice : Nat
  premiumPerGas : Nat
  senderReservedGasPayment : Nat
  blobBaseFee : Nat
  deleteCallerAccount : Bool
  logs : List LogEvent
  trace : List Stage
  deriving DecidableEq, Repr

structure Observation where
  outcome : Outcome
  transaction : TransactionState
  world : PartialWorldState
  metrics : MetricsState
  journal : JournalState
  effectiveGasPrice : Nat
  opcodeGasPrice : Nat
  premiumPerGas : Nat
  senderReservedGasPayment : Nat
  blobBaseFee : Nat
  deleteCallerAccount : Bool
  logs : List LogEvent
  trace : List Stage
  deriving DecidableEq, Repr

def Observation.terminalVocabulary (observation : Observation) : TerminalVocabulary :=
  OrdinaryStatefulAdmissionPrefixExtractor.Reference.terminalVocabulary observation.outcome

inductive RecoveryResult where
  | continue (state : ReferenceState)
  | threw (state : ReferenceState) (site : ThrowSite)
  deriving DecidableEq, Repr

inductive RecoveryDisposition where
  | knownSender
  | recoveredSender (sender : Option Nat)
  | absentSender (createAccount : Bool)
  deriving DecidableEq, Repr

def completeRecovery (state : ReferenceState) : RecoveryResult :=
  if state.transaction.sender.isSome then .continue state else .threw state .recoverSender

def uint64Modulus : Nat := 2 ^ 64
def uint64Max : Nat := uint64Modulus - 1
def uint256Modulus : Nat := 2 ^ 256
def gasPerBlob : Nat := 131072
def asU64 (value : Nat) : Nat := value % uint64Modulus
def asU256 (value : Nat) : Nat := value % uint256Modulus
def nextU64 (left right : Nat) : Nat := asU64 (asU64 left + asU64 right)
structure CheckedUInt256 where
  value : Nat
  overflow : Bool
  deriving DecidableEq, Repr
def addU256 (left right : Nat) : CheckedUInt256 :=
  let total := asU256 left + asU256 right
  { value := asU256 total, overflow := !(total < uint256Modulus) }
def multiplyU256 (left right : Nat) : CheckedUInt256 :=
  let total := asU256 left * asU256 right
  { value := asU256 total, overflow := !(total < uint256Modulus) }

def optionFlag (raw flag : Nat) : Bool := ((raw / flag) % 2) == 1
def skipsChecks (input : Input) : Bool := optionFlag input.options.raw executionOptionSkipValidation
def asksRestore (input : Input) : Bool := optionFlag input.options.raw executionOptionRestore
def isWarmup (input : Input) : Bool := optionFlag input.options.raw executionOptionWarmup
def recoveryCommits (input : Input) : Bool :=
  optionFlag input.options.raw executionOptionCommit || !input.spec.eip658Enabled
def validatesGas (input : Input) : Bool :=
  !skipsChecks input || asU256 input.tx.maxFeePerGas != 0 || asU256 input.tx.maxPriorityFeePerGas != 0
def recoveryCandidate (input : Input) : Option Nat :=
  if input.tx.signaturePresent && (!input.spec.eip2780Enabled || !input.tx.isMessageCall) then input.recoveredSender
  else input.tx.sender

def start (input : Input) : ReferenceState :=
  { transaction := { sender := input.tx.sender }
    world := { effectiveSenderAccountExists := input.world.effectiveSenderAccountExists
               effectiveSenderBalance := asU256 input.world.effectiveSenderBalance
               effectiveSenderNonce := asU64 input.world.effectiveSenderNonce
               accountCreated := false }
    metrics := { blockGasPrices := [] }
    journal := { resetCalled := false, resetBlockChangesFalse := false }
    effectiveGasPrice := 0
    opcodeGasPrice := 0
    premiumPerGas := 0
    senderReservedGasPayment := 0
    blobBaseFee := 0
    deleteCallerAccount := false
    logs := []
    trace := [] }

def enter (state : ReferenceState) (stage : Stage) : ReferenceState :=
  { state with trace := state.trace ++ [stage] }
def note (state : ReferenceState) (event : LogEvent) : ReferenceState :=
  { state with logs := state.logs ++ [event] }

def installRecoveredSender (state : ReferenceState) (sender : Option Nat) : ReferenceState :=
  { state with transaction := { sender := sender } }

def provisionRecoveredAccount (input : Input) (state : ReferenceState) (deleteCallerAccount : Bool) : ReferenceState :=
  if input.world.standardMainnetWorldState then
    { state with world := { effectiveSenderAccountExists := true
                            effectiveSenderBalance := 0
                            effectiveSenderNonce := 0
                            accountCreated := true }
                 deleteCallerAccount := deleteCallerAccount }
  else
    { state with world := { state.world with effectiveSenderAccountExists := true, accountCreated := true }
                 deleteCallerAccount := deleteCallerAccount }

def blobFeeCap (tx : Transaction) : Nat :=
  tx.maxFeePerBlobGas.getD 0

def blobHashCount (tx : Transaction) : Nat :=
  match tx.blobVersionedHashes with
  | none => 0
  | some hashes => hashes.length

def blobGas (tx : Transaction) : Nat :=
  asU64 (asU64 (blobHashCount tx) * gasPerBlob)

def observe (outcome : Outcome) (state : ReferenceState) : Observation :=
  { outcome := outcome
    transaction := state.transaction
    world := state.world
    metrics := state.metrics
    journal := state.journal
    effectiveGasPrice := state.effectiveGasPrice
    opcodeGasPrice := state.opcodeGasPrice
    premiumPerGas := state.premiumPerGas
    senderReservedGasPayment := state.senderReservedGasPayment
    blobBaseFee := state.blobBaseFee
    deleteCallerAccount := state.deleteCallerAccount
    logs := state.logs
    trace := state.trace }

def finalReturn (input : Input) (state : ReferenceState) (site : ReturnSite) (error : ErrorType) : Observation :=
  let state :=
    if asksRestore input then
      { state with journal := { resetCalled := true, resetBlockChangesFalse := true } }
    else state
  observe (.returned site error) state

def priceForExecution (input : Input) : Nat :=
  if !input.spec.eip1559Enabled then asU256 input.tx.maxPriorityFeePerGas else
  let effectiveFee := addU256 (asU256 input.tx.maxPriorityFeePerGas) (asU256 input.header.baseFeePerGas)
  if effectiveFee.overflow then asU256 input.tx.maxFeePerGas
  else Nat.min (asU256 input.tx.maxFeePerGas) effectiveFee.value

def minerPremium (input : Input) : Option Nat :=
  let feeCap := if input.tx.supports1559 then asU256 input.tx.maxFeePerGas else asU256 input.tx.maxPriorityFeePerGas
  if asU256 input.header.baseFeePerGas > feeCap then
    if input.tx.isFree then some 0 else none
  else
    some (Nat.min (asU256 input.tx.maxPriorityFeePerGas) (feeCap - asU256 input.header.baseFeePerGas))

def classifySenderRecovery (input : Input) (state : ReferenceState) : RecoveryDisposition :=
  let sender := state.transaction.sender
  if sender.isSome && input.world.suppliedSenderAccountExists then .knownSender else
  let recovered := recoveryCandidate input
  if sender != recovered then .recoveredSender recovered
  else .absentSender (!recoveryCommits input || skipsChecks input || state.effectiveGasPrice == 0)

def executeSenderRecovery
    (input : Input) (state : ReferenceState) (disposition : RecoveryDisposition) : RecoveryResult :=
  match disposition with
  | .knownSender => .continue state
  | .recoveredSender recovered =>
    let state := if input.logger.debugEnabled then note state .recoveryAttempt else state
    let state := installRecoveredSender state recovered
    let state := if input.logger.warnEnabled then note state .recoveryChangedSender else state
    completeRecovery state
  | .absentSender createAccount =>
    let state := if input.logger.debugEnabled then note state .recoveryAttempt else state
    let state := note state .senderAccountDoesNotExist
    let state :=
      if createAccount then
        if input.world.createAccountSucceeds then
          provisionRecoveredAccount input state (!recoveryCommits input || asksRestore input)
        else state
      else state
    if !input.world.createAccountSucceeds && createAccount then
      .threw state .createAccount
    else completeRecovery state

def recover (input : Input) (state : ReferenceState) : RecoveryResult :=
  executeSenderRecovery input state (classifySenderRecovery input state)

def settleBalance (input : Input) (state : ReferenceState) (balanceCheck : Nat) : Sum Observation ReferenceState :=
  if state.world.effectiveSenderBalance < balanceCheck then
    if isWarmup input then
      let warmCharge := Nat.min state.senderReservedGasPayment state.world.effectiveSenderBalance
      let state := { state with world := { state.world with effectiveSenderBalance := state.world.effectiveSenderBalance - warmCharge } }
      .inr state
    else
      .inl (finalReturn input (note state .insufficientBalance)
        .buyGasInsufficientBalance .insufficientMaxFeePerGasForSenderBalance)
  else
    let state := { state with world :=
      { state.world with effectiveSenderBalance := state.world.effectiveSenderBalance - state.senderReservedGasPayment } }
    .inr state

def settleReservedWithBlob (input : Input) (state : ReferenceState)
    (balanceAfterBlobCap : Nat) : Sum Observation ReferenceState :=
  let reservedWithBlob := addU256 state.senderReservedGasPayment state.blobBaseFee
  let state := { state with senderReservedGasPayment := reservedWithBlob.value }
  if reservedWithBlob.overflow then
    .inl (finalReturn input (note state .blobPaymentOverflow)
      .buyGasBlobPaymentOverflow .insufficientMaxFeePerGasForSenderBalance)
  else
    settleBalance input state balanceAfterBlobCap

def recordBlobBaseFee (state : ReferenceState) (feeCalculationSucceeds : Bool) (baseFee : Nat) : ReferenceState :=
  if !feeCalculationSucceeds then
    { state with blobBaseFee := 0 }
  else { state with blobBaseFee := asU256 baseFee }

def assessBlobFeeCalculation (input : Input) (state : ReferenceState)
    (balanceAfterBlobCap : Nat) : Sum Observation ReferenceState :=
  let state := recordBlobBaseFee state input.blobOracle.feePerBlobGasCalculationSucceeds input.blobOracle.blobBaseFee
  if !input.blobOracle.feePerBlobGasCalculationSucceeds ||
      !input.blobOracle.blobBaseFeeCalculationSucceeds then
    .inl (finalReturn input (note state .blobFeeCalculationOverflow)
      .buyGasBlobFeeCalculationOverflow .insufficientMaxFeePerGasForSenderBalance)
  else if asU256 (blobFeeCap input.tx) < asU256 input.blobOracle.feePerBlobGas then
    .inl (finalReturn input (note state .blobFeeCapBelowBaseFee)
      .buyGasBlobFeeCapBelowBaseFee .insufficientSenderBalance)
  else
    settleReservedWithBlob input state balanceAfterBlobCap

def assessBlobCapacity (input : Input) (state : ReferenceState)
    (balanceAfterValue maximumBlobFee : Nat) : Sum Observation ReferenceState :=
  let balanceAfterBlobCap := addU256 balanceAfterValue maximumBlobFee
  if balanceAfterBlobCap.overflow then
    .inl (finalReturn input (note state .blobMaximumFeeOverflow)
      .buyGasBlobMaximumFeeOverflow .insufficientMaxFeePerGasForSenderBalance)
  else
    assessBlobFeeCalculation input state balanceAfterBlobCap.value

def assessMaximumBlobFee (input : Input) (state : ReferenceState)
    (balanceAfterValue : Nat) : Sum Observation ReferenceState :=
  let maximumBlobFee := multiplyU256 (blobGas input.tx) (blobFeeCap input.tx)
  if maximumBlobFee.overflow then
    .inl (finalReturn input (note state .blobMaximumFeeOverflow)
      .buyGasBlobMaximumFeeOverflow .insufficientMaxFeePerGasForSenderBalance)
  else
    assessBlobCapacity input state balanceAfterValue maximumBlobFee.value

def assessBalanceAfterValue (input : Input) (state : ReferenceState)
    (maximumBalance : Nat) : Sum Observation ReferenceState :=
  let balanceAfterValue := addU256 maximumBalance input.tx.value
  if balanceAfterValue.overflow then
    .inl (finalReturn input (note state .valueOverflow)
      .buyGasValueOverflow .insufficientMaxFeePerGasForSenderBalance)
  else if !input.tx.supportsBlobs then
    settleBalance input state balanceAfterValue.value
  else if input.tx.maxFeePerBlobGas.isNone then
    .inl (observe (.threw .malformedBlobFields) state)
  else
    assessMaximumBlobFee input state balanceAfterValue.value

def assessMaximumFee (input : Input) (state : ReferenceState) : Sum Observation ReferenceState :=
  let maximumBalanceCheck :=
    if input.spec.eip1559Enabled && !input.tx.isFree then
      multiplyU256 (asU64 input.tx.gasLimit) input.tx.maxFeePerGas
    else
      { value := state.senderReservedGasPayment, overflow := false }
  if maximumBalanceCheck.overflow then
    .inl (finalReturn input (note state .maximumFeeOverflow)
      .buyGasMaximumFeeOverflow .insufficientMaxFeePerGasForSenderBalance)
  else
    assessBalanceAfterValue input state maximumBalanceCheck.value

def settleReservedPayment (input : Input) (state : ReferenceState)
    (reservedValue : Nat) (reservedOverflow : Bool) : Sum Observation ReferenceState :=
  let state := { state with senderReservedGasPayment := reservedValue }
  if reservedOverflow then
    .inl (finalReturn input (note state .reservedPaymentOverflow)
      .buyGasReservedPaymentOverflow .insufficientMaxFeePerGasForSenderBalance)
  else
    assessMaximumFee input state

def settleFeeReservation (input : Input) (state : ReferenceState) : Sum Observation ReferenceState :=
  let reservedPayment := multiplyU256 (asU64 input.tx.gasLimit) state.effectiveGasPrice
  settleReservedPayment input state reservedPayment.value reservedPayment.overflow

def chargeGas (input : Input) (state : ReferenceState) : Sum Observation ReferenceState :=
  let state := { state with premiumPerGas := 0, senderReservedGasPayment := 0, blobBaseFee := 0 }
  let state := enter state .buyGas
  if validatesGas input then
    let premium := minerPremium input
    let state := { state with premiumPerGas := premium.getD 0 }
    if validatesGas input && !premium.isSome then
      .inl (finalReturn input (note state .premiumBelowBaseFee)
        .buyGasPremiumBelowBaseFee .maxFeePerGasBelowBaseFee)
    else
      settleFeeReservation input state
  else
    settleFeeReservation input state

def advanceNonce (input : Input) (state : ReferenceState) : Sum Observation ReferenceState :=
  let state := enter state .incrementNonce
  let validate := !skipsChecks input
  if validate && asU64 input.tx.nonce != state.world.effectiveSenderNonce then
    if asU64 input.tx.nonce > state.world.effectiveSenderNonce then
      .inl (finalReturn input (note state .nonceMismatch)
        .incrementNonceTooHigh .transactionNonceTooHigh)
    else
      .inl (finalReturn input (note state .nonceMismatch)
        .incrementNonceTooLow .transactionNonceTooLow)
  else
    let newNonce := if validate || state.world.effectiveSenderNonce < uint64Max then nextU64 state.world.effectiveSenderNonce 1 else 0
    if !state.world.effectiveSenderAccountExists then
      .inl (observe (.threw .setNonceAbsentAccount) state)
    else
      .inr { state with world := { state.world with effectiveSenderNonce := newNonce } }

def finishAfterGasCharge (input : Input) (result : Sum Observation ReferenceState) : Observation :=
  match result with
  | .inl observation => observation
  | .inr state =>
    match advanceNonce input state with
    | .inl observation => observation
    | .inr state => observe .continue state

def continueAfterRecovery (input : Input) (state : ReferenceState) : Observation :=
  let state := enter state .validateSender
  if !skipsChecks input && !input.skipSenderCodeCheck && input.world.effectiveSenderInvalidContract then
    finalReturn input (note state .invalidContractSender)
      .validateSender .senderHasDeployedCode
  else
    finishAfterGasCharge input (chargeGas input state)

def finishAfterRecovery (input : Input) (result : RecoveryResult) : Observation :=
  match result with
  | .threw state site => observe (.threw site) state
  | .continue state => continueAfterRecovery input state

def run (input : Input) : Observation :=
  let state := enter (start input) .validateStatic
  if !(input.staticError == .none) then
    observe (.returned .validateStatic input.staticError) state
  else
    let state := enter state .calculateEffectiveGasPrice
    let effectiveGasPrice := priceForExecution input
    let state := { state with effectiveGasPrice := effectiveGasPrice, opcodeGasPrice := effectiveGasPrice }
    let state := enter state .updateMetrics
    let state :=
      if input.options.raw == executionOptionCommit || input.options.raw == 0 then
        { state with metrics := { blockGasPrices := [effectiveGasPrice] } }
      else state
    let state := enter state .recoverSenderIfNeeded
    finishAfterRecovery input (recover input state)

end OrdinaryStatefulAdmissionPrefixExtractor.Reference
