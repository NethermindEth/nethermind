// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;

namespace Nethermind.JsonRpc;

public interface IJsonRpcParam
{
    void ReadJson(JsonElement jsonValue, JsonSerializerOptions options);
}
