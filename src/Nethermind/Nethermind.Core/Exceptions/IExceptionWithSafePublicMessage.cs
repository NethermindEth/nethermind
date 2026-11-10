// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core.Exceptions;

/// <summary>
/// Marker for exceptions whose <see cref="System.Exception.Message"/> is safe to expose to external callers.
/// </summary>
public interface IExceptionWithSafePublicMessage
{
    /// <summary>
    /// Whether <see cref="System.Exception.Message"/> is exposed without the <c>invalid argument N:</c> prefix that names
    /// the position of the JSON-RPC argument that failed to bind.
    /// </summary>
    /// <remarks>
    /// Set for checks that Geth performs in the method handler after the arguments are decoded, so its errors for them
    /// carry no argument position.
    /// </remarks>
    bool OmitArgumentPosition => false;
}
