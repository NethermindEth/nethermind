// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.JsonRpc.Modules
{
    public interface IRpcModuleFactory<out T> where T : IRpcModule
    {
        /// <summary>Creates a module instance on behalf of a module pool.</summary>
        /// <remarks>
        /// Pools create modules lazily from request threads. First-time creations are serialized per pool for
        /// compatibility with factories that are not safe for concurrent construction; implementations must still
        /// be safe to invoke by the owning pool throughout its lifetime.
        /// </remarks>
        T Create();
    }
}
