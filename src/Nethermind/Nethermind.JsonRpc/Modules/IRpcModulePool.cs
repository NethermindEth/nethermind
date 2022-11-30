// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;

namespace Nethermind.JsonRpc.Modules
{
    public interface IRpcModulePool<T> where T : IRpcModule
    {
        Task<T> GetModule(bool canBeShared);

        void ReturnModule(T module);

        IRpcModuleFactory<T> Factory { get; set; }
    }
}
