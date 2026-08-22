// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat;

/// <summary>Which subsystem the current thread is reading persistence on behalf of.</summary>
/// <remarks>
/// Diagnostic only. Set once per unit of work rather than per read, so the cost is a thread-static
/// store at a scope boundary. Threads that never opt in are attributed as <see cref="Role.Other"/>.
/// </remarks>
public static class PersistenceReadRole
{
    public enum Role : byte { Other = 0, Warmer = 1, Commit = 2 }

    [ThreadStatic]
    private static Role _current;

    public static Role Current => _current;

    /// <summary>Tags the calling thread for the lifetime of the returned scope.</summary>
    public static Scope Enter(Role role) => new(role);

    public readonly struct Scope : IDisposable
    {
        private readonly Role _previous;

        internal Scope(Role role)
        {
            _previous = _current;
            _current = role;
        }

        public void Dispose() => _current = _previous;
    }
}
