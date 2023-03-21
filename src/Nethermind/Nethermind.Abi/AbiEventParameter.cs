// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Contracts.Json;
using System.Text.Json.Serialization;

namespace Nethermind.Abi
{
    //[JsonConverter(typeof(AbiEventParameterConverter))]
    public class AbiEventParameter : AbiParameter
    {
        public bool Indexed { get; set; }
    }
}
