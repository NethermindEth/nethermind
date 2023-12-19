// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Text.RegularExpressions;

using Nethermind.Abi;

namespace Nethermind.Blockchain.Contracts.Json;

public abstract class AbiParameterConverterBase<T> : JsonConverter<T> where T : AbiParameter, new()
{
    private static readonly object _registerLock = new();
    private static IList<IAbiTypeFactory> _abiTypeFactories = Array.Empty<IAbiTypeFactory>();

    public static bool IsFactoryRegistered<TFactory>()
        where TFactory : IAbiTypeFactory
    {
        IList<IAbiTypeFactory> abiTypeFactories = _abiTypeFactories;
        foreach (var factory in abiTypeFactories)
        {
            if (factory is TFactory)
            {
                return true;
            }
        }

        return false;
    }

    public static void RegisterFactory<TFactory>(TFactory factory)
        where TFactory : IAbiTypeFactory
    {
        lock (_registerLock)
        {
            if (IsFactoryRegistered<TFactory>())
            {
                return;
            }

            List<IAbiTypeFactory> abiTypeFactories = new(_abiTypeFactories)
                {
                    factory
                };

            _abiTypeFactories = abiTypeFactories;
        }
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions op)
    {
        writer.WriteStartObject();
        writer.WriteString("name"u8, value.Name);
        writer.WriteString("type"u8, value.Type.Name);
        if (value is AbiEventParameter eventParameter)
        {
            writer.WriteBoolean("indexed"u8, eventParameter.Indexed);
        }
        writer.WriteEndObject();
    }

    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions op)
    {
        JsonElement token = JsonElement.ParseValue(ref reader);
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
        return token.TryGetProperty("components"u8, out JsonElement components)
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
            if (components is not null && components.Value.ValueKind != JsonValueKind.Null)
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
            {
                return abiType;
            }
        }

        if (AbiParameterConverterStatics.SimpleTypeFactories.TryGetValue(baseType, out var simpleTypeFactory))
        {
            int? m = match.Groups[AbiParameterConverterStatics.TypeLengthGroup].Success ? int.Parse(match.Groups[AbiParameterConverterStatics.TypeLengthGroup].Value) : (int?)null;
            int? n = match.Groups[AbiParameterConverterStatics.PrecisionGroup].Success ? int.Parse(match.Groups[AbiParameterConverterStatics.PrecisionGroup].Value) : (int?)null;
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
    public AbiParameterConverter() : base()
    {
    }
}

public class AbiEventParameterConverter : AbiParameterConverterBase<AbiEventParameter>
{
    public AbiEventParameterConverter() : base()
    {
    }

    protected override void Populate(AbiEventParameter item, JsonElement token)
    {
        base.Populate(item, token);
        if (token.TryGetProperty("indexed"u8, out JsonElement property))
        {
            item.Indexed = property.GetBoolean();
        }
    }
}
