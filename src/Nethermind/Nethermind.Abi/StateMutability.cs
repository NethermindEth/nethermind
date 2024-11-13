// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Nethermind.Core.JsonConverters;

namespace Nethermind.Abi
{
    [JsonConverter(typeof(LowerCaseJsonStringEnumConverter<StateMutability>))]
    public enum StateMutability
    {
        Pure,
        View,
        NonPayable,
        Payable,
    }
}
