// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Tools.Kute.ResponseTracer;

public interface IResponseTracer
{
    Task TraceResponse(JsonRpc.Response response, CancellationToken token = default);
}
