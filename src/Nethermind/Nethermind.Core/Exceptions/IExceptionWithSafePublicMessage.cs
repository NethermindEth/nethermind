// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core.Exceptions;

/// <summary>
/// Marker for exceptions whose <see cref="System.Exception.Message"/> is safe to expose to external callers.
/// </summary>
public interface IExceptionWithSafePublicMessage
{
}
