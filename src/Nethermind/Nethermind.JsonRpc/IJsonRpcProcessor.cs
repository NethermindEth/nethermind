// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO.Pipelines;

namespace Nethermind.JsonRpc
{
    public interface IJsonRpcProcessor
    {
        IAsyncEnumerable<JsonRpcResult> ProcessAsync(PipeReader stream, JsonRpcContext context);
    }
}
