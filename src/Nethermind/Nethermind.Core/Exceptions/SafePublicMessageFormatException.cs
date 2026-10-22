// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core.Exceptions;

/// <summary>
/// A <see cref="FormatException"/> whose <see cref="Exception.Message"/> is safe to propagate to external callers.
/// </summary>
/// <param name="message">The public message.</param>
/// <param name="namesInput">See <see cref="IExceptionWithSafePublicMessage.NamesInput"/>.</param>
public sealed class SafePublicMessageFormatException(string message, bool namesInput = false) : FormatException(message), IExceptionWithSafePublicMessage
{
    /// <inheritdoc/>
    public bool NamesInput => namesInput;
}
