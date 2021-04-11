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
using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using NUnit.Framework;

namespace Nethermind.Monitoring.Test
{
    [TestFixture]
    public class MetricsTests
    {
        [Test]
        public void All_config_items_have_descriptions()
        {
            ValidateMetricsDescriptions();
        }

        public static void ValidateMetricsDescriptions()
        {
            ForEachProperty(CheckDescribedOrHidden);
        }

        private static void CheckDescribedOrHidden(PropertyInfo property)
        {
            System.ComponentModel.DescriptionAttribute attribute = property.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>();
            attribute.Should().NotBeNull();
        }

        private static void ForEachProperty(Action<PropertyInfo> verifier)
        {
            string[] dlls = Directory.GetFiles(AppDomain.CurrentDomain.BaseDirectory, "Nethermind.*.dll");
            foreach (string dll in dlls)
            {
                TestContext.WriteLine($"Verify {nameof(MetricsTests)} on {Path.GetFileName(dll)}");
                Assembly assembly = Assembly.LoadFile(dll);
                Type[] configs = assembly.GetExportedTypes().Where(t => t.Name == "Metrics").ToArray();

                foreach (Type metricsType in configs)
                {
                    TestContext.WriteLine($"  Verifying type {metricsType.FullName}");
                    PropertyInfo[] properties = metricsType.GetProperties(BindingFlags.Static | BindingFlags.Public);
                    foreach (PropertyInfo property in properties)
                    {
                        try
                        {
                            TestContext.WriteLine($"    Verifying property {property.Name}");
                            verifier(property);
                        }
                        catch (Exception e)
                        {
                            throw new Exception(property.Name, e);
                        }
                    }
                }
            }
        }
    }
}
