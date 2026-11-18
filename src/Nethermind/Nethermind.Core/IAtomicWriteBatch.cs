// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core;

/// <summary>A write batch that discards pending writes on disposal unless explicitly committed.</summary>
public interface IAtomicWriteBatch : IWriteBatch
{
    /// <summary>Atomically publishes all pending writes.</summary>
    /// <remarks>Any commit attempt closes the batch.</remarks>
    void Commit();
}
