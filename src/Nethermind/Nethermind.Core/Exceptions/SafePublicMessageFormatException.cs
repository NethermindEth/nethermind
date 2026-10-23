// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core.Exceptions;

/// <summary>
/// A <see cref="FormatException"/> whose <see cref="Exception.Message"/> is safe to propagate to external callers.
/// </summary>
/// <param name="message">The public message.</param>
/// <param name="omitArgumentPosition">See <see cref="IExceptionWithSafePublicMessage.OmitArgumentPosition"/>.</param>
public sealed class SafePublicMessageFormatException(string message, bool omitArgumentPosition) : FormatException(message), IExceptionWithSafePublicMessage
{
    /// <summary>Creates an exception whose public message includes the argument position when binding fails.</summary>
    /// <param name="message">The public message.</param>
    public SafePublicMessageFormatException(string message) : this(message, false) { }

    /// <inheritdoc/>
    public bool OmitArgumentPosition => omitArgumentPosition;
}
