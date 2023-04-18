// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nethermind.Int256;
using Newtonsoft.Json;

namespace Nethermind.Config
{
    internal static class ConfigSourceHelper
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
                        var objCollection = JsonConvert.DeserializeObject(valueString, valueType);
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
                                item = valueItem.Substring(1, valueItem.Length - 2);
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
                        value = JsonConvert.DeserializeObject(valueString, valueType);
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

            var nullableType = Nullable.GetUnderlyingType(valueType);

            return nullableType is null
                ? Convert.ChangeType(itemValue, valueType)
                : !string.IsNullOrEmpty(itemValue) && !itemValue.Equals("null", StringComparison.InvariantCultureIgnoreCase)
                    ? Convert.ChangeType(itemValue, nullableType)
                    : null;
        }
    }
}
