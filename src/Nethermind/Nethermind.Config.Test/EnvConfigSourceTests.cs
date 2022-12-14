// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using NUnit.Framework;

namespace Nethermind.Config.Test
{
    [TestFixture]
    public class EnvConfigSourceTests
    {
        [Test]
        public void Works_fine_with_unset_values()
        {
            EnvConfigSource configSource = new();
            Assert.IsFalse(configSource.GetValue(typeof(int), "b", "a").IsSet);
        }

        [Test]
        public void Is_case_insensitive()
        {
            EnvConfigSource configSource = new();
            Environment.SetEnvironmentVariable("NETHERMIND_A_A", "12", EnvironmentVariableTarget.Process);
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
            Environment.SetEnvironmentVariable("NETHERMIND_A_A", valueString, EnvironmentVariableTarget.Process);
            EnvConfigSource configSource = new();
            Assert.AreEqual(parsedValue, configSource.GetValue(valueType, "a", "A").Value);
        }
    }
}
