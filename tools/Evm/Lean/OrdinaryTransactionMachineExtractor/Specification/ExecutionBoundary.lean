-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import OrdinaryTransactionMachineExtractor.Specification.Economics

namespace OrdinaryTransactionMachineExtractor.Specification

/-! Dependencies return bounded payloads, never a replacement transaction State.
These declarations name obligations; they supply no provider or proof witness. -/

structure RecoveryQuery where
  transaction : Transaction
  chainId : Nat
  validateChainId : Bool
  deriving DecidableEq, Repr

structure CodeQuery where
  account : Address
  followDelegation : Bool
  fork : Fork
  world : WorldJournalExtractor.Machine
  deriving DecidableEq, Repr

structure CodeAnswer where
  code : Bytes
  delegation : Option Address
  isPrecompile : Bool
  deriving DecidableEq, Repr

structure AuthorizationRecoveryQuery where
  authorization : Authorization
  chainId : Nat
  deriving DecidableEq, Repr

structure TracerProjection where
  receipt : Receipt
  feeTip : Nat
  baseAndBlobBurn : Nat
  deriving DecidableEq, Repr

inductive ExternalRequest where
  | recoverSender (query : RecoveryQuery)
  | intrinsicGas (tx : Transaction) (fork : Fork) (blockGasLimit : Nat)
  | blobFees (tx : Transaction) (header : Header)
      (blobBaseFeeUpdateFraction : Nat)
  | blockHash (block : BlockContext) (number : Nat) (world : WorldJournalExtractor.Machine)
  | codeLookup (query : CodeQuery)
  | recoverAuthority (query : AuthorizationRecoveryQuery)
  | creationAddress (sender : Address) (preIncrementNonce : Nat)
  | executeFrame (input : TopFrameInput)
  | validateDeployedCode (code : Bytes) (fork : Fork)
  | commitReset (options : Options) (state : WorldJournalExtractor.Machine)
  | stateRoot (state : WorldJournalExtractor.Machine)
  | trace (projection : TracerProjection)

def ExternalResponse : ExternalRequest → Type
  | .recoverSender _ => Option Address
  | .intrinsicGas _ _ _ => Intrinsic
  | .blobFees _ _ _ => BlobFees
  | .blockHash _ _ _ => Option Nat
  | .codeLookup _ => CodeAnswer
  | .recoverAuthority _ => Option Address
  | .creationAddress _ _ => Address
  | .executeFrame _ => FrameCompletion
  | .validateDeployedCode _ _ => Bool
  | .commitReset _ _ => WorldJournalExtractor.Machine
  | .stateRoot _ => Nat
  | .trace _ => Unit

abbrev ExternalImplementation :=
  (request : ExternalRequest) → Except Failure (ExternalResponse request)

inductive DriverOutcome where
  | invalid (reason : InvalidTransaction) (state : State)
  | escaped (exception : EscapingException) (state : State)
  | completed (state : State)
  | incomplete (dependency : MissingDependency) (state : State)
  deriving DecidableEq, Repr

/-- No complete transaction can be claimed before source semantic lowering exists. -/
def runScaffold (_tx : Transaction) (_fork : Fork) (_options : Options)
    (state : State) : DriverOutcome :=
  .incomplete .sourceSemanticLowering state

end OrdinaryTransactionMachineExtractor.Specification
