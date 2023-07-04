// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core
{
    /// <summary>
    /// Journal is a collection-like type that allows saving and restoring a state through snapshots.
    /// </summary>
    /// <typeparam name="TSnapshot">Type representing state snapshot.</typeparam>
    public interface IJournal<TSnapshot>
    {
        /// <summary>
        /// Saves state to potentially restore later.
        /// </summary>
        /// <returns>State to potentially restore later.</returns>
        TSnapshot TakeSnapshot();

        /// <summary>
        /// Restores previously saved state.
        /// </summary>
        /// <param name="snapshot">Previously saved state.</param>
        /// <exception cref="InvalidOperationException">Thrown when snapshot cannot be restored. For example previous snapshot was already restored.</exception>
        void Restore(TSnapshot snapshot);
    }
}
