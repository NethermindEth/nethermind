// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core.Exceptions;

/// <summary>
/// A <see cref="FormatException"/> whose <see cref="Exception.Message"/> is safe to propagate to external callers.
/// </summary>
/// <param name="message">The public message.</param>
/// <param name="innerException">The exception that caused this one, if any.</param>
/// <param name="omitArgumentPosition">See <see cref="IExceptionWithSafePublicMessage.OmitArgumentPosition"/>.</param>
public sealed class SafePublicMessageFormatException(string message, Exception? innerException, bool omitArgumentPosition)
    : FormatException(message, innerException), IExceptionWithSafePublicMessage
{
    /// <summary>Creates an exception with no inner exception.</summary>
    /// <param name="message">The public message.</param>
    /// <param name="omitArgumentPosition">See <see cref="IExceptionWithSafePublicMessage.OmitArgumentPosition"/>.</param>
    public SafePublicMessageFormatException(string message, bool omitArgumentPosition) : this(message, null, omitArgumentPosition) { }

    /// <summary>Creates an exception whose public message includes the argument position when binding fails.</summary>
    /// <param name="message">The public message.</param>
    /// <param name="innerException">The exception that caused this one.</param>
    public SafePublicMessageFormatException(string message, Exception? innerException) : this(message, innerException, false) { }

    /// <summary>Creates an exception whose public message includes the argument position when binding fails.</summary>
    /// <param name="message">The public message.</param>
    public SafePublicMessageFormatException(string message) : this(message, null, false) { }

    /// <inheritdoc/>
    public bool OmitArgumentPosition => omitArgumentPosition;
}
