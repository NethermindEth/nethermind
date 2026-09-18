-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import SynchronousBlockPipelineMachineExtractor.Specification.BlockPipelinePlan

namespace SynchronousBlockPipelineMachineExtractor.Specification

inductive CompositionStatus where
  | sourceProjectionOnly
  | semanticAdapterOpen
  | byteIdentityAccepted
  | persistenceOpen
  deriving DecidableEq, Repr

structure CompositionInput where
  packageName : String
  artifactName : String
  productionRole : String
  status : CompositionStatus
  deriving DecidableEq, Repr

def existingCompositionInputs : List CompositionInput :=
  [{ packageName := "BlockProcessorExtractor"
     artifactName := "BlockProcessorControl"
     productionRole := "source syntax projection for BlockProcessor/BranchProcessor/BlockchainProcessor"
     status := .sourceProjectionOnly },
   { packageName := "Eip803x"
     artifactName := "BlockReference"
     productionRole := "handwritten block reference boundary"
     status := .semanticAdapterOpen },
   { packageName := "Eip803x"
     artifactName := "BranchReference"
     productionRole := "handwritten branch scope/retry boundary"
     status := .semanticAdapterOpen },
   { packageName := "Eip803x"
     artifactName := "BlockSystemComposition"
     productionRole := "byte-pinned block/system composition dependency; semantic admission remains open"
     status := .byteIdentityAccepted },
   { packageName := "Eip803x"
     artifactName := "ParallelBlockReference"
     productionRole := "byte-pinned parallel/BAL composition dependency; equivalence remains open"
     status := .byteIdentityAccepted },
   { packageName := "OrdinaryTransactionMachineExtractor"
     artifactName := "ExecutionBoundary"
     productionRole := "ordinary transaction adapter boundary"
     status := .semanticAdapterOpen },
   { packageName := "TransactionProcessorExtractor"
     artifactName := "TransactionProcessorLifecycle"
     productionRole := "transaction processor lifecycle projection"
     status := .sourceProjectionOnly }]

def compositionObligations : List String :=
  ["replace source projections with exact semantic adapters",
   "connect ordinary and system transaction settlement to receipt and gas observations",
   "connect BlockReference and BranchReference state tokens to real journals and scopes",
   "prove the selected parallel/BAL path or keep it outside the claim",
   "prove hashing, persistence and compiler/runtime assumptions separately"]

end SynchronousBlockPipelineMachineExtractor.Specification
