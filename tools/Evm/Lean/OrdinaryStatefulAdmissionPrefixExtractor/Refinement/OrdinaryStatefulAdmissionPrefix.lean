-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import OrdinaryStatefulAdmissionPrefixExtractor.Generated.OrdinaryStatefulAdmissionPrefix
import OrdinaryStatefulAdmissionPrefixExtractor.Reference.OrdinaryStatefulAdmissionPrefixReference

/-!
The refinement maps the source-bound generated admission prefix to an independently structured
reference state and observation boundary. Each production-order segment is simulated compositionally.
-/

namespace OrdinaryStatefulAdmissionPrefixExtractor.Refinement

open OrdinaryStatefulAdmissionPrefixExtractor

private def mapState (state : Reference.ReferenceState) : Generated.AdmissionState :=
  { transaction := state.transaction
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

private def mapResult (result : Reference.RecoveryResult) : Generated.RecoveryResult :=
  match result with
  | .continue state => .continue (mapState state)
  | .threw state site => .threw (mapState state) site

private def mapObservation (observation : Reference.Observation) : Generated.Output :=
  { outcome := observation.outcome
    transaction := observation.transaction
    world := observation.world
    metrics := observation.metrics
    journal := observation.journal
    effectiveGasPrice := observation.effectiveGasPrice
    opcodeGasPrice := observation.opcodeGasPrice
    premiumPerGas := observation.premiumPerGas
    senderReservedGasPayment := observation.senderReservedGasPayment
    blobBaseFee := observation.blobBaseFee
    deleteCallerAccount := observation.deleteCallerAccount
    logs := observation.logs
    trace := observation.trace }

private def mapChargeResult
    (result : Sum Reference.Observation Reference.ReferenceState) : Sum Generated.Output Generated.AdmissionState :=
  match result with
  | .inl observation => .inl (mapObservation observation)
  | .inr state => .inr (mapState state)

private def mapDisposition (value : Reference.RecoveryDisposition) : Generated.RecoveryDecision :=
  match value with
  | .knownSender => .fast
  | .recoveredSender sender => .changed sender
  | .absentSender create => .same create

@[simp] private theorem map_note (state : Reference.ReferenceState) (event : Generated.LogEvent) :
    mapState (Reference.note state event) = Generated.appendLog (mapState state) event := by rfl

@[simp] private theorem map_enter (state : Reference.ReferenceState) (stage : Generated.Stage) :
    mapState (Reference.enter state stage) = Generated.appendStage (mapState state) stage := by rfl

@[simp] private theorem map_start (input : Generated.Input) :
    mapState (Reference.start input) = Generated.initialState input := by
  simp [Reference.start, Generated.initialState, mapState, Reference.asU64, Generated.u64,
    Reference.asU256, Generated.u256, Reference.uint64Modulus, Generated.uint64Modulus,
    Reference.uint256Modulus, Generated.uint256Modulus]

@[simp] private theorem map_observe (outcome : Generated.Outcome) (state : Reference.ReferenceState) :
    mapObservation (Reference.observe outcome state) = Generated.finish outcome (mapState state) := by rfl

@[simp] private theorem map_charge_inl (observation : Reference.Observation) :
    mapChargeResult (.inl observation) = Sum.inl (mapObservation observation) := by rfl

@[simp] private theorem map_charge_inr (state : Reference.ReferenceState) :
    mapChargeResult (.inr state) = Sum.inr (mapState state) := by rfl

@[simp] private theorem mapChargeResult_ite (condition : Bool)
    (onTrue onFalse : Sum Reference.Observation Reference.ReferenceState) :
    mapChargeResult (if condition then onTrue else onFalse) =
      if condition then mapChargeResult onTrue else mapChargeResult onFalse := by
  split <;> rfl

@[simp] private theorem mapChargeResult_iteProp (condition : Prop) [Decidable condition]
    (onTrue onFalse : Sum Reference.Observation Reference.ReferenceState) :
    mapChargeResult (if condition then onTrue else onFalse) =
      if condition then mapChargeResult onTrue else mapChargeResult onFalse := by
  by_cases h : condition <;> simp [h]

@[simp] private theorem mapState_ite (condition : Bool)
    (onTrue onFalse : Reference.ReferenceState) :
    mapState (if condition then onTrue else onFalse) =
      if condition then mapState onTrue else mapState onFalse := by
  split <;> rfl

private theorem map_finalReturn
    (input : Generated.Input) (state : Reference.ReferenceState)
    (site : Generated.ReturnSite) (error : Generated.ErrorType) :
    mapObservation (Reference.finalReturn input state site error) =
      Generated.rejectAfterCombinedGate input (mapState state) site error := by
  cases state
  by_cases hRestore : input.options.raw / Generated.executionOptionRestore % 2 == 1
  · simp [Reference.finalReturn, Generated.rejectAfterCombinedGate, Reference.asksRestore,
      Generated.restore, Reference.optionFlag, Generated.hasFlag, mapState, hRestore]
  · simp [Reference.finalReturn, Generated.rejectAfterCombinedGate, Reference.asksRestore,
      Generated.restore, Reference.optionFlag, Generated.hasFlag, mapState, hRestore]

@[simp] private theorem mapChargeResult_finalReturn
    (input : Generated.Input) (state : Reference.ReferenceState)
    (site : Generated.ReturnSite) (error : Generated.ErrorType) :
    mapChargeResult (.inl (Reference.finalReturn input state site error)) =
      Sum.inl (Generated.rejectAfterCombinedGate input (mapState state) site error) := by
  simp [mapChargeResult, map_finalReturn]

private theorem price_refines (input : Generated.Input) :
    Reference.priceForExecution input = Generated.calculateEffectiveGasPrice input := by
  unfold Reference.priceForExecution Generated.calculateEffectiveGasPrice Reference.addU256 Generated.checkedAdd256
  simp [Reference.asU256, Generated.u256, Reference.uint256Modulus, Generated.uint256Modulus] <;> omega

private theorem premium_refines (input : Generated.Input) :
    Reference.minerPremium input = Generated.tryCalculatePremiumPerGas input := by
  unfold Reference.minerPremium Generated.tryCalculatePremiumPerGas
  simp [Reference.asU256, Generated.u256, Reference.uint256Modulus, Generated.uint256Modulus] <;> rfl

@[simp] private theorem u64_refines (value : Nat) : Reference.asU64 value = Generated.u64 value := by
  unfold Reference.asU64 Generated.u64 Reference.uint64Modulus Generated.uint64Modulus
  rfl

@[simp] private theorem u256_refines (value : Nat) : Reference.asU256 value = Generated.u256 value := by
  unfold Reference.asU256 Generated.u256 Reference.uint256Modulus Generated.uint256Modulus
  rfl

@[simp] private theorem blobFeeCap_refines (tx : Generated.Transaction) :
    Reference.blobFeeCap tx = Generated.blobFeeCap tx := by rfl

@[simp] private theorem blobGas_refines (tx : Generated.Transaction) :
    Reference.blobGas tx = Generated.blobGas tx := by
  simp [Reference.blobGas, Generated.blobGas, Reference.blobHashCount, Generated.blobHashCount,
    Reference.gasPerBlob, Generated.gasPerBlob, Reference.asU64, Generated.u64,
    Reference.uint64Modulus, Generated.uint64Modulus] <;> rfl

@[simp] private theorem multiply_value_refines (left right : Nat) :
    (Reference.multiplyU256 left right).value = (Generated.checkedMul256 left right).value := by
  unfold Reference.multiplyU256 Generated.checkedMul256
  simp [Reference.asU256, Generated.u256, Reference.uint256Modulus, Generated.uint256Modulus] <;> rfl

@[simp] private theorem multiply_overflow_refines (left right : Nat) :
    (Reference.multiplyU256 left right).overflow = (Generated.checkedMul256 left right).overflow := by
  unfold Reference.multiplyU256 Generated.checkedMul256
  simp [Reference.asU256, Generated.u256, Reference.uint256Modulus, Generated.uint256Modulus] <;> rfl

@[simp] private theorem add_value_refines (left right : Nat) :
    (Reference.addU256 left right).value = (Generated.checkedAdd256 left right).value := by
  unfold Reference.addU256 Generated.checkedAdd256
  simp [Reference.asU256, Generated.u256, Reference.uint256Modulus, Generated.uint256Modulus] <;> rfl

@[simp] private theorem add_overflow_refines (left right : Nat) :
    (Reference.addU256 left right).overflow = (Generated.checkedAdd256 left right).overflow := by
  unfold Reference.addU256 Generated.checkedAdd256
  simp [Reference.asU256, Generated.u256, Reference.uint256Modulus, Generated.uint256Modulus] <;> rfl

@[simp] private theorem map_senderReserved (state : Reference.ReferenceState) (value : Nat) :
    mapState { state with senderReservedGasPayment := value } =
      { mapState state with senderReservedGasPayment := value } := by rfl

@[simp] private theorem map_blobBaseFee (state : Reference.ReferenceState) (value : Nat) :
    mapState { state with blobBaseFee := value } = { mapState state with blobBaseFee := value } := by rfl

@[simp] private theorem map_recordBlobBaseFee
    (state : Reference.ReferenceState) (feeCalculationSucceeds : Bool) (baseFee : Nat) :
    mapState (Reference.recordBlobBaseFee state feeCalculationSucceeds baseFee) =
      Generated.setBlobBaseFee (mapState state) feeCalculationSucceeds baseFee := by
  cases feeCalculationSucceeds <;>
    simp [Reference.recordBlobBaseFee, Generated.setBlobBaseFee, mapState, Reference.asU256,
      Generated.u256, Reference.uint256Modulus, Generated.uint256Modulus]

@[simp] private theorem map_buyGasValues (state : Reference.ReferenceState) (premium reserved blob : Nat) :
    mapState { state with premiumPerGas := premium, senderReservedGasPayment := reserved, blobBaseFee := blob } =
      { mapState state with premiumPerGas := premium, senderReservedGasPayment := reserved, blobBaseFee := blob } := by rfl

private theorem settleBalance_refines
    (input : Generated.Input) (state : Reference.ReferenceState) (balanceCheck : Nat) :
    mapChargeResult (Reference.settleBalance input state balanceCheck) =
      Generated.finishBuyGas input (mapState state) balanceCheck := by
  cases state
  unfold Reference.settleBalance Generated.finishBuyGas
  all_goals repeat' first
    | split
    | simp_all [mapChargeResult, mapObservation, mapState, Reference.isWarmup,
        Reference.optionFlag, Generated.hasFlag, Reference.note, Generated.appendLog,
        Reference.finalReturn, Generated.rejectAfterCombinedGate, Reference.asksRestore, Generated.restore,
        Reference.observe, Generated.finish, Generated.debitEffectiveSender]
    | omega

private theorem settleReservedWithBlob_refines
    (input : Generated.Input) (state : Reference.ReferenceState) (balanceAfterBlobCap : Nat) :
    mapChargeResult (Reference.settleReservedWithBlob input state balanceAfterBlobCap) =
      Generated.finishReservedWithBlob input (mapState state) balanceAfterBlobCap := by
  unfold Reference.settleReservedWithBlob Generated.finishReservedWithBlob
  simp only [mapChargeResult_ite, mapChargeResult_finalReturn]
  simp [mapState, Reference.addU256, Generated.checkedAdd256, Reference.asU256, Generated.u256,
    Reference.uint256Modulus, Generated.uint256Modulus, Reference.note, Generated.appendLog,
    settleBalance_refines] <;> rfl

private theorem assessBlobFeeCalculation_refines
    (input : Generated.Input) (state : Reference.ReferenceState) (balanceAfterBlobCap : Nat) :
    mapChargeResult (Reference.assessBlobFeeCalculation input state balanceAfterBlobCap) =
      Generated.finishBlobFeeCalculation input (mapState state) balanceAfterBlobCap := by
  unfold Reference.assessBlobFeeCalculation Generated.finishBlobFeeCalculation
  simp only [mapChargeResult_iteProp, mapChargeResult_finalReturn,
    map_recordBlobBaseFee, map_note]
  repeat' first | rw [mapChargeResult_ite] | rw [mapChargeResult_iteProp] | rw [mapChargeResult_finalReturn]
  rw [settleReservedWithBlob_refines]
  simp [map_recordBlobBaseFee, u256_refines, blobFeeCap_refines]

private theorem assessBlobCapacity_refines
    (input : Generated.Input) (state : Reference.ReferenceState)
    (balanceAfterValue maximumBlobFee : Nat) :
    mapChargeResult (Reference.assessBlobCapacity input state balanceAfterValue maximumBlobFee) =
      Generated.finishBalanceAfterBlobCap input (mapState state) balanceAfterValue maximumBlobFee := by
  unfold Reference.assessBlobCapacity Generated.finishBalanceAfterBlobCap
  simp only [mapChargeResult_ite, mapChargeResult_finalReturn]
  rw [assessBlobFeeCalculation_refines]
  simp [Reference.addU256, Generated.checkedAdd256, Reference.asU256, Generated.u256,
    Reference.uint256Modulus, Generated.uint256Modulus, mapState, Reference.note, Generated.appendLog] <;> rfl

private theorem assessMaximumBlobFee_refines
    (input : Generated.Input) (state : Reference.ReferenceState) (balanceAfterValue : Nat) :
    mapChargeResult (Reference.assessMaximumBlobFee input state balanceAfterValue) =
      Generated.finishMaximumBlobFee input (mapState state) balanceAfterValue := by
  unfold Reference.assessMaximumBlobFee Generated.finishMaximumBlobFee
  simp only [mapChargeResult_ite, mapChargeResult_finalReturn]
  rw [assessBlobCapacity_refines]
  simp only [blobGas_refines, blobFeeCap_refines, multiply_overflow_refines, multiply_value_refines]
  rfl

private theorem assessBalanceAfterValue_refines
    (input : Generated.Input) (state : Reference.ReferenceState) (maximumBalance : Nat) :
    mapChargeResult (Reference.assessBalanceAfterValue input state maximumBalance) =
      Generated.finishBalanceAfterValue input (mapState state) maximumBalance := by
  unfold Reference.assessBalanceAfterValue Generated.finishBalanceAfterValue
  simp only [mapChargeResult_ite, mapChargeResult_finalReturn]
  repeat' first | rw [mapChargeResult_ite] | rw [mapChargeResult_iteProp] | rw [mapChargeResult_finalReturn]
  rw [settleBalance_refines, assessMaximumBlobFee_refines]
  simp [Reference.addU256, Generated.checkedAdd256, Reference.asU256, Generated.u256,
    Reference.uint256Modulus, Generated.uint256Modulus, mapChargeResult, mapState, map_observe,
    Reference.note, Generated.appendLog] <;> rfl

private theorem assessMaximumFee_refines
    (input : Generated.Input) (state : Reference.ReferenceState) :
    mapChargeResult (Reference.assessMaximumFee input state) =
      Generated.finishMaximumBalanceCheck input (mapState state) := by
  unfold Reference.assessMaximumFee Generated.finishMaximumBalanceCheck
  simp only [mapChargeResult_ite, mapChargeResult_finalReturn]
  repeat' first | rw [mapChargeResult_ite] | rw [mapChargeResult_iteProp] | rw [mapChargeResult_finalReturn]
  rw [assessBalanceAfterValue_refines]
  cases h1559 : input.spec.eip1559Enabled <;> cases hFree : input.tx.isFree <;>
    simp_all [Reference.multiplyU256, Generated.checkedMul256, Reference.asU64, Generated.u64,
      Reference.asU256, Generated.u256, Reference.uint64Modulus, Generated.uint64Modulus,
      Reference.uint256Modulus, Generated.uint256Modulus, mapState, Reference.note, Generated.appendLog] <;> rfl

private theorem settleReservedPayment_refines
    (input : Generated.Input) (state : Reference.ReferenceState)
    (reservedValue : Nat) (reservedOverflow : Bool) :
    mapChargeResult (Reference.settleReservedPayment input state reservedValue reservedOverflow) =
      Generated.finishReservedPayment input (mapState state) reservedValue reservedOverflow := by
  unfold Reference.settleReservedPayment Generated.finishReservedPayment
  simp only [mapChargeResult_ite, mapChargeResult_finalReturn]
  rw [assessMaximumFee_refines]
  simp [mapState, Reference.note, Generated.appendLog] <;> rfl

private theorem settleFeeReservation_refines
    (input : Generated.Input) (state : Reference.ReferenceState) :
    mapChargeResult (Reference.settleFeeReservation input state) =
      Generated.finishGasPurchase input (mapState state) := by
  unfold Reference.settleFeeReservation Generated.finishGasPurchase
  rw [settleReservedPayment_refines]
  simp [Reference.multiplyU256, Generated.reservePayment, Generated.checkedMul256, Reference.asU64,
    Generated.u64, Reference.asU256, Generated.u256, Reference.uint64Modulus, Generated.uint64Modulus,
    Reference.uint256Modulus, Generated.uint256Modulus] <;> rfl

private theorem validatesGas_refines (input : Generated.Input) :
    Reference.validatesGas input = Generated.shouldValidateGas input := by
  simp [Reference.validatesGas, Generated.shouldValidateGas, Reference.skipsChecks, Generated.skipValidation,
    Reference.optionFlag, Generated.hasFlag, Reference.asU256, Generated.u256, Reference.uint256Modulus,
    Generated.uint256Modulus] <;> rfl

private theorem chargeGas_refines
    (input : Generated.Input) (state : Reference.ReferenceState) :
    mapChargeResult (Reference.chargeGas input state) = Generated.buyGas input (mapState state) := by
  unfold Reference.chargeGas Generated.buyGas
  rw [validatesGas_refines]
  simp only [mapChargeResult_ite, mapChargeResult_finalReturn]
  repeat' first | rw [mapChargeResult_ite] | rw [mapChargeResult_iteProp] | rw [mapChargeResult_finalReturn]
  rw [premium_refines, settleFeeReservation_refines]
  rw [settleFeeReservation_refines]
  simp [mapState, Reference.enter, Generated.appendStage, Reference.note, Generated.appendLog]

private theorem advanceNonce_refines (input : Generated.Input) (state : Reference.ReferenceState) :
    mapChargeResult (Reference.advanceNonce input state) =
      Generated.incrementNonce input (mapState state) := by
  rcases state with ⟨transaction, world, metrics, journal, effectiveGasPrice, opcodeGasPrice, premiumPerGas,
    senderReservedGasPayment, blobBaseFee, deleteCallerAccount, logs, trace⟩
  unfold Reference.advanceNonce Generated.incrementNonce
  simp only [mapChargeResult_iteProp, mapChargeResult_finalReturn]
  unfold mapChargeResult mapState
  dsimp [Reference.enter, Generated.appendStage,
    Reference.skipsChecks, Generated.skipValidation, Reference.optionFlag, Generated.hasFlag,
    Reference.asU64, Generated.u64, Reference.uint64Modulus, Generated.uint64Modulus,
    Reference.uint64Max, Generated.uint64Max, Reference.nextU64, Generated.add64,
    Reference.note, Generated.appendLog]
  repeat' first | split
  all_goals rfl

private theorem finishAfterGasCharge_refines
    (input : Generated.Input) (result : Sum Reference.Observation Reference.ReferenceState) :
    mapObservation (Reference.finishAfterGasCharge input result) =
      Generated.finishAfterGasCharge input (mapChargeResult result) := by
  cases result with
  | inl observation => rfl
  | inr state =>
    unfold Reference.finishAfterGasCharge Generated.finishAfterGasCharge
    change mapObservation
      (match Reference.advanceNonce input state with
      | .inl observation => observation
      | .inr state => Reference.observe .continue state) =
      match Generated.incrementNonce input (mapState state) with
      | .inl output => output
      | .inr state => Generated.finishPrefixContinuation state
    rw [← advanceNonce_refines input state]
    cases h : Reference.advanceNonce input state <;>
      simp_all [mapChargeResult, mapObservation, mapState, Reference.observe, Generated.finish,
        Generated.finishPrefixContinuation]

@[simp] private theorem map_sender (state : Reference.ReferenceState) (sender : Option Nat) :
    mapState (Reference.installRecoveredSender state sender) = Generated.replaceRecoveredSender (mapState state) sender := by rfl

@[simp] private theorem map_account (input : Generated.Input) (state : Reference.ReferenceState) (delete : Bool) :
    mapState (Reference.provisionRecoveredAccount input state delete) =
      Generated.createRecoveredAccount input (mapState state) delete := by
  cases h : input.world.standardMainnetWorldState <;>
    simp [Reference.provisionRecoveredAccount, Generated.createRecoveredAccount, mapState, h]

@[simp] private theorem map_completeRecovery (state : Reference.ReferenceState) :
    mapResult (Reference.completeRecovery state) = Generated.finishRecovery (mapState state) := by
  rcases state with ⟨⟨sender⟩, world, metrics, journal, effectiveGasPrice, opcodeGasPrice, premiumPerGas,
    senderReservedGasPayment, blobBaseFee, deleteCallerAccount, logs, trace⟩
  cases sender <;> rfl

@[simp] private theorem map_threw (state : Reference.ReferenceState) (site : Generated.ThrowSite) :
    mapResult (.threw state site) = Generated.RecoveryResult.threw (mapState state) site := by rfl

@[simp] private theorem mapResult_ite (condition : Bool)
    (onTrue onFalse : Reference.RecoveryResult) :
    mapResult (if condition then onTrue else onFalse) =
      if condition then mapResult onTrue else mapResult onFalse := by
  split <;> rfl

private theorem map_changedWork (input : Generated.Input) (state : Reference.ReferenceState) (sender : Option Nat) :
    mapState
      (let state := if input.logger.debugEnabled then Reference.note state .recoveryAttempt else state
       let state := Reference.installRecoveredSender state sender
       if input.logger.warnEnabled then Reference.note state .recoveryChangedSender else state) =
      (let state := if input.logger.debugEnabled then Generated.appendLog (mapState state) .recoveryAttempt else mapState state
       let state := Generated.replaceRecoveredSender state sender
       if input.logger.warnEnabled then Generated.appendLog state .recoveryChangedSender else state) := by
  cases input.logger.debugEnabled <;> cases input.logger.warnEnabled <;> simp

private theorem map_absentWork (input : Generated.Input) (state : Reference.ReferenceState) (create : Bool) :
    mapState
      (let state := if input.logger.debugEnabled then Reference.note state .recoveryAttempt else state
       let state := Reference.note state .senderAccountDoesNotExist
       if create then
         if input.world.createAccountSucceeds then
           Reference.provisionRecoveredAccount input state (!Reference.recoveryCommits input || Reference.asksRestore input)
         else state
       else state) =
      (let state := if input.logger.debugEnabled then Generated.appendLog (mapState state) .recoveryAttempt else mapState state
       let state := Generated.appendLog state .senderAccountDoesNotExist
       if create then
         if input.world.createAccountSucceeds then
           Generated.createRecoveredAccount input state (!Generated.recoveryCommit input || Generated.restore input.options)
         else state
       else state) := by
  cases input.logger.debugEnabled <;> cases create <;> cases input.world.createAccountSucceeds <;>
    simp [Reference.recoveryCommits, Generated.recoveryCommit, Reference.asksRestore, Generated.restore,
      Reference.optionFlag, Generated.hasFlag]

private theorem app_refines (input : Generated.Input) (state : Reference.ReferenceState) (disposition : Reference.RecoveryDisposition) :
    mapResult (Reference.executeSenderRecovery input state disposition) =
      Generated.applyRecoveryDecision input (mapState state) (mapDisposition disposition) := by
  cases disposition with
  | knownSender => rfl
  | recoveredSender sender =>
    simp only [Reference.executeSenderRecovery, Generated.applyRecoveryDecision, mapDisposition]
    rw [map_completeRecovery, map_changedWork]
  | absentSender create =>
    simp only [Reference.executeSenderRecovery, Generated.applyRecoveryDecision, mapDisposition]
    rw [mapResult_ite]
    simp only [map_threw, map_completeRecovery]
    rw [map_absentWork]

private theorem mapDisposition_classify (known changed create : Bool) (sender : Option Nat) :
    mapDisposition
      (if known then .knownSender else if changed then .recoveredSender sender else .absentSender create) =
      if known then .fast else if changed then .changed sender else .same create := by
  cases known <;> cases changed <;> rfl

private theorem classify_refines (input : Generated.Input) (state : Reference.ReferenceState) :
    mapDisposition (Reference.classifySenderRecovery input state) =
      Generated.classifyRecovery input (mapState state) := by
  simpa [Reference.classifySenderRecovery, Generated.classifyRecovery, mapState,
    Reference.recoveryCandidate, Generated.recoveryCandidate, Reference.recoveryCommits,
    Generated.recoveryCommit, Reference.skipsChecks, Generated.skipValidation, Reference.optionFlag,
    Generated.hasFlag] using
    mapDisposition_classify
      (state.transaction.sender.isSome && input.world.suppliedSenderAccountExists)
      (state.transaction.sender != Generated.recoveryCandidate input)
      (!Generated.recoveryCommit input || Generated.skipValidation input.options || state.effectiveGasPrice == 0)
      (Generated.recoveryCandidate input)

private theorem recover_refines (input : Generated.Input) (state : Reference.ReferenceState) :
    mapResult (Reference.recover input state) = Generated.applyRecovery input (mapState state) := by
  unfold Reference.recover Generated.applyRecovery
  rw [app_refines, classify_refines]

private theorem continueAfterRecovery_refines
    (input : Generated.Input) (state : Reference.ReferenceState) :
    mapObservation (Reference.continueAfterRecovery input state) =
      Generated.continueAfterRecovery input (mapState state) := by
  unfold Reference.continueAfterRecovery Generated.continueAfterRecovery
  have gate :
      (!Reference.skipsChecks input && !input.skipSenderCodeCheck && input.world.effectiveSenderInvalidContract) =
        (!Generated.skipValidation input.options && !input.skipSenderCodeCheck && input.world.effectiveSenderInvalidContract) := by
    simp [Reference.skipsChecks, Generated.skipValidation, Reference.optionFlag, Generated.hasFlag]
  rw [gate]
  split
  · simpa using
      (map_finalReturn input (Reference.note (Reference.enter state .validateSender) .invalidContractSender)
        .validateSender .senderHasDeployedCode)
  · rw [finishAfterGasCharge_refines, chargeGas_refines]
    simp

private theorem finishAfterRecovery_refines
    (input : Generated.Input) (result : Reference.RecoveryResult) :
    mapObservation (Reference.finishAfterRecovery input result) =
      Generated.finishAfterRecovery input (mapResult result) := by
  cases result <;>
    simp [Reference.finishAfterRecovery, Generated.finishAfterRecovery, mapResult,
      map_observe, continueAfterRecovery_refines]

private def prepareReferenceRecovery
    (input : Generated.Input) (state : Reference.ReferenceState) : Reference.ReferenceState :=
  let state := Reference.enter state .calculateEffectiveGasPrice
  let effectiveGasPrice := Reference.priceForExecution input
  let state := { state with effectiveGasPrice := effectiveGasPrice, opcodeGasPrice := effectiveGasPrice }
  let state := Reference.enter state .updateMetrics
  let state :=
    if input.options.raw == Generated.executionOptionCommit || input.options.raw == 0 then
      { state with metrics := { blockGasPrices := [effectiveGasPrice] } }
    else state
  Reference.enter state .recoverSenderIfNeeded

private def prepareGeneratedRecovery
    (input : Generated.Input) (state : Generated.AdmissionState) : Generated.AdmissionState :=
  let state := Generated.appendStage state .calculateEffectiveGasPrice
  let effectiveGasPrice := Generated.calculateEffectiveGasPrice input
  let state := { state with effectiveGasPrice := effectiveGasPrice, opcodeGasPrice := effectiveGasPrice }
  let state := Generated.appendStage state .updateMetrics
  let state :=
    if input.options.raw == Generated.executionOptionCommit || input.options.raw == 0 then
      { state with metrics := { blockGasPrices := [effectiveGasPrice] } }
    else state
  Generated.appendStage state .recoverSenderIfNeeded

private theorem prepareRecovery_refines
    (input : Generated.Input) (state : Reference.ReferenceState) :
    mapState (prepareReferenceRecovery input state) =
      prepareGeneratedRecovery input (mapState state) := by
  unfold prepareReferenceRecovery prepareGeneratedRecovery
  rw [price_refines]
  by_cases hMetrics : input.options.raw == Generated.executionOptionCommit || input.options.raw == 0
  · simp [hMetrics, mapState, Reference.enter, Generated.appendStage]
  · simp [hMetrics, mapState, Reference.enter, Generated.appendStage]

/-! This is the composed simulation itself: static gate, price/metrics preparation, recovery,
    sender validation, fee reservation, and nonce advance are connected by the lemmas above. -/
theorem generatedRun_mapsReference
    (input : Generated.Input) :
    mapObservation (Reference.run input) = Generated.run input := by
  change mapObservation
      (let state := Reference.enter (Reference.start input) .validateStatic
       if input.staticError != .none then
         Reference.observe (.returned .validateStatic input.staticError) state
       else
         Reference.finishAfterRecovery input
           (Reference.recover input (prepareReferenceRecovery input state))) =
    (let state := Generated.appendStage (Generated.initialState input) .validateStatic
     if input.staticError != .none then
       Generated.finish (.returned .validateStatic input.staticError) state
     else
       Generated.finishAfterRecovery input
         (Generated.applyRecovery input (prepareGeneratedRecovery input state)))
  split
  · rw [map_observe, map_enter, map_start]
  · rw [finishAfterRecovery_refines, recover_refines, prepareRecovery_refines, map_enter, map_start]

def observeGenerated (output : Generated.Output) : Reference.Observation :=
  { outcome := output.outcome
    transaction := output.transaction
    world := output.world
    metrics := output.metrics
    journal := output.journal
    effectiveGasPrice := output.effectiveGasPrice
    opcodeGasPrice := output.opcodeGasPrice
    premiumPerGas := output.premiumPerGas
    senderReservedGasPayment := output.senderReservedGasPayment
    blobBaseFee := output.blobBaseFee
    deleteCallerAccount := output.deleteCallerAccount
    logs := output.logs
    trace := output.trace }

@[simp] private theorem observeGenerated_mapObservation (observation : Reference.Observation) :
    observeGenerated (mapObservation observation) = observation := by rfl

/-- The generated bounded prefix has the same observable transition as the independently structured reference
    when the separately reviewed compiler-resolution audit validates the finite syntax-grammar tags. -/
theorem generatedRun_refines_handwrittenReference
    (input : Generated.Input) (_ : Generated.sourceGrammarSemanticsCoherent input) :
    observeGenerated (Generated.run input) = Reference.run input := by
  symm
  calc
    Reference.run input = observeGenerated (mapObservation (Reference.run input)) := by rfl
    _ = observeGenerated (Generated.run input) := congrArg observeGenerated (generatedRun_mapsReference input)

def ProductionPrefixAgreement (input : Generated.Input) : Prop :=
  Generated.sourceGrammarSemanticsCoherent input ∧
    Generated.inputAdapterCoherent input ∧
      observeGenerated (Generated.run input) = Reference.run input

/-- Bounded transcription corollary after the source-admitted ordinary-route classifier has selected this processor.
    It remains conditional on the external compiler-resolution audit and is not a C# semantic-refinement theorem. -/
theorem ordinaryStandardPrefix_refines_handwrittenReference
    (input : Generated.Input) (h : Generated.ordinaryStandardInput input) :
    ProductionPrefixAgreement input := by
  rcases h with ⟨_, _, _, _, _, _, _, _, _, coherent⟩
  unfold Generated.inputAdapterCoherent at coherent
  exact ⟨coherent.left, coherent, generatedRun_refines_handwrittenReference input coherent.left⟩

end OrdinaryStatefulAdmissionPrefixExtractor.Refinement
