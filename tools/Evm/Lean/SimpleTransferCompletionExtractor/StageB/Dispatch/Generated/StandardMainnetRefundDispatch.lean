-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only
-- Generated from the admitted standard-mainnet receiver and call-site closure. Do not edit.

namespace SimpleTransferCompletionExtractor.StageB.Dispatch.Generated

inductive ReceiverKind where
  | ethereumTransactionProcessor
  | balTransactionProcessor
  deriving DecidableEq, Repr

inductive MethodKind where
  | refund
  | payRefund
  deriving DecidableEq, Repr

structure ReceiverEvidence where
  runtimeType : String
  lineage : List String
  isSealed : Bool
  declaresRefund : Bool
  declaresPayRefund : Bool
  deriving DecidableEq, Repr

def baseProcessor : String := "global::Nethermind.Evm.TransactionProcessing.TransactionProcessorBase<global::Nethermind.Evm.GasPolicy.EthereumGasPolicy>"
def gasPolicy : String := "global::Nethermind.Evm.GasPolicy.EthereumGasPolicy"
def sourceClosureSha256 : String := "1ee418a801f9fddbbbf38354865f701eba7add0536d80d2653ff521a0603320d"
def referenceClosureSha256 : String := "ab40fa72023510363b582e317d10de98f8acf43dbd3cee39ef2d63a3c60a961c"
def prefixManifestSha256 : String := "d7eb0fedb1ed7f97e18ba030325441e05a8b27d06158aef003808a497b1030ee"
def ordinaryRefundManifestSha256 : String := "a0dd398a94994b65c3ea6bc59b6d62cd77958025208c423237e14a44d1ce30a1"

def receiverEvidence : ReceiverKind → ReceiverEvidence
  | .ethereumTransactionProcessor => { runtimeType := "global::Nethermind.Evm.TransactionProcessing.EthereumTransactionProcessor", lineage := ["global::Nethermind.Evm.TransactionProcessing.EthereumTransactionProcessor", "global::Nethermind.Evm.TransactionProcessing.EthereumTransactionProcessorBase", "global::Nethermind.Evm.TransactionProcessing.TransactionProcessorBase<global::Nethermind.Evm.GasPolicy.EthereumGasPolicy>"], isSealed := true, declaresRefund := false, declaresPayRefund := false }
  | .balTransactionProcessor => { runtimeType := "global::Nethermind.Evm.TransactionProcessing.TransactionProcessor<global::Nethermind.Evm.GasPolicy.EthereumGasPolicy>", lineage := ["global::Nethermind.Evm.TransactionProcessing.TransactionProcessor<global::Nethermind.Evm.GasPolicy.EthereumGasPolicy>", "global::Nethermind.Evm.TransactionProcessing.TransactionProcessorBase<global::Nethermind.Evm.GasPolicy.EthereumGasPolicy>"], isSealed := true, declaresRefund := false, declaresPayRefund := false }

def resolve (_receiver : ReceiverKind) (_method : MethodKind) : String := baseProcessor

structure CallEvidence where
  caller : String
  callee : String
  receiver : String
  sourcePath : String
  syntaxSha256 : String
  deriving DecidableEq, Repr

def executeSimpleTransferRefundCall : CallEvidence := { caller := "global::Nethermind.Evm.TransactionProcessing.TransactionProcessorBase<TGasPolicy>.ExecuteSimpleTransfer/15", callee := "global::Nethermind.Evm.TransactionProcessing.TransactionProcessorBase<TGasPolicy>.Refund/12", receiver := "containingInstance", sourcePath := "src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs", syntaxSha256 := "acd70f59fe634d9147c7f67c33c5e0a7c72520f4527624b5ca9d0bc59427a4df" }
def refundPayRefundCall : CallEvidence := { caller := "global::Nethermind.Evm.TransactionProcessing.TransactionProcessorBase<TGasPolicy>.Refund/12", callee := "global::Nethermind.Evm.TransactionProcessing.TransactionProcessorBase<TGasPolicy>.PayRefund/3", receiver := "containingInstance", sourcePath := "src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs", syntaxSha256 := "fc5079a4f348d3a81b8d2a4ce897dac6f13ecdbcb9576585e2dc3ad7835a5702" }

end SimpleTransferCompletionExtractor.StageB.Dispatch.Generated
