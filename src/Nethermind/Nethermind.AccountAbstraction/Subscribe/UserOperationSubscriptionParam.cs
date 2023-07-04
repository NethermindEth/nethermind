// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.JsonRpc;
using Newtonsoft.Json;

namespace Nethermind.AccountAbstraction.Subscribe;

public class UserOperationSubscriptionParam : IJsonRpcParam
{
    public Address[] EntryPoints { get; set; } = Array.Empty<Address>();
    public bool IncludeUserOperations { get; set; }

    public void ReadJson(JsonSerializer serializer, string jsonValue)
    {
        UserOperationSubscriptionParam ep = serializer.Deserialize<UserOperationSubscriptionParam>(jsonValue.ToJsonTextReader())
                              ?? throw new ArgumentException($"Invalid 'entryPoints' filter: {jsonValue}");
        EntryPoints = ep.EntryPoints;
        IncludeUserOperations = ep.IncludeUserOperations;

    }
}
