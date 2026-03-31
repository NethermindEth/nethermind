// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.StateComposition;

/// <summary>
/// Thrown when a state composition operation cannot be completed due to
/// business logic constraints (cooldown active, scan already running, etc.).
/// </summary>
public class StateCompositionException : InvalidOperationException
{
    public StateCompositionException(string message) : base(message) { }
    public StateCompositionException(string message, Exception innerException) : base(message, innerException) { }
}
