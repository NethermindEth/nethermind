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

            string[] rulePatterns = {"Abc.*", "Cdf.efg"};
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
