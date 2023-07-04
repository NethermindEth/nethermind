// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Nethermind.JsonRpc.Modules;
using Nethermind.Serialization.Json;

namespace Nethermind.JsonRpc
{
    public interface IJsonRpcProcessor
    {
        IAsyncEnumerable<JsonRpcResult> ProcessAsync(TextReader request, JsonRpcContext context);
    }
}
