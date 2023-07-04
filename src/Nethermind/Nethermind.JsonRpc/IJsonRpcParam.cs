// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Newtonsoft.Json;

namespace Nethermind.JsonRpc;

public interface IJsonRpcParam
{
    void ReadJson(JsonSerializer serializer, string jsonValue);
}
