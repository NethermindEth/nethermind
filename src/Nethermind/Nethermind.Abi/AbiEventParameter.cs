// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Nethermind.Blockchain.Contracts.Json;

namespace Nethermind.Abi
{
    [JsonConverter(typeof(AbiEventParameterConverter))]
    public class AbiEventParameter : AbiParameter
    {
        public bool Indexed { get; set; }
    }
}
