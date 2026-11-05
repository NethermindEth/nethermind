// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Exceptions;

namespace Nethermind.Facade.Eth.RpcTransaction;

/// <summary>Thrown when a call request sets both <c>data</c> and <c>input</c> to different values.</summary>
/// <remarks>
/// Geth rejects this inside the call rather than while decoding the argument, so the JSON-RPC layer reports its
/// message without naming the argument.
/// </remarks>
public sealed class DataAndInputDifferException() : FormatException(RpcTransactionErrors.DataAndInputDiffer), IExceptionWithSafePublicMessage
{
    /// <inheritdoc/>
    public bool OmitArgumentPosition => true;
}
