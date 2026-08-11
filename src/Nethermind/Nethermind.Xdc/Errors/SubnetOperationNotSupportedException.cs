// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Xdc.Errors;

/// <summary>
/// Thrown when an operation has no subnet equivalent, so callers can report it rather than treat it as a fault.
/// </summary>
/// <remarks>
/// Distinct from a bare <see cref="NotSupportedException"/> so a caller catching this cannot also swallow an
/// unrelated one raised further down, which would lose that exception's log and stack trace.
/// </remarks>
/// <param name="operation">Describes the attempted operation; forms the start of the message.</param>
public class SubnetOperationNotSupportedException(string operation)
    : NotSupportedException($"{operation} is not supported on subnet chains")
{
    public string Operation { get; } = operation;
}
