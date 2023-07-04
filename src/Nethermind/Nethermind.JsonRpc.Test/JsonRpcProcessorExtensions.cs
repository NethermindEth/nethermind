// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.IO;

namespace Nethermind.JsonRpc.Test
{
    public static class JsonRpcProcessorExtensions
    {
        public static IAsyncEnumerable<JsonRpcResult> ProcessAsync(this IJsonRpcProcessor processor, string request, JsonRpcContext context) => processor.ProcessAsync(new StringReader(request), context);
    }
}
