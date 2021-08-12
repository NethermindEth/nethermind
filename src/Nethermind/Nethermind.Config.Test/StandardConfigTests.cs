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
// 

using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using NUnit.Framework;

namespace Nethermind.Config.Test
{
    public static class StandardConfigTests
    {
        public static void ValidateDefaultValues()
        {
            ForEachProperty(CheckDefault);
        }

        public static void ValidateDescriptions()
        {
            ForEachProperty(CheckDescribedOrHidden);
        }

        private static void ForEachProperty(Action<PropertyInfo, object> verifier)
        {
            var dlls = Directory.GetFiles(AppDomain.CurrentDomain.BaseDirectory, "Nethermind.*.dll").OrderBy(n => n).ToArray();
            foreach (string dll in dlls)
            {
                TestContext.WriteLine($"Verifying {nameof(StandardConfigTests)} on {Path.GetFileName(dll)}");
                Assembly assembly = Assembly.LoadFile(dll);
                Type[] configs =
                    assembly.GetExportedTypes().Where(t => typeof(IConfig).IsAssignableFrom(t) && t.IsInterface)
                        .ToArray();

                foreach (Type configType in configs)
                {
                    TestContext.WriteLine($"  Verifying type {configType.Name}");
                    PropertyInfo[] properties = configType.GetProperties();

                    Type implementationType = configType.Assembly.GetExportedTypes()
                        .SingleOrDefault(t => t.IsClass && configType.IsAssignableFrom(t));
                    object instance = Activator.CreateInstance(implementationType);

                    foreach (PropertyInfo property in properties)
                    {
                        try
                        {
                            TestContext.WriteLine($"    Verifying property {property.Name}");
                            verifier(property, instance);
                        }
                        catch (Exception e)
                        {
                            throw new Exception($"{configType.Name}.{property.Name}", e);
                        }
                    }
                }
            }
        }

        private static void CheckDescribedOrHidden(PropertyInfo property, object instance)
        {
            ConfigItemAttribute attribute = property.GetCustomAttribute<ConfigItemAttribute>();
            if (string.IsNullOrWhiteSpace(attribute?.Description) && !(attribute?.HiddenFromDocs ?? false))
            {
                ConfigCategoryAttribute categoryLevel =
                    property.DeclaringType?.GetCustomAttribute<ConfigCategoryAttribute>();
                if (!(categoryLevel?.HiddenFromDocs ?? false))
                {
                    throw new AssertionException(
                        $"Config {instance?.GetType().Name}.{property.Name} has no description and is in the docs.");
                }
            }
        }

        private static void CheckDefault(PropertyInfo property, object instance)
        {
            ConfigItemAttribute attribute = property.GetCustomAttribute<ConfigItemAttribute>();
            if (attribute == null)
            {
                //there are properties without attribute - we don't pay attention to them 
                return;
            }

            string expectedValue = attribute.DefaultValue?.Trim('"') ?? "null";
            string actualValue;

            object value = property.GetValue(instance);
            if (value == null)
            {
                actualValue = "null";
            }
            else if (value is bool)
            {
                actualValue = value.ToString()?.ToLowerInvariant();
            }
            else if (value is IList actualValueArray)
            {
                // there is a case when we have default value as [4, 8, 8] and we need to compare this string to int[] so removing brackets and whitespaces
                string[] expectedItems = expectedValue
                    .Trim('[').Trim(']')
                    .Replace(" ", "")
                    .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);


                int length = Math.Min(expectedItems.Length, actualValueArray.Count);
                for (int i = 0; i < length; i++)
                {
                    string? actualValueAtIndex = actualValueArray[i]?.ToString();
                    string expectedValueAtIndex = expectedItems[i];
                    Assert.AreEqual(actualValueAtIndex, expectedValueAtIndex,
                        $"Property: {property.Name}, expected value at index {i}: <{expectedValueAtIndex}> but was <{actualValueAtIndex}>");
                }

                Assert.AreEqual(actualValueArray.Count, expectedItems.Length,
                    $"Property: {property.Name}, expected value length: <{expectedItems.Length}> but was <{actualValueArray.Count}>");

                return;
            }
            else
            {
                actualValue = value.ToString();
            }

            Assert.AreEqual(actualValue, expectedValue,
                $"Property: {property.Name}, expected value: <{expectedValue}> but was <{actualValue}>");
        }
    }
}
