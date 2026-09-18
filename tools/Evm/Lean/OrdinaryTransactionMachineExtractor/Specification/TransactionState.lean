-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import FrameJournalExtractor.Specification.FrameJournalState

namespace OrdinaryTransactionMachineExtractor.Specification

abbrev Address := WorldJournalExtractor.Address
abbrev Bytes := List UInt8

inductive MissingDependency where
  | sourceSemanticLowering | sourceInputProjection | forkProjection
  | senderRecovery | intrinsicCalculation | intrinsicStateGasAdapterComposition
  | blobFeeCalculation | blockHashLookup | codeLookup | authorityRecovery | delegationInstallation
  | creationAddress | topFrameInputProjection | frameExecution | frameResultProjection
  | codeDeposit | haltGasComposition | commitReset | stateRoot | tracerProjection
  | uint256Domain | uint64Domain | journalPrecondition | unsupportedOptions | systemRoute
  deriving DecidableEq, Repr

inductive InvalidTransaction where
  | blockGasLimitExceeded | gasLimitBelowIntrinsicGas | gasLimitBelowFloorGas
  | insufficientMaxFeePerGasForSenderBalance | insufficientSenderBalance
  | malformedTransaction | maxFeePerGasBelowBaseFee | minerPremiumNegative
  | nonceOverflow | senderHasDeployedCode | senderNotSpecified
  | transactionSizeOverMaxInitCodeSize | transactionNonceTooHigh | transactionNonceTooLow
  deriving DecidableEq, Repr

def InvalidTransaction.sourceValue : InvalidTransaction → Nat
  | .blockGasLimitExceeded => 1
  | .gasLimitBelowIntrinsicGas => 2
  | .gasLimitBelowFloorGas => 3
  | .insufficientMaxFeePerGasForSenderBalance => 4
  | .insufficientSenderBalance => 5
  | .malformedTransaction => 6
  | .maxFeePerGasBelowBaseFee => 7
  | .minerPremiumNegative => 8
  | .nonceOverflow => 9
  | .senderHasDeployedCode => 10
  | .senderNotSpecified => 11
  | .transactionSizeOverMaxInitCodeSize => 12
  | .transactionNonceTooHigh => 13
  | .transactionNonceTooLow => 14

inductive EscapingException where
  | unresolvedSender | unresolvedRecipient | unresolvedBlockHash | blobFeeOverflow
  | missingCode | canceled | externalCallback
  deriving DecidableEq, Repr

inductive Failure where
  | invalid (reason : InvalidTransaction)
  | incomplete (dependency : MissingDependency)
  | escaped (exception : EscapingException)
  deriving DecidableEq, Repr

structure Options where
  commit : Bool
  «restore» : Bool
  skipValidation : Bool
  warmup : Bool
  buildUp : Bool
  deriving DecidableEq, Repr

def Options.raw (options : Options) : Nat :=
  (if options.commit then 1 else 0) + (if options.«restore» then 2 else 0) +
  (if options.skipValidation then 4 else 0) + (if options.warmup then 8 else 0) +
  (if options.buildUp then 16 else 0)

def decodeOptions (raw : Nat) : Except Failure Options :=
  if raw < 32 then
    .ok ⟨raw % 2 == 1, raw / 2 % 2 == 1, raw / 4 % 2 == 1,
      raw / 8 % 2 == 1, raw / 16 % 2 == 1⟩
  else .error (.incomplete .unsupportedOptions)

def routesToSystem (isSystem : Bool) (options : Options) : Bool :=
  isSystem || options.raw == 4

structure SpecIdentity where
  objectIdentity : Nat
  deriving DecidableEq, Repr

structure ForkGasCosts where
  txDataNonZeroMultiplier : Nat
  totalCostFloorPerToken : Nat
  destroyRefund : Nat
  balanceCost : Nat
  extCodeCost : Nat
  extCodeHashCost : Nat
  maxBlobGasPerBlock : Nat
  maxBlobGasPerTx : Nat
  targetBlobGasPerBlock : Nat
  deriving DecidableEq, Repr

