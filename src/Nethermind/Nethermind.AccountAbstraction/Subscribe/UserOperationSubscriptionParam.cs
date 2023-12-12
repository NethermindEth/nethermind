// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;

using Nethermind.Core;
using Nethermind.JsonRpc;

namespace Nethermind.AccountAbstraction.Subscribe;

public class UserOperationSubscriptionParam : IJsonRpcParam
{
    public Address[] EntryPoints { get; set; } = Array.Empty<Address>();
    public bool IncludeUserOperations { get; set; }

    public void ReadJson(JsonElement jsonValue, JsonSerializerOptions options)
    {
        UserOperationSubscriptionParam ep = JsonSerializer.Deserialize<UserOperationSubscriptionParam>(jsonValue, options)
                              ?? throw new ArgumentException($"Invalid 'entryPoints' filter: {jsonValue}");
        EntryPoints = ep.EntryPoints;
        IncludeUserOperations = ep.IncludeUserOperations;
    }
}
