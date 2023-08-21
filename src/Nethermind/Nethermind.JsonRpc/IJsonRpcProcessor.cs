// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.IO;

namespace Nethermind.JsonRpc
{
    public interface IJsonRpcProcessor
    {
        IAsyncEnumerable<JsonRpcResult> ProcessAsync(TextReader request, JsonRpcContext context);
    }
}
