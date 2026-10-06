// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.State.Flat.Persistence;

namespace Nethermind.State.Pbt.Image;

/// <summary>Owns exclusively pinned bootstrap inputs for the native PBT anchor import.</summary>
/// <remarks>The module's factory must keep source DBs immutable.
/// A snapshot is imported on its own, and preimages beside it only verify it; preimages alone take their values from
/// the offline source; with neither, the offline source is imported whole.</remarks>
internal abstract class PbtBootstrapLease : IDisposable
{
    public abstract PbtImageAnchor Anchor { get; }
    public abstract string ScratchDirectory { get; }
    public virtual Stream? Snapshot => null;
    public virtual Stream? Preimages => null;
    public virtual IPersistence.IPersistenceReader? OfflineSource => null;
    public virtual IReadOnlyKeyValueStore? OfflineCode => null;
    public abstract bool IsAnchorCurrent();
    public abstract void Dispose();
}
