// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core.Exceptions;

/// <summary>
/// Marker for exceptions whose <see cref="System.Exception.Message"/> is safe to expose to external callers.
/// </summary>
public interface IExceptionWithSafePublicMessage
{
    /// <summary>
    /// Whether <see cref="System.Exception.Message"/> already names the input it rejects, so it is exposed without the
    /// position of the JSON-RPC argument that carried that input.
    /// </summary>
    bool NamesInput => false;
}
