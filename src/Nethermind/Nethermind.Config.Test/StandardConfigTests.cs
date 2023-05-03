// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
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

        private static void ForEachProperty(Action<PropertyInfo, object?> verifier)
        {
            string[] dlls = Directory.GetFiles(AppDomain.CurrentDomain.BaseDirectory, "Nethermind.*.dll").OrderBy(n => n).ToArray();
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

                    Type? implementationType = configType.Assembly.GetExportedTypes()
                        .SingleOrDefault(t => t.IsClass && configType.IsAssignableFrom(t));

                    if (implementationType is null)
                    {
                        throw new Exception($"Missing config implementation for {configType}");
                    }

                    object? instance = Activator.CreateInstance(implementationType);

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

        private static void CheckDescribedOrHidden(PropertyInfo property, object? instance)
        {
            ConfigItemAttribute? attribute = property.GetCustomAttribute<ConfigItemAttribute>();
            if (string.IsNullOrWhiteSpace(attribute?.Description) && !(attribute?.HiddenFromDocs ?? false))
            {
                ConfigCategoryAttribute? categoryLevel =
                    property.DeclaringType?.GetCustomAttribute<ConfigCategoryAttribute>();
                if (!(categoryLevel?.HiddenFromDocs ?? false))
                {
                    throw new AssertionException(
                        $"Config {instance?.GetType().Name}.{property.Name} has no description and is in the docs.");
                }
            }
        }

        private static void CheckDefault(PropertyInfo property, object? instance)
        {
            ConfigItemAttribute? attribute = property.GetCustomAttribute<ConfigItemAttribute>();
            if (attribute is null || attribute.DisabledForCli)
            {
                //there are properties without attribute - we don't pay attention to them 
                return;
            }

            string expectedValue = attribute.DefaultValue?.Trim('"') ?? "null";
            string actualValue;

            object? value = property.GetValue(instance);
            if (value is null)
            {
                actualValue = "null";
            }
            else if (value is bool)
            {
                actualValue = value.ToString()!.ToLowerInvariant();
            }
            else if (value is IList actualValueArray)
            {
                // there is a case when we have default value as [4, 8, 8] and we need to compare this string to int[] so removing brackets and whitespaces
                string[] expectedItems = expectedValue
                    .Trim('[').Trim(']')
                    .Replace(" ", string.Empty)
                    .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);


                int length = Math.Min(expectedItems.Length, actualValueArray.Count);
                for (int i = 0; i < length; i++)
                {
                    string? actualValueAtIndex = actualValueArray[i]?.ToString();
                    string expectedValueAtIndex = expectedItems[i];
                    Assert.That(expectedValueAtIndex, Is.EqualTo(actualValueAtIndex),
                        $"Property: {property.Name}, expected value at index {i}: <{expectedValueAtIndex}> but was <{actualValueAtIndex}>");
                }

                Assert.That(expectedItems.Length, Is.EqualTo(actualValueArray.Count),
                    $"Property: {property.Name}, expected value length: <{expectedItems.Length}> but was <{actualValueArray.Count}>");

                return;
            }
            else
            {
                actualValue = value.ToString()!;
            }

            Assert.That(expectedValue, Is.EqualTo(actualValue),
                $"Property: {property.Name}, expected value: <{expectedValue}> but was <{actualValue}>");
        }
    }
}
