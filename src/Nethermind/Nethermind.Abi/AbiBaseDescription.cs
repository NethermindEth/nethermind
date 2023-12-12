// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Text.Json.Serialization;

using Nethermind.Core.Crypto;

namespace Nethermind.Abi
{
    [JsonDerivedType(typeof(AbiEventDescription))]
    [JsonDerivedType(typeof(AbiFunctionDescription))]
    [JsonDerivedType(typeof(AbiErrorDescription))]
    public abstract class AbiBaseDescription
    {
        public AbiDescriptionType Type { get; set; } = AbiDescriptionType.Function;
        public string Name { get; set; } = string.Empty;
    }

    public abstract class AbiBaseDescription<T> : AbiBaseDescription where T : AbiParameter
    {
        private AbiSignature? _callSignature;

        public T[] Inputs { get; set; } = Array.Empty<T>();

        public AbiEncodingInfo GetCallInfo(AbiEncodingStyle encodingStyle = AbiEncodingStyle.IncludeSignature) =>
            new(encodingStyle, _callSignature ??= new AbiSignature(Name, Inputs.Select(i => i.Type).ToArray()));

        public Hash256 GetHash() => GetCallInfo().Signature.Hash;

    }
}
