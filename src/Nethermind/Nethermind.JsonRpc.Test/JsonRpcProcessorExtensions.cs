// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Text;

namespace Nethermind.JsonRpc.Test
{
    public static class JsonRpcProcessorExtensions
    {
        public static IAsyncEnumerable<JsonRpcResult> ProcessAsync(this IJsonRpcProcessor processor, string request, JsonRpcContext context) => processor.ProcessAsync(PipeReader.Create(new MemoryStream(Encoding.UTF8.GetBytes(request))), context);
    }
}
