// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core.Exceptions;

/// <summary>
/// A <see cref="FormatException"/> whose <see cref="Exception.Message"/> is safe to propagate to external callers.
/// </summary>
public sealed class SafePublicMessageFormatException(string message) : FormatException(message), IExceptionWithSafePublicMessage;
