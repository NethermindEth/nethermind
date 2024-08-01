// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

using Nethermind.Blockchain.Contracts.Json;

namespace Nethermind.Abi
{
    [JsonConverter(typeof(AbiParameterConverter))]
    [JsonDerivedType(typeof(AbiEventParameter))]
    public class AbiParameter
    {
        public string Name { get; set; } = string.Empty;
        public AbiType Type { get; set; } = AbiType.UInt256;
    }
}

namespace Nethermind.Blockchain.Contracts.Json
{
    using Nethermind.Abi;

    public interface IAbiTypeFactory
    {
        AbiType? Create(string abiTypeSignature);
    }

    internal static partial class AbiParameterConverterStatics
    {
        internal const string TypeGroup = "T";
        internal const string TypeLengthGroup = "M";
        internal const string PrecisionGroup = "N";
        internal const string ArrayGroup = "A";
        internal const string LengthGroup = "L";

        /// <remarks>
        /// Groups:
        /// T - type or base type if array
        /// M - length of type https://solidity.readthedocs.io/en/v0.5.3/abi-spec.html#types 
        /// N - precision of type https://solidity.readthedocs.io/en/v0.5.3/abi-spec.html#types
        /// A - if matched type is array
        /// L - if matched, denotes length of fixed length array 
        /// </remarks>
        internal static readonly Regex TypeExpression = TypeExpressionRegex();

        internal static readonly Dictionary<string, Func<int?, int?, AbiType>> SimpleTypeFactories = new Dictionary<string, Func<int?, int?, AbiType>>(StringComparer.InvariantCultureIgnoreCase)
        {
            {"int", (m, n) => new AbiInt(m ?? 256)},
            {"uint", (m, n) => new AbiUInt(m ?? 256)},
            {"address", (m, n) => AbiType.Address},
            {"bool", (m, n) => AbiType.Bool},
            {"fixed", (m, n) => new AbiFixed(m ?? 128, n ?? 18)},
            {"ufixed", (m, n) => new AbiUFixed(m ?? 128, n ?? 18)},
            {"bytes", (m, n) => m.HasValue ?  new AbiBytes(m.Value) : AbiType.DynamicBytes},
            {"function", (m, n) => AbiType.Function},
            {"string", (m, n) => AbiType.String}
        };

        [GeneratedRegex("^(?<T>u?int(?<M>\\d{1,3})?|address|bool|u?fixed((?<M>\\d{1,3})x(?<N>\\d{1,2}))?|bytes(?<M>\\d{1,3})?|function|string|tuple)(?<A>\\[(?<L>\\d+)?\\])?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex TypeExpressionRegex();
    }
}