structure Fork where
  identity : SpecIdentity
  chainId : Nat
  gasCosts : ForkGasCosts
  eip2 : Bool
  eip158 : Bool
  eip658 : Bool
  eip1559 : Bool
  eip2028 : Bool
  eip2780 : Bool
  eip2929 : Bool
  eip2930 : Bool
  eip2935 : Bool
  eip3529 : Bool
  eip3541 : Bool
  eip3607 : Bool
  eip3651 : Bool
  eip3860 : Bool
  eip4844 : Bool
  eip7623 : Bool
  eip7702 : Bool
  eip7708 : Bool
  eip7778 : Bool
  eip7843 : Bool
  eip7976 : Bool
  eip7981 : Bool
  eip8037 : Bool
  eip8038 : Bool
  eip8246 : Bool
  blockHashInStateAvailable : Bool
  blockHashHistoryAddress : Option Address
  blockHashRingBufferSize : Nat
  blobFeeCollector : Bool
  feeCollector : Option Address
  blobBaseFeeUpdateFraction : Nat
  maxCodeSize : Nat
  maxInitCodeSize : Nat
  limitCodeSize : Bool
  executionCap : Nat
  deriving DecidableEq, Repr

structure Signature where
  r : Nat
  s : Nat
  v : Nat
  deriving DecidableEq, Repr

structure Authorization where
  chainId : Nat
  nonce : Nat
  codeAddress : Address
  signature : Signature
  recoveredAuthority : Option Address
  deriving DecidableEq, Repr

structure AccessListEntry where
  address : Address
  storageKeys : List Nat
  deriving DecidableEq, Repr

structure GasState where
  execution : Nat
  reservoir : Int
  stateUsed : Int
  spill : Int
  refundedSpill : Int
  deriving DecidableEq, Repr

structure Intrinsic where
  standard : GasState
  floor : GasState
  deriving DecidableEq, Repr

inductive IntrinsicMemo where
  | ethereum (specIdentity : SpecIdentity) (isEip2780SelfTransfer : Bool) (gas : Intrinsic)
  | foreign
  deriving DecidableEq, Repr

inductive TransactionRuntimeType where
  | ordinary | system | other
  deriving DecidableEq, Repr

structure Transaction where
  objectIdentity : Nat
  runtimeType : TransactionRuntimeType
  txType : UInt8
  chainId : Option Nat
  hash : Option Nat
  sender : Option Address
  recipient : Option Address
  nonce : Nat
  gasLimit : Nat
  value : Nat
  decodedMaxFee : Nat
  maxPriorityFee : Nat
  maxBlobFee : Option Nat
  isOPSystemTransaction : Bool
  isService : Bool
  data : Bytes
  signature : Option Signature
  accessList : Option (List AccessListEntry)
  blobVersionedHashes : Option (List (Option Bytes))
  authorizations : Option (List Authorization)
  intrinsicMemo : Option IntrinsicMemo
  deriving DecidableEq, Repr

def Transaction.isSystem (tx : Transaction) : Bool :=
  tx.runtimeType == .system || tx.sender == some (2 ^ 160 - 2) || tx.isOPSystemTransaction

def Transaction.supportsAccessList (tx : Transaction) : Bool :=
  tx.txType.toNat ≥ 1 && tx.txType.toNat != 126

def Transaction.supports1559 (tx : Transaction) : Bool :=
  tx.txType.toNat ≥ 2 && tx.txType.toNat != 126

def Transaction.supportsBlobs (tx : Transaction) : Bool := tx.txType.toNat == 3

def Transaction.maxFee (tx : Transaction) : Nat :=
  if tx.supports1559 then tx.decodedMaxFee else tx.maxPriorityFee

def Transaction.blobCount (tx : Transaction) : Nat :=
  (tx.blobVersionedHashes.getD []).length

structure Header where
  number : Nat
  timestamp : Nat
  slotNumber : Option Nat
  parentHash : Option Nat
  hash : Option Nat
  excessBlobGas : Option Nat
  blobGasUsed : Option Nat
  difficulty : Nat
  random : Option Nat
  isPostMerge : Bool
  gasLimit : Nat
  gasUsed : Nat
  executionGas : Nat
  stateGas : Nat
  cumulativeReceiptGas : Nat
  baseFee : Nat
  beneficiary : Option Address
  deriving DecidableEq, Repr

structure BlockContext where
  header : Header
  fork : Fork
  chainId : Nat
  cachedCoinbase : Address
  cachedNumber : Nat
  cachedGasLimit : Nat
  cachedBlobBaseFee : Nat
  prevRandao : Nat
  isGenesis : Bool
  deriving DecidableEq, Repr

structure Reservation where
  maximumBalanceRequirement : Nat
  requestedReserve : Nat
  actualDebit : Nat
  effectivePrice : Nat
  premium : Nat
  blobBurn : Nat
  deriving DecidableEq, Repr

structure PreparationSnapshot where
  journal : WorldJournalExtractor.FrameSnapshot
  gas : GasState
  intrinsic : Intrinsic
  deriving DecidableEq, Repr

