// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.JsonRpc.Modules
{
    public interface IRpcModuleFactory<out T> where T : IRpcModule
    {
        T Create();
    }
}
