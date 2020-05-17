//  Copyright (c) 2018 Demerzel Solutions Limited
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
// 

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.Core2.Json
{
    public class LastConstructorJsonConverter<T> : JsonConverter<T>
    {
        private static ConstructorInfo? _constructorInfo;
        private static KeyValuePair<JsonEncodedText, Type>[]? _parameterNames;
        private static KeyValuePair<JsonEncodedText, MethodInfo>[]? _propertyGetAccessors;

        // NOTE: This will be built in to .NET 5.0
        // https://github.com/dotnet/runtime/blob/master/src/libraries/System.Text.Json/src/System/Text/Json/Serialization/Converters/Object/ObjectWithParameterizedConstructorConverter.cs
        // https://github.com/manne/obviously/tree/master/src/system.text.json
        
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            EnsureNames(options);
            object?[] parameters = new object?[_parameterNames!.Length];
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType == JsonTokenType.PropertyName)
                {
                    for (int index = 0; index < _parameterNames.Length; index++)
                    {
                        KeyValuePair<JsonEncodedText, Type> kvp = _parameterNames[index];
                        if (reader.ValueTextEquals(kvp.Key.EncodedUtf8Bytes))
                        {
                            reader.Read();
                            object parameterValue = JsonSerializer.Deserialize(ref reader, kvp.Value, options);
                            parameters[index] = parameterValue;
                            break;
                        }
                    }
                    // Ignore unmatched values
                }
            }

            return (T)_constructorInfo!.Invoke(parameters);
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            EnsureNames(options);
            writer.WriteStartObject();
            foreach (KeyValuePair<JsonEncodedText, MethodInfo> kvp in _propertyGetAccessors!)
            {
                writer.WritePropertyName(kvp.Key);
                object propertyValue = kvp.Value.Invoke(value, null);
                JsonSerializer.Serialize(writer, propertyValue, options);
            }
            writer.WriteEndObject();
        }

        private void EnsureNames(JsonSerializerOptions options)
        {
            if (_constructorInfo is null)
            {
                Type typeToConvert = typeof(T);
                ConstructorInfo[] constructors = typeToConvert.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
                ConstructorInfo lastConstructor = constructors.Last();
                ParameterInfo[] constructorParameters = lastConstructor.GetParameters();
                if (constructorParameters.Length == 0)
                {
                    throw new Exception(
                        $"Cannot convert type {typeToConvert.Name} as it does not have constructor parameters.");
                }

                Dictionary<JsonEncodedText, PropertyInfo> encodedNamePropertyInfoDictionary = new Dictionary<JsonEncodedText, PropertyInfo>();
                PropertyInfo[] properties =
                    typeToConvert.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                foreach (PropertyInfo propertyInfo in properties)
                {
                    JsonEncodedText encodedName = JsonEncodedText.Encode(
                        options.PropertyNamingPolicy.ConvertName(propertyInfo.Name),
                        options.Encoder);
                    encodedNamePropertyInfoDictionary[encodedName] = propertyInfo;
                }

                List<KeyValuePair<JsonEncodedText, Type>> parameterNames = new List<KeyValuePair<JsonEncodedText, Type>>();
                foreach (ParameterInfo parameterInfo in constructorParameters)
                {
                    JsonEncodedText encodedName = JsonEncodedText.Encode(
                        options.PropertyNamingPolicy.ConvertName(parameterInfo.Name),
                        options.Encoder);
                    if (!encodedNamePropertyInfoDictionary.ContainsKey(encodedName))
                    {
                        throw new Exception(
                            $"Cannot convert type {typeToConvert.Name} as constructor parameter {parameterInfo.Name} does not have a matching property.");
                    }

                    parameterNames.Add(KeyValuePair.Create(encodedName, parameterInfo.ParameterType));
                }

                List<JsonEncodedText> missingProperties = encodedNamePropertyInfoDictionary.Keys
                    .Where(x => !parameterNames.Exists(y => x.Equals(y.Key)))
                    .ToList();
                if (missingProperties.Count > 0)
                {
                    throw new Exception(
                        $"Cannot convert type {typeToConvert.Name} as there are {missingProperties.Count} properties that don't have a matching constructor parameter, starting with {missingProperties[0]}.");
                }

                IEnumerable<KeyValuePair<JsonEncodedText, MethodInfo>> propertyGetAccessors = encodedNamePropertyInfoDictionary
                    .OrderBy(x => x.Key.ToString())
                    .Select(x => KeyValuePair.Create(x.Key, x.Value.GetMethod));

                _propertyGetAccessors = propertyGetAccessors.ToArray();
                _parameterNames = parameterNames.ToArray();
                _constructorInfo = lastConstructor;
            }
        }
    }
}