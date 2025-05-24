// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;

namespace Nethermind.Tools.Kute.ResponseTracer;

public class NullResponseTracer : IResponseTracer
{
    public Task TraceResponse(JsonDocument? response) => Task.CompletedTask;
}
