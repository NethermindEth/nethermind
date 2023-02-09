// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using NLog;
using NLog.Config;
using NLog.Targets;
using NUnit.Framework;

namespace Nethermind.Logging.NLog.Test
{
    [TestFixture]
    public class NLogManagerTests
    {
        [Test]
        public void Logger_name_is_set_to_full_class_name()
        {
            NLogManager manager = new NLogManager("test", null);
            NLogLogger logger = (NLogLogger)manager.GetClassLogger();
            Assert.AreEqual(GetType().FullName.Replace("Nethermind.", string.Empty), logger.Name);
        }

        [Test]
        public void Create_defines_rules_correctly()
        {
            void CheckRules(string[] rules, bool shouldExist)
            {
                for (int i = 0; i < rules.Length; i++)
                {
                    IEnumerable<LoggingRule> foundRules = LogManager.Configuration.LoggingRules.Where(r => r.LoggerNamePattern == rules[i]);
                    if (shouldExist)
                    {
                        foundRules.Should().NotBeEmpty();
                    }
                    else
                    {
                        foundRules.Should().BeEmpty();
                    }
                }

            }

            string[] rulePatterns = { "Abc.*", "Cdf.efg" };
            CheckRules(rulePatterns, false);
            string logRules = string.Join(";", rulePatterns.Select(r => $"{r}:Warn"));
            NLogManager manager = new("test", null, logRules);
            CheckRules(rulePatterns, true);
        }

        [Test]
        public void Create_removes_overwritten_rules()
        {
            NLogManager manager = new("test", null, "*:Error");
            LogManager.Configuration.LoggingRules.Should().BeEquivalentTo(
                new LoggingRule[] { new LoggingRule("*", global::NLog.LogLevel.Error, null) },
                c => c.Excluding(r => r.Targets));
        }
    }
}
