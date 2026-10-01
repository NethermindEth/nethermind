// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;

namespace Nethermind.JsonRpc.Modules.Trace;

/// <summary>
/// An unsigned trace call the processor rejected, answered with the <see cref="TransactionErrorCodes"/> code of its
/// rejection, as eth_simulateV1 answers it, rather than the generic code of any other rejected transaction.
/// </summary>
internal sealed class RejectedCallException(InvalidTransactionException rejection)
    : InvalidTransactionException(rejection.InvalidBlock, rejection.Message, rejection.Reason, rejection);
