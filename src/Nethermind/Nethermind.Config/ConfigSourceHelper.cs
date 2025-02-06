// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Int256;
using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.Config
{
    public static class ConfigSourceHelper
    {
        public static object ParseValue(Type valueType, string valueString, string category, string name)
        {
            if (Nullable.GetUnderlyingType(valueType) is { } nullableType)
            {
                return IsNullString(valueString) ? null : ParseValue(nullableType, valueString, category, name);
            }

            if (!valueType.IsValueType && IsNullString(valueString))
            {
                return null;
            }

            try
            {
                object value;
                if (valueType.IsArray || (valueType.IsGenericType && valueType.GetGenericTypeDefinition() == typeof(IEnumerable<>)))
                {
                    //supports Arrays, e.g int[] and generic IEnumerable<T>, IList<T>
                    var itemType = valueType.IsGenericType ? valueType.GetGenericArguments()[0] : valueType.GetElementType();

                    if (itemType == typeof(byte) && !valueString.AsSpan().TrimStart().StartsWith('['))
                    {
                        // hex encoded byte array
                        value = Bytes.FromHexString(valueString.Trim());
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
                        var valueItems = valueString.Split(',').Select(static s => s.Trim()).ToArray();
                        IList collection = (valueType.IsGenericType
                            ? (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(itemType))
                            : (IList)Activator.CreateInstance(valueType, valueItems.Length))!;

                        var i = 0;
                        foreach (string valueItem in valueItems)
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

        private static bool IsNullString(string valueString) =>
            valueString?.Equals("null", StringComparison.OrdinalIgnoreCase) ?? true;

        public static object GetDefault(Type type) => type.IsValueType ? (false, Activator.CreateInstance(type)) : (false, null);

        private static bool TryFromHex(Type type, string itemValue, out object value)
        {
            if (!itemValue.StartsWith("0x", StringComparison.Ordinal))
            {
                value = null;
                return false;
            }

            if (typeof(IConvertible).IsAssignableFrom(type) && type != typeof(string))
            {
                object baseValue = type == typeof(ulong)
                    ? Convert.ToUInt64(itemValue, 16) // Use UInt64 parsing for unsigned types to avoid overflow
                    : Convert.ToInt64(itemValue, 16); // Default to Int64 parsing for other integer types

                value = Convert.ChangeType(baseValue, type);
                return true;
            }

            value = null;
            return false;
        }

        private static object GetValue(Type valueType, string itemValue)
        {
            if (Nullable.GetUnderlyingType(valueType) is { } nullableType)
            {
                return IsNullString(itemValue) ? null : GetValue(nullableType, itemValue);
            }

            if (!valueType.IsValueType && IsNullString(itemValue))
            {
                return null;
            }

            if (valueType == typeof(UInt256))
            {
                return UInt256.Parse(itemValue);
            }

            if (valueType == typeof(Address))
            {
                return Address.TryParse(itemValue, out Address address)
                    ? address
                    : throw new FormatException($"Could not parse {itemValue} to {typeof(Address)}");
            }

            if (valueType == typeof(Hash256))
            {
                return new Hash256(itemValue);
            }

            if (valueType.IsEnum)
            {
                return Enum.TryParse(valueType, itemValue, true, out object enumValue)
                    ? enumValue
                    : throw new FormatException($"Cannot parse enum value: {itemValue}, type: {valueType.Name}");
            }

            return TryFromHex(valueType, itemValue, out object value) ? value : Convert.ChangeType(itemValue, valueType);
        }
    }
}
