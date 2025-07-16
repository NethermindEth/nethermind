// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;

namespace Nethermind.Tools.Kute.JsonRpcSubmitter;

public interface IJsonRpcSubmitter
{
    Task<HttpResponseMessage?> Submit(JsonRpc rpc);
}
