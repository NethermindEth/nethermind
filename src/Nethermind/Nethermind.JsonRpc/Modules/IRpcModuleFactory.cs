// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.JsonRpc.Modules
{
    public interface IRpcModuleFactory<out T> where T : IRpcModule
    {
        /// <summary>Creates a module instance; pools create lazily, so this may run concurrently on request threads.</summary>
        T Create();
    }
}
