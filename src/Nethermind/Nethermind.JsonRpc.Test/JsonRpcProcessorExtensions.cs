// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Nethermind.JsonRpc.Test
{
    public static class JsonRpcProcessorExtensions
    {
        public static IAsyncEnumerable<(JsonRpcResult, IDisposable)> ProcessAsync(this IJsonRpcProcessor processor, string request, JsonRpcContext context) => processor.ProcessAsync(new MemoryStream(Encoding.UTF8.GetBytes(request)), context);
    }
}
