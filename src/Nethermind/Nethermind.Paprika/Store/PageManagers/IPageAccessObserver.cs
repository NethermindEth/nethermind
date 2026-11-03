// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Paprika.Store.PageManagers;

/// <summary>
/// Observes the pages a <see cref="MemoryMappedPageManager"/> hands out, so a host can bound the resident set of its mapping.
/// </summary>
/// <remarks>
/// The page manager owns the observer. It calls <see cref="OnMapped"/> once, before any <see cref="OnAccess"/>, and
/// disposes the observer first in its own <see cref="IDisposable.Dispose"/>, before it unmaps, so the observer must not
/// touch the mapping after its <see cref="IDisposable.Dispose"/>. No <see cref="PointerPageManager.GetAt"/> may run while
/// the manager is disposed. A caller that fails to open a store still disposes the observer, which the store may have
/// disposed already, so <see cref="IDisposable.Dispose"/> must be idempotent.
/// </remarks>
public interface IPageAccessObserver : IDisposable
{
    /// <summary>
    /// Reports the mapping that holds the pages.
    /// </summary>
    /// <param name="basePointer">The address of page 0.</param>
    /// <param name="mappedBytes">The length of the mapping.</param>
    void OnMapped(nint basePointer, long mappedBytes);

    /// <summary>
    /// Reports an access to <paramref name="page"/>, root pages included.
    /// </summary>
    /// <remarks>
    /// Called on every <see cref="PointerPageManager.GetAt"/>, after its bounds check, and for every page of a run the
    /// manager writes to its file. It runs on the caller's thread, from any thread and concurrently.
    /// </remarks>
    /// <param name="page">The raw address of the page.</param>
    void OnAccess(uint page);
}
