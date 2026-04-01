// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.StateComposition;

/// <summary>
/// Thrown for truly exceptional infrastructure failures in state composition
/// (e.g., config validation). Business logic errors (cooldown, scan in progress)
/// use <see cref="Nethermind.Core.Result{T}"/> instead.
/// </summary>
public class StateCompositionException : InvalidOperationException
{
    public StateCompositionException(string message) : base(message) { }
    public StateCompositionException(string message, Exception innerException) : base(message, innerException) { }
}
