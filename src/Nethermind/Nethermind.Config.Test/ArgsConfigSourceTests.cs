// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace Nethermind.Config.Test
{
    [TestFixture]
    [Parallelizable(ParallelScope.All)]
    public class ArgsConfigSourceTests
    {
        [Test]
        public void Works_fine_with_unset_values()
        {
            Dictionary<string, string> args = new();
            ArgsConfigSource configSource = new(args);
            Assert.IsFalse(configSource.GetValue(typeof(int), "a", "a").IsSet);
        }

        [Test]
        public void Is_case_insensitive()
        {
            Dictionary<string, string> args = new();
            args.Add("A.a", "12");
            ArgsConfigSource configSource = new(args);
            Assert.IsTrue(configSource.GetValue(typeof(int), "a", "A").IsSet);
        }

        [TestCase(typeof(byte), "12", (byte)12)]
        [TestCase(typeof(int), "12", 12)]
        [TestCase(typeof(uint), "12", 12U)]
        [TestCase(typeof(long), "12", 12L)]
        [TestCase(typeof(ulong), "12", 12UL)]
        [TestCase(typeof(string), "12", "12")]
        [TestCase(typeof(bool), "false", false)]
        public void Can_parse_various_values(Type valueType, string valueString, object parsedValue)
        {
            Dictionary<string, string> args = new();
            args.Add("A.a", valueString);
            ArgsConfigSource configSource = new(args);
            Assert.AreEqual(parsedValue, configSource.GetValue(valueType, "a", "A").Value);
        }
    }
}
