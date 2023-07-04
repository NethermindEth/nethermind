// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Newtonsoft.Json;

namespace Nethermind.JsonRpc.Modules
{
    public interface IRpcModuleFactory<out T> where T : IRpcModule
    {
        T Create();

        IReadOnlyCollection<JsonConverter> GetConverters();
    }
}
