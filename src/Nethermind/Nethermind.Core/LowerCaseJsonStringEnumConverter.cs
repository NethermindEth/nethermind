// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.Serialization.Json;

public class LowerCaseJsonStringEnumConverter : JsonStringEnumConverter
{
    public LowerCaseJsonStringEnumConverter() : base(namingPolicy: LowerCaseJsonNamingPolicy.Default)
    {
    }
}

public class LowerCaseJsonNamingPolicy : JsonNamingPolicy
{
    public static LowerCaseJsonNamingPolicy Default { get; } = new();

    public override string ConvertName(string name)
    {
        return name.ToLowerInvariant();
    }
}