structure AuthorizationPhase where
  nextAuthorization : Nat
  delegationCodeInsertRefundCount : Int
  preAuthorizationStateGasUsed : Int
  delegatedBeforeTransaction : List (Address × Bool)
  delegationSetFor : List Address
  writtenAccounts : List Address
  topFrameOutOfGas : Bool
  deriving DecidableEq, Repr

inductive FrameExit where
  | success | revert | exception
  deriving DecidableEq, Repr

inductive EvmFailure where
  | stop | badInstruction | stackOverflow | stackUnderflow | outOfGas
  | invalidJumpDestination | accessViolation | staticCallViolation | precompileFailure
  | transactionCollision | notEnoughBalance | other | revert | invalidCode | suspend
  deriving DecidableEq, Repr

def EvmFailure.sourceValue : EvmFailure → Int
  | .stop => -1
  | .badInstruction => 1
  | .stackOverflow => 2
  | .stackUnderflow => 3
  | .outOfGas => 4
  | .invalidJumpDestination => 5
  | .accessViolation => 6
  | .staticCallViolation => 7
  | .precompileFailure => 8
  | .transactionCollision => 9
  | .notEnoughBalance => 10
  | .other => 11
  | .revert => 12
  | .invalidCode => 13
  | .suspend => 14

structure TransactionResult where
  error : Option InvalidTransaction
  evmFailure : Option EvmFailure
  errorDescription : String
  deriving DecidableEq, Repr

structure Substate where
  shouldRevert : Bool
  isError : Bool
  evmFailure : Option EvmFailure
  opcodeRefund : Int
  output : Bytes
  logs : List WorldJournalExtractor.LogEntry
  destroyList : List Address
  shouldRestoreRipemdTouch : Bool
  deriving DecidableEq, Repr

structure SettlementInputs where
  opcodeRefund : Int
  codeInsertRefundCount : Nat
  codeInsertExecutionRefund : Nat
  executionIntrinsicStandard : GasState
  postIntrinsicStateReservoir : Int
  topLevelCreateStateGasCharged : Bool
  deriving DecidableEq, Repr

structure Settlement where
  paid : Nat
  operation : Nat
  blockExecution : Nat
  blockState : Nat
  maximum : Nat
  refund : Nat
  deriving DecidableEq, Repr

structure Receipt where
  txType : UInt8
  transactionHash : Option Nat
  blockHash : Option Nat
  blockNumber : Nat
  transactionIndex : Int
  sender : Option Address
  recipient : Option Address
  contractAddress : Option Address
  success : Bool
  paidGas : Nat
  cumulativeGas : Nat
  output : Bytes
  logs : List WorldJournalExtractor.LogEntry
  stateRoot : Option Nat
  failure : Option EvmFailure
  errorDescription : Option String
  effectiveGasPrice : Nat
  operationGas : Nat
  blockExecutionGas : Nat
  blockStateGas : Nat
  deriving DecidableEq, Repr

structure State where
  world : FrameJournalExtractor.Machine
  entryJournal : WorldJournalExtractor.JournalState
  preparation : Option PreparationSnapshot
  authorization : Option AuthorizationPhase
  topSnapshot : Option WorldJournalExtractor.FrameSnapshot
  gas : GasState
  intrinsic : Intrinsic
  executionIntrinsicStandard : GasState
  postIntrinsicReservoir : Int
  topLevelCreateStateGasCharged : Bool
  reservation : Option Reservation
  temporarySender : Bool
  header : Header
  substate : Option Substate
  settlementInputs : Option SettlementInputs
  settlement : Option Settlement
  receipt : Option Receipt
  result : Option TransactionResult
  deriving DecidableEq, Repr

inductive StepOutcome where
  | success (state : State)
  | failure (reason : Failure) (state : State)
  deriving DecidableEq, Repr

structure TopFrameInput where
  block : BlockContext
  sender : Address
  destination : Address
  codeSource : Option Address
  callDepth : Nat
  isStatic : Bool
  isCreate : Bool
  code : Bytes
  inputData : Bytes
  value : Nat
  gas : GasState
  world : FrameJournalExtractor.Machine
  snapshot : WorldJournalExtractor.FrameSnapshot
  opcodeGasPrice : Nat
  blobVersionedHashes : Option (List (Option Bytes))
  deriving DecidableEq, Repr

structure FrameCompletion where
  exit : FrameExit
  world : FrameJournalExtractor.Machine
  gas : GasState
  substate : Substate
  deriving DecidableEq, Repr

end OrdinaryTransactionMachineExtractor.Specification
