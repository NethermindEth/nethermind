// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.JsonRpc.Modules
{
    public class RpcModuleAttribute : Attribute
    {
        public string ModuleType { get; }

        public RpcModuleAttribute(string moduleType)
        {
            ModuleType = moduleType;
        }
    }
}
