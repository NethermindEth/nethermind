-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

namespace SimpleTransferCompletionExtractor.StageB.Dispatch.Specification

inductive Receiver where
  | nonBalEthereum
  | balEthereum
  deriving DecidableEq, Repr

inductive Method where
  | refund
  | payRefund
  deriving DecidableEq, Repr

inductive DeclaringType where
  | transactionProcessorBaseEthereumGasPolicy
  deriving DecidableEq, Repr

def resolve (_receiver : Receiver) (_method : Method) : DeclaringType :=
  .transactionProcessorBaseEthereumGasPolicy

end SimpleTransferCompletionExtractor.StageB.Dispatch.Specification
