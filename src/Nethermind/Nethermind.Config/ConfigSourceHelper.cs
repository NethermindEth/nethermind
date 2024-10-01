// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Int256;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.Config
{
    public static class ConfigSourceHelper
    {
        public static object ParseValue(Type valueType, string valueString, string category, string name)
        {
            try
            {
                object value;
                if (valueType.IsArray || (valueType.IsGenericType && valueType.GetGenericTypeDefinition() == typeof(IEnumerable<>)))
                {
                    //supports Arrays, e.g int[] and generic IEnumerable<T>, IList<T>
                    var itemType = valueType.IsGenericType ? valueType.GetGenericArguments()[0] : valueType.GetElementType();

                    //In case of collection of objects (more complex config models) we parse entire collection
                    if (itemType.IsClass && typeof(IConfigModel).IsAssignableFrom(itemType))
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
            switch (Type.GetTypeCode(type))
            {
                case TypeCode.Byte:
                    value = Byte.Parse(itemValue);
                    return true;
                case TypeCode.SByte:
                    value = SByte.Parse(itemValue);
                    return true;
                case TypeCode.UInt16:
                    value = UInt16.Parse(itemValue);
                    return true;
                case TypeCode.UInt32:
                    value = UInt32.Parse(itemValue);
                    return true;
                case TypeCode.UInt64:
                    value = UInt64.Parse(itemValue);
                    return true;
                case TypeCode.Int16:
                    value = Int16.Parse(itemValue);
                    return true;
                case TypeCode.Int32:
                    value = Int32.Parse(itemValue);
                    return true;
                case TypeCode.Int64:
                    value = Int64.Parse(itemValue);
                    return true;
                case TypeCode.Decimal:
                    value = Decimal.Parse(itemValue);
                    return true;
                case TypeCode.Double:
                    value = Double.Parse(itemValue);
                    return true;
                case TypeCode.Single:
                    value = Single.Parse(itemValue);
                    return true;
                default:
                    value = null;
                    return false;
            }
        }

        private static object GetValue(Type valueType, string itemValue)
        {
            if (valueType == typeof(UInt256))
            {
                return UInt256.Parse(itemValue);
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

            var nullableType = Nullable.GetUnderlyingType(valueType);

            return nullableType is null
                ? Convert.ChangeType(itemValue, valueType)
                : !string.IsNullOrEmpty(itemValue) && !itemValue.Equals("null", StringComparison.InvariantCultureIgnoreCase)
                    ? Convert.ChangeType(itemValue, nullableType)
                    : null;
        }
    }
}
