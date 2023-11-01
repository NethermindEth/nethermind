// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Newtonsoft.Json;

namespace Nethermind.Serialization.Json.Nethermind.Serialization.Json;

public class DictionaryWithSpecialUInt256KeyHash256ValConverter : JsonConverter
{
    public static bool IsType(Type type, Type typeToBe)
    {

        if (!typeToBe.IsGenericTypeDefinition)
            return typeToBe.IsAssignableFrom(type);

        List<Type> toCheckTypes = new() { type };
        if (typeToBe.IsInterface)
            toCheckTypes.AddRange(type.GetInterfaces());

        Type basedOn = type;
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
        Type valueType = objectType.GetGenericArguments()[1];
        Type intermediateDictionaryType = typeof(Dictionary<,>).MakeGenericType(typeof(string), typeof(string));
        IDictionary intermediateDictionary = (IDictionary)Activator.CreateInstance(intermediateDictionaryType);
        serializer.Populate(reader, intermediateDictionary);

        IDictionary finalDictionary = (IDictionary)Activator.CreateInstance(objectType);
        foreach (DictionaryEntry pair in intermediateDictionary)
        {
            string keyData = pair.Key.ToString();
            UInt256 key = keyData.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? (UInt256)BigInteger.Parse(keyData.Substring(2), NumberStyles.HexNumber)
                : UInt256.Parse(keyData);

            string valueData = pair.Value.ToString();
            Hash256 val = new(valueData);

            finalDictionary.Add(key, val);
        }

        return finalDictionary;
    }

    public override bool CanConvert(Type objectType)
    {
        bool isDict = IsType(objectType, typeof(IDictionary<,>));
        if (!isDict) return false;

        var args = objectType.GetGenericArguments();
        if (args.Length < 2) return false;

        bool isKeyUint256 = IsType(args[0], typeof(UInt256));
        if (!isKeyUint256) return false;

        bool isValValueKeccak = IsType(args[1], typeof(Hash256));
        return isValValueKeccak;
    }
}
