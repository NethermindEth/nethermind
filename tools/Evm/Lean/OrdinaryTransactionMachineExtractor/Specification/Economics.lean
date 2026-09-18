-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import OrdinaryTransactionMachineExtractor.Specification.TransactionState
import WorldJournalExtractor.Specification.WorldJournal

namespace OrdinaryTransactionMachineExtractor.Specification

/-! Handwritten operational primitives. Their source refinement is not yet proved. -/

def uint256Bound : Nat := 2 ^ 256
def uint64Bound : Nat := 2 ^ 64

def checkedReservationAmount (value : Nat) : Except Failure Nat :=
  if value < uint256Bound then .ok value
  else .error (.invalid .insufficientMaxFeePerGasForSenderBalance)

def effectiveCommit (options : Options) (fork : Fork) : Bool :=
  options.commit || (!options.skipValidation && !fork.eip658)

def recoveryCommit (options : Options) (fork : Fork) : Bool :=
  options.commit || !fork.eip658

def shouldValidateGas (tx : Transaction) (options : Options) : Bool :=
  !options.skipValidation || tx.maxFee != 0 || tx.maxPriorityFee != 0

def effectivePrice (tx : Transaction) (header : Header) (fork : Fork) : Nat :=
  if !fork.eip1559 then tx.maxPriorityFee
  else if tx.maxPriorityFee + header.baseFee ≥ uint256Bound then tx.maxFee
  else min tx.maxFee (tx.maxPriorityFee + header.baseFee)

def premium (tx : Transaction) (header : Header) : Except Failure Nat :=
  let cap := if tx.supports1559 then tx.maxFee else tx.maxPriorityFee
  if header.baseFee > cap then
    if tx.isSystem || tx.isService then .ok 0 else .error (.invalid .maxFeePerGasBelowBaseFee)
  else .ok (min tx.maxPriorityFee (cap - header.baseFee))

structure BlobFees where
  gas : Nat
  price : Nat
  total : Nat
  deriving DecidableEq, Repr

def quoteReservation (tx : Transaction) (header : Header) (fork : Fork)
    (options : Options) (blob : Option BlobFees) : Except Failure Reservation := do
  if tx.gasLimit ≥ uint64Bound then throw (.incomplete .uint64Domain)
  if tx.value ≥ uint256Bound || tx.maxFee ≥ uint256Bound ||
      tx.maxPriorityFee ≥ uint256Bound || header.baseFee ≥ uint256Bound then
    throw (.incomplete .uint256Domain)
  let price := effectivePrice tx header fork
  let tip ← if shouldValidateGas tx options then premium tx header else pure 0
  let actualGas ← checkedReservationAmount (tx.gasLimit * price)
  let capGas ← checkedReservationAmount
    (if fork.eip1559 && !(tx.isSystem || tx.isService)
      then tx.gasLimit * tx.maxFee else actualGas)
  let balanceRequirement ← checkedReservationAmount (capGas + tx.value)
  if tx.supportsBlobs then
    match blob, tx.maxBlobFee with
    | some fees, some cap =>
        if cap ≥ uint256Bound || fees.price ≥ uint256Bound || fees.total ≥ uint256Bound then
          throw (.incomplete .uint256Domain)
        if fees.gas ≥ uint64Bound || fees.total != fees.gas * fees.price then
          throw (.incomplete .blobFeeCalculation)
        let blobCap ← checkedReservationAmount (fees.gas * cap)
        let required ← checkedReservationAmount (balanceRequirement + blobCap)
        if cap < fees.price then throw (.invalid .insufficientSenderBalance)
        let reservation ← checkedReservationAmount (actualGas + fees.total)
        pure ⟨required, reservation, 0, price, tip, fees.total⟩
    | _, _ => throw (.incomplete .blobFeeCalculation)
  else if blob.isSome then
    throw (.incomplete .blobFeeCalculation)
  else
    pure ⟨balanceRequirement, actualGas, 0, price, tip, 0⟩

def replaceJournal (state : State) (journal : WorldJournalExtractor.JournalState) : State :=
  { state with world := { state.world with world := { state.world.world with journal } } }

def readAccount (state : State) (address : Address) : State × Option WorldJournalExtractor.AccountView :=
  let (journal, account) := WorldJournalExtractor.Specification.readAccount state.world.world.journal address
  (replaceJournal state journal, account)

def writeAccount (state : State) (address : Address) (account : WorldJournalExtractor.AccountView) : State :=
  replaceJournal state
    (WorldJournalExtractor.Specification.writeAccount state.world.world.journal address account)

def reserveGas (tx : Transaction) (options : Options) (quote : Reservation)
    (state : State) : StepOutcome :=
  match tx.sender with
  | none => .failure (.invalid .senderNotSpecified) state
  | some sender =>
      let (readState, found) := readAccount state sender
      match found with
      | none => .failure (.incomplete .journalPrecondition) readState
      | some account =>
          if account.balance < quote.maximumBalanceRequirement && !options.warmup then
            .failure (.invalid .insufficientMaxFeePerGasForSenderBalance) readState
          else
            let charge := if options.warmup then min quote.requestedReserve account.balance
              else quote.requestedReserve
            if charge > account.balance then .failure (.incomplete .uint256Domain) readState
            else
              let after := if charge == 0 then readState
                else writeAccount readState sender { account with balance := account.balance - charge }
              .success { after with reservation := some { quote with actualDebit := charge } }

def incrementNonce (tx : Transaction) (options : Options) (state : State) : StepOutcome :=
  match tx.sender with
  | none => .failure (.invalid .senderNotSpecified) state
  | some sender =>
      let (readState, found) := readAccount state sender
      match found with
      | none => .failure (.incomplete .journalPrecondition) readState
      | some account =>
          if account.nonce ≥ uint64Bound || tx.nonce ≥ uint64Bound then
            .failure (.incomplete .uint64Domain) readState
          else if !options.skipValidation && tx.nonce != account.nonce then
            .failure (.invalid (if tx.nonce > account.nonce then
              .transactionNonceTooHigh else .transactionNonceTooLow)) readState
          else
            let nonce := if account.nonce + 1 < uint64Bound then account.nonce + 1 else 0
            .success (writeAccount readState sender { account with nonce })

def credit (address : Address) (amount : Nat) (state : State) : StepOutcome :=
  let (readState, found) := readAccount state address
  let account := found.getD WorldJournalExtractor.emptyAccount
  let balance := account.balance + amount
  if balance ≥ uint256Bound then .failure (.incomplete .uint256Domain) readState
  else .success (writeAccount readState address { account with balance })

/-- Sequential reads/writes preserve participant aliasing; no cached participant copies are used. -/
def moveSimpleTransferValue (tx : Transaction) (options : Options) (destination : Address)
    (state : State) : StepOutcome :=
  match tx.sender with
  | none => .failure (.invalid .senderNotSpecified) state
  | some sender =>
      if sender == destination then .success state
      else
        let (readState, found) := readAccount state sender
        match found with
        | none => .failure (.incomplete .journalPrecondition) readState
        | some account =>
            let debit := if options.warmup then min tx.value account.balance else tx.value
            if debit > account.balance then .failure (.incomplete .uint256Domain) readState
            else
              let debited := if debit == 0 then readState
                else writeAccount readState sender { account with balance := account.balance - debit }
              credit destination tx.value debited

end OrdinaryTransactionMachineExtractor.Specification
