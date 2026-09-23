// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Db;
using Nethermind.State.Flat.Persistence;

namespace Nethermind.State.Pbt.Image;

/// <summary>Owns exclusively pinned bootstrap inputs for the native PBT anchor import.</summary>
/// <remarks>The module's factory must pin the standard-flat anchor and keep source DBs immutable.
/// A null portable stream pair selects the offline source.</remarks>
internal abstract class PbtBootstrapLease : IDisposable
{
    public abstract PbtImageAnchor Anchor { get; }
    public abstract IPersistence.IPersistenceReader MptAnchor { get; }
    public abstract IColumnsDb<PbtColumns> Target { get; }
    public abstract string ScratchDirectory { get; }
    public virtual Stream? Snapshot => null;
    public virtual Stream? Preimages => null;
    public virtual IPersistence.IPersistenceReader? OfflineSource => null;
    public virtual IReadOnlyKeyValueStore? OfflineCode => null;
    public abstract bool IsAnchorCurrent();
    public abstract void Dispose();
}
