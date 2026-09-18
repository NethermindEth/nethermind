-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

-- Generated from admitted C# syntax. External calls are observations, not proved implementations.
-- IR SHA-256: b1ec3e5e069d7638d3fdb972f9d9653188b2f0afaa8c55ff50c6aae084457f04
namespace BlockProcessorExtractor.Generated

inductive Phase where
  | daoTransition
  | beaconRootSystemCall
  | historicalBlockhashStateChange
  | userTransactionFold
  | blobGasReceiptRootAndBloom
  | rewards
  | withdrawals
  | executionRequestsAndSystemCalls
  | storageAndStateRoots
  | blockAccessList
  | processedHeaderValidation
  deriving DecidableEq, Repr

inductive ReceiptEvent where
  | receiptBloomsComputed
  | receiptsRootComputed
  | receiptsRootInstalled
  deriving DecidableEq, Repr

inductive FinalizationStep where
  | totalDifficulty
  | mainChainUpdate
  | markProcessed
  deriving DecidableEq, Repr

def processPhases : List Phase :=
  [ .daoTransition, .beaconRootSystemCall, .historicalBlockhashStateChange, .userTransactionFold, .blobGasReceiptRootAndBloom, .rewards, .withdrawals, .executionRequestsAndSystemCalls, .storageAndStateRoots, .blockAccessList, .processedHeaderValidation ]

def synchronousReceiptEvents : List ReceiptEvent :=
  [ .receiptBloomsComputed, .receiptsRootComputed, .receiptsRootInstalled ]

def blockEvents : List String :=
  [ "traceStarted", "executionContextSet", "balSetup", "commitState", "commitState", "commitState", "executionRequests", "traceEnded", "commitStorageRoots", "accountChanges", "stateRoot", "balFinalized" ]

def perBlockEvents : List String :=
  [ "processOne", "inclusionList", "waitPrewarm", "commitTree", "reset" ]

def finalizationSteps : List FinalizationStep :=
  [ .totalDifficulty, .mainChainUpdate, .markProcessed ]

def cleanupEvents : List String :=
  [ "disposeAccountChanges", "disposeAccountChanges", "disposeScopeForRetry", "reopenScopeForRetry", "disposeScopeAtCheckpoint", "disposeScopeFinally" ]

def copiedHeaderFields : List (String × String) :=
  [ ("dst.Bloom", "Core.Bloom.Empty"), ("dst.Author", "Author"), ("dst.Hash", "Hash"), ("dst.MixHash", "MixHash"), ("dst.Nonce", "Nonce"), ("dst.TxRoot", "TxRoot"), ("dst.TotalDifficulty", "TotalDifficulty"), ("dst.ReceiptsRoot", "ReceiptsRoot"), ("dst.BaseFeePerGas", "BaseFeePerGas"), ("dst.WithdrawalsRoot", "WithdrawalsRoot"), ("dst.RequestsHash", "RequestsHash"), ("dst.IsPostMerge", "IsPostMerge"), ("dst.ParentBeaconBlockRoot", "ParentBeaconBlockRoot"), ("dst.SlotNumber", "SlotNumber"), ("dst.BlockAccessListHash", "BlockAccessListHash"), ("dst.BlobGasUsed", "BlobGasUsed"), ("dst.ExcessBlobGas", "ExcessBlobGas") ]

def publishedArtifacts : List (String × String) :=
  [ ("suggestedBlock.AccountChanges", "processedBlock.AccountChanges"), ("suggestedBlock.ExecutionRequests", "processedBlock.ExecutionRequests"), ("suggestedBlock.GeneratedBlockAccessList", "processedBlock.GeneratedBlockAccessList"), ("suggestedBlock.EncodedBlockAccessList", "processedBlock.EncodedBlockAccessList??suggestedBlock.EncodedBlockAccessList") ]

def checkpointIndex (index total : Nat) : Bool :=
  (((index % 64) == 0) && ((!(index == 0)) && (!(index == (total - 1)))))

end BlockProcessorExtractor.Generated
