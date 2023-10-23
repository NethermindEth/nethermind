// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Nethermind.Int256;
using Newtonsoft.Json;

namespace Nethermind.Serialization.Json;

//This is a temp solution to bypass https://github.com/NethermindEth/int256/pull/35
public class DictionaryWithSpecialUInt256KeyConverter : JsonConverter
{
    public static bool IsType(Type type, Type typeToBe)
    {

        if (!typeToBe.IsGenericTypeDefinition)
            return typeToBe.IsAssignableFrom(type);

        var toCheckTypes = new List<Type> { type };
        if (typeToBe.IsInterface)
            toCheckTypes.AddRange(type.GetInterfaces());

        var basedOn = type;
        while (basedOn.BaseType != null)
        {
            toCheckTypes.Add(basedOn.BaseType);
            basedOn = basedOn.BaseType;
        }

        return toCheckTypes.Any(x => x.IsGenericType && x.GetGenericTypeDefinition() == typeToBe);
    }

    public override bool CanWrite
    {
        get { return false; }
    }

    public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
    {
        throw new NotSupportedException();
    }

    public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
    {
        if (reader.TokenType == JsonToken.Null)
            return null;

        // Assuming the keys in JSON are strings, and the Address type has a suitable Parse or TryParse method
        var valueType = objectType.GetGenericArguments()[1];
        var intermediateDictionaryType = typeof(Dictionary<,>).MakeGenericType(typeof(string), valueType);
        var intermediateDictionary = (IDictionary)Activator.CreateInstance(intermediateDictionaryType);
        serializer.Populate(reader, intermediateDictionary);

        var finalDictionary = (IDictionary)Activator.CreateInstance(objectType);
        foreach (DictionaryEntry pair in intermediateDictionary)
        {
            UInt256 key = (UInt256)BigInteger.Parse(pair.Key.ToString().Substring(2), NumberStyles.HexNumber);
            finalDictionary.Add(key, pair.Value);
        }

        return finalDictionary;
    }

    public override bool CanConvert(Type objectType)
    {
        bool isDict = IsType(objectType, typeof(IDictionary<,>));
        if (!isDict) return false;

        Type genericArgument = objectType.GetGenericArguments()[0];
        bool isKeyUint256 = IsType(genericArgument, typeof(UInt256));
        var res = isDict && isKeyUint256;
        return res;
    }
}
