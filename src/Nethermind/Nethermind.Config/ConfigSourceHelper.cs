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
                    value = GetValue(valueType, valueString);
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
            
            return nullableType == null
                ? Convert.ChangeType(itemValue, valueType)
                : !string.IsNullOrEmpty(itemValue) && !itemValue.Equals("null", StringComparison.InvariantCultureIgnoreCase) 
                    ? Convert.ChangeType(itemValue, nullableType) 
                    : null;
        }
    }
}
