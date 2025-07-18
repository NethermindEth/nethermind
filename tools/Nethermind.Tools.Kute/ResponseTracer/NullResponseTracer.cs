// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Tools.Kute.ResponseTracer;

public sealed class NullResponseTracer : IResponseTracer
{
    public NullResponseTracer() { }

    public Task TraceResponse(JsonRpc.Response response) => Task.CompletedTask;
}
