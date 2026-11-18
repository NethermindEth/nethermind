// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Consensus.Validators;

/// <summary>
/// Validation called synchronously on this thread inside <see cref="Begin"/> leaves no record in the validator
/// decorators that remember verdicts.
/// </summary>
/// <remarks>
/// Those decorators wrap the validators every component shares and keep each verdict as the node's own. Code that
/// validates a block the node does not import, such as a builder submission, keeps its verdict to itself by
/// validating inside a scope.
/// </remarks>
internal static class UntrackedValidation
{
    [ThreadStatic]
    private static int _depth;

    public static bool IsActive => _depth > 0;

    public static Scope Begin()
    {
        _depth++;
        return new Scope(active: true);
    }

    /// <remarks>
    /// A ref struct, so the compiler refuses to keep a scope across an <c>await</c>, where it would end on another
    /// thread. A default instance ends nothing, and a scope disposed more than once never ends more scopes than
    /// were begun.
    /// </remarks>
    public readonly ref struct Scope : IDisposable
    {
        private readonly bool _active;

        internal Scope(bool active) => _active = active;

        public void Dispose()
        {
            if (_active && _depth > 0) _depth--;
        }
    }
}
