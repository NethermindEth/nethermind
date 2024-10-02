// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Int256;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Config
{
    public static class ConfigSourceHelper
    {
        public static object ParseValue(Type valueType, string valueString, string category, string name)
        {
            if (Nullable.GetUnderlyingType(valueType) is var nullableType && nullableType is not null)
            {
                if (string.IsNullOrEmpty(valueString) || valueString.Equals("null", StringComparison.InvariantCultureIgnoreCase))
                {
                    return null;
                }

                return ParseValue(nullableType, valueString, category, name);
            }
            try
            {
                object value;
                if (valueType.IsArray || (valueType.IsGenericType && valueType.GetGenericTypeDefinition() == typeof(IEnumerable<>)))
                {
                    //supports Arrays, e.g int[] and generic IEnumerable<T>, IList<T>
                    var itemType = valueType.IsGenericType ? valueType.GetGenericArguments()[0] : valueType.GetElementType();

                    if (itemType == typeof(byte) && !valueString.Trim().StartsWith('['))
                    {
                        // hex encoded byte array
                        string hex = valueString.Trim().RemoveStart('0').RemoveStart('x').TrimEnd();
                        value = Enumerable.Range(0, hex.Length)
                            .Where(x => x % 2 == 0)
                            .Select(x => Convert.ToByte(hex.Substring(x, 2), 16))
                            .ToArray();
                    }
                    //In case of collection of objects (more complex config models) we parse entire collection
                    else if (itemType.IsClass && typeof(IConfigModel).IsAssignableFrom(itemType))
                    {
                        var objCollection = JsonSerializer.Deserialize(valueString, valueType);
                        value = objCollection;
                    }
                    else
                    {
                        valueString = valueString.Trim().RemoveStart('[').RemoveEnd(']');
                        var valueItems = valueString.Split(',').Select(s => s.Trim()).ToArray();
                        var collection = valueType.IsGenericType
                            ? (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(itemType))
                            : (IList)Activator.CreateInstance(valueType, valueItems.Length);

                        var i = 0;
                        foreach (var valueItem in valueItems)
                        {
                            string item = valueItem;
                            if (valueItem.StartsWith('"') && valueItem.EndsWith('"'))
                            {
                                item = valueItem[1..^1];
                            }

                            var itemValue = GetValue(itemType, item);
                            if (valueType.IsGenericType)
                            {
                                collection.Add(itemValue);
                            }
                            else
                            {
                                collection[i] = itemValue;
                                i++;
                            }
                        }

                        value = collection;
                    }
                }
                else
                {
                    try
                    {
                        value = GetValue(valueType, valueString);
                    }
                    catch (InvalidCastException)
                    {
                        value = JsonSerializer.Deserialize(valueString, valueType);
                    }
                }

                return value;
            }
            catch (Exception e)
            {
                throw new System.Configuration.ConfigurationErrorsException($"Could not load value {category}.{name}. See inner exception for details.", e);
            }
        }

        public static object GetDefault(Type type)
        {
            return type.IsValueType ? (false, Activator.CreateInstance(type)) : (false, null);
        }

        public static bool TryFromHex(Type type, string itemValue, out object value)
        {
            if (!itemValue.StartsWith("0x"))
            {
                value = null;
                return false;
            }
            switch (Type.GetTypeCode(type))
            {
                case TypeCode.Byte:
                    value = Convert.ToByte(itemValue, 16);
                    return true;
                case TypeCode.SByte:
                    value = Convert.ToSByte(itemValue, 16);
                    return true;
                case TypeCode.UInt16:
                    value = Convert.ToUInt16(itemValue, 16);
                    return true;
                case TypeCode.UInt32:
                    value = Convert.ToUInt32(itemValue, 16);
                    return true;
                case TypeCode.UInt64:
                    value = Convert.ToUInt64(itemValue, 16);
                    return true;
                case TypeCode.Int16:
                    value = Convert.ToInt16(itemValue, 16);
                    return true;
                case TypeCode.Int32:
                    value = Convert.ToInt32(itemValue, 16);
                    return true;
                case TypeCode.Int64:
                    value = Convert.ToInt64(itemValue, 16);
                    return true;
                default:
                    value = null;
                    return false;
            }
        }

        private static object GetValue(Type valueType, string itemValue)
        {
            if (Nullable.GetUnderlyingType(valueType) is var nullableType && nullableType is not null)
            {
                if (string.IsNullOrEmpty(itemValue) || itemValue.Equals("null", StringComparison.InvariantCultureIgnoreCase))
                {
                    return null;
                }

                return GetValue(nullableType, itemValue);
            }

            if (valueType == typeof(UInt256))
            {
                return UInt256.Parse(itemValue);
            }

            if (valueType == typeof(Address))
            {
                if (Address.TryParse(itemValue, out var address))
                {
                    return address;
                }
                throw new FormatException($"Could not parse {itemValue} to {typeof(Address)}");
            }

            if (valueType == typeof(Hash256))
            {
                return new Hash256(itemValue);
            }

            if (valueType.IsEnum)
            {
                if (Enum.TryParse(valueType, itemValue, true, out var enumValue))
                {
                    return enumValue;
                }

                throw new FormatException($"Cannot parse enum value: {itemValue}, type: {valueType.Name}");
            }

            if (TryFromHex(valueType, itemValue, out object value))
            {
                return value;
            }

            return Convert.ChangeType(itemValue, valueType);
        }
    }
}
