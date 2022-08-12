//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Nethermind.Abi;

namespace Nethermind.Blockchain.Contracts.Json
{
    internal static class AbiParameterConverterStatics
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
        internal static readonly Regex TypeExpression = new(@"^(?<T>u?int(?<M>\d{1,3})?|address|bool|u?fixed((?<M>\d{1,3})x(?<N>\d{1,2}))?|bytes(?<M>\d{1,3})?|function|string|tuple)(?<A>\[(?<L>\d+)?\])?$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);


        internal static readonly IDictionary<string, Func<int?, int?, AbiType>> SimpleTypeFactories = new Dictionary<string, Func<int?, int?, AbiType>>(StringComparer.InvariantCultureIgnoreCase)
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
    }

    public abstract class AbiParameterConverterBase<T> : JsonConverter<T> where T : AbiParameter, new()
    {
        private readonly IList<IAbiTypeFactory> _abiTypeFactories;

        protected AbiParameterConverterBase(IList<IAbiTypeFactory> abiTypeFactories)
        {
            _abiTypeFactories = abiTypeFactories;
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions op)
        {
            var simpleOp = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };
            simpleOp.Converters.Add(new AbiTypeConverter());
            JsonSerializer.Serialize(writer, value, simpleOp);
        }

        public override T Read(ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions op)
        {
            var token = JsonElement.ParseValue(ref reader);
            T value = new();
            Populate(value, token);
            return value;
        }

        protected virtual void Populate(T item, JsonElement token)
        {
            item.Name = GetName(token);
            item.Type = GetAbiType(token);
        }

        private AbiType GetAbiType(JsonElement token)
        {
            return token.TryGetProperty("components", out JsonElement components)
                ? GetParameterType(token.GetProperty(TypePropertyName).GetString()!, components)
                : GetParameterType(token.GetProperty(TypePropertyName).GetString()!, null);
        }


        private static string TypePropertyName => nameof(AbiParameter.Type).ToLowerInvariant();

        private static string GetName(JsonElement token) =>
            token.GetProperty(NamePropertyName).GetString()!;

        private static string NamePropertyName => nameof(AbiParameter.Name).ToLowerInvariant();

        private AbiType GetParameterType(string type, JsonElement? components)
        {
            var match = AbiParameterConverterStatics.TypeExpression.Match(type);
            if (match.Success)
            {
                var baseType = new string(match.Groups[AbiParameterConverterStatics.TypeGroup].Value.TakeWhile(char.IsLetter).ToArray());
                var baseAbiType = GetBaseType(baseType, match, components);
                return match.Groups[AbiParameterConverterStatics.ArrayGroup].Success
                    ? match.Groups[AbiParameterConverterStatics.LengthGroup].Success
                        ? new AbiFixedLengthArray(baseAbiType, int.Parse(match.Groups[AbiParameterConverterStatics.LengthGroup].Value))
                        : new AbiArray(baseAbiType)
                    : baseAbiType;
            }
            else
            {
                throw new ArgumentException($"Invalid contract ABI json. Unknown type {type}.");
            }
        }

        private AbiType GetBaseType(string baseType, Match match, JsonElement? components)
        {
            string GetAbiTypeName()
            {
                string name = baseType;
                if (components is not null)
                {
                    IEnumerable<string> innerTypes = components.Value.EnumerateArray()
                        .Select(c => c.GetProperty(TypePropertyName).GetString())!;
                    name = $"({string.Join(",", innerTypes)})";
                }

                return name;
            }

            string abiTypeName = GetAbiTypeName();

            foreach (IAbiTypeFactory factory in _abiTypeFactories)
            {
                AbiType? abiType = factory.Create(abiTypeName);
                if (abiType is not null)
                    return abiType;
            }

            if (AbiParameterConverterStatics.SimpleTypeFactories.TryGetValue(baseType, out var simpleTypeFactory))
            {
                int? m = match.Groups[AbiParameterConverterStatics.TypeLengthGroup].Success ? int.Parse(match.Groups[AbiParameterConverterStatics.TypeLengthGroup].Value) : null;
                int? n = match.Groups[AbiParameterConverterStatics.PrecisionGroup].Success ? int.Parse(match.Groups[AbiParameterConverterStatics.PrecisionGroup].Value) : null;
                return simpleTypeFactory(m, n);
            }
            else if (baseType == "tuple")
            {
                IEnumerable<JsonElement> children = components!.Value.EnumerateArray().ToArray();
                return new AbiTuple(children.Select(GetAbiType).ToArray(), children.Select(GetName).ToArray());
            }
            else
            {
                throw new NotSupportedException($"Abi doesn't support type '{baseType}'");
            }
        }
    }

    public class AbiParameterConverter : AbiParameterConverterBase<AbiParameter>
    {
        public AbiParameterConverter(IList<IAbiTypeFactory> abiTypeFactories) : base(abiTypeFactories)
        {
        }
    }

    public class AbiEventParameterConverter : AbiParameterConverterBase<AbiEventParameter>
    {
        public AbiEventParameterConverter(IList<IAbiTypeFactory> abiTypeFactories) : base(abiTypeFactories)
        {
        }

        protected override void Populate(AbiEventParameter item, JsonElement token)
        {
            base.Populate(item, token);
            if (token.TryGetProperty(nameof(AbiEventParameter.Indexed).ToLowerInvariant(), out JsonElement property))
                item.Indexed = property.GetBoolean();
        }
    }
}
