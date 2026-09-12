// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using NLog.Config;
using NLog.Targets;
using NLog.Targets.Wrappers;
using NUnit.Framework;
using Level = NLog.LogLevel;

namespace Nethermind.Logging.NLog.Test
{
    [TestFixture]
    public class NLogManagerTests
    {
        [Test]
        public void Logger_name_is_set_to_full_class_name()
        {
            NLogManager manager = new("test", null);
            NLogLogger logger = (NLogLogger)manager.GetClassLogger<NLogManagerTests>().UnderlyingLogger;
            Assert.That(logger.Name, Is.EqualTo(GetType().FullName.Replace("Nethermind.", string.Empty)));
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
                        Assert.That(foundRules, Is.Not.Empty);
                    }
                    else
                    {
                        Assert.That(foundRules, Is.Empty);
                    }
                }

            }

            string[] rulePatterns = { "Abc.*", "Cdf.efg" };
            CheckRules(rulePatterns, false);
            string logRules = string.Join(";", rulePatterns.Select(r => $"{r}:Warn"));
            _ = new NLogManager("test", null, logRules);
            CheckRules(rulePatterns, true);
        }

        [Test]
        public void Create_removes_overwritten_rules()
        {
            // Reload the linked NLog.config into a fresh configuration so this test's outcome
            // does not depend on rule mutations left behind by other tests in this fixture.
            string configPath = Path.Combine(AppContext.BaseDirectory, "NLog.config");
            LogManager.Configuration = new XmlLoggingConfiguration(configPath);

            using (new NLogManager("test", null, "*:Error")) { }

            // The shipped catch-all rule writing to seq survives alongside the synthesised "*" rule;
            // every other shipped rule matches "*" and is overridden.
            Assert.That(LogManager.Configuration.LoggingRules, Has.Count.EqualTo(2));

            LoggingRule seqRule = LogManager.Configuration.LoggingRules.Single(r => r.Targets.Any(t => t.Name == "seq"));
            LoggingRule synthesisedRule = LogManager.Configuration.LoggingRules.Single(r => r != seqRule);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(seqRule.LoggerNamePattern, Is.EqualTo("*"));
                Assert.That(seqRule.Targets.Select(t => t.Name), Is.EqualTo(new[] { "seq" }));

                Assert.That(synthesisedRule.LoggerNamePattern, Is.EqualTo("*"));
                Assert.That(synthesisedRule.Targets.Select(t => t.Name), Does.Not.Contain("seq"));
                Assert.That(synthesisedRule.IsLoggingEnabledForLevel(global::NLog.LogLevel.Trace), Is.False);
                Assert.That(synthesisedRule.IsLoggingEnabledForLevel(global::NLog.LogLevel.Debug), Is.False);
                Assert.That(synthesisedRule.IsLoggingEnabledForLevel(global::NLog.LogLevel.Info), Is.False);
                Assert.That(synthesisedRule.IsLoggingEnabledForLevel(global::NLog.LogLevel.Warn), Is.False);
                Assert.That(synthesisedRule.IsLoggingEnabledForLevel(global::NLog.LogLevel.Error), Is.True);
                Assert.That(synthesisedRule.IsLoggingEnabledForLevel(global::NLog.LogLevel.Fatal), Is.True);
            }
        }

        [Test]
        public void Log_rules_do_not_write_to_seq_target()
        {
            // Reload the linked NLog.config into a fresh configuration so this test's outcome
            // does not depend on rule mutations left behind by other tests in this fixture.
            string configPath = Path.Combine(AppContext.BaseDirectory, "NLog.config");
            LogManager.Configuration = new XmlLoggingConfiguration(configPath);

            using (new NLogManager("test", null, "Synchronization.*:Trace"))
            {
                LoggingRule rule = LogManager.Configuration.LoggingRules.Single(r => r.LoggerNamePattern == "Synchronization.*");
                string[] targetNames = rule.Targets.Select(t => t.Name).ToArray();

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(targetNames, Does.Not.Contain("seq"));
                    Assert.That(targetNames, Does.Contain("file-async"));
                    Assert.That(targetNames, Does.Contain("auto-colored-console-async"));
                }
            }
        }

        private static LoggingConfiguration ConfigurationWith(params LoggingRule[] rules)
        {
            LoggingConfiguration configuration = new();
            foreach (LoggingRule rule in rules)
            {
                configuration.LoggingRules.Add(rule);
            }

            return configuration;
        }

        [TestCase("*:Warn", false, TestName = "Seq_writing_rules_survive_Init_LogRules(catch_all_override)")]
        [TestCase("Network.*:Trace", true, TestName = "Seq_writing_rules_survive_Init_LogRules(overlapping_namespace_override)")]
        public void Seq_writing_rules_survive_Init_LogRules(string logRules, bool fileRuleSurvives)
        {
            MemoryTarget seq = new() { Name = "seq" };
            MemoryTarget file = new() { Name = "file" };
            LoggingRule seqCatchAll = new("*", Level.Trace, Level.Fatal, seq);
            LoggingRule seqNetwork = new("Network.*", Level.Trace, Level.Fatal, seq);
            LoggingRule fileCatchAll = new("*", Level.Trace, Level.Fatal, file);
            LogManager.Configuration = ConfigurationWith(seqCatchAll, seqNetwork, fileCatchAll);

            using (new NLogManager("test", null, logRules))
            {
                IList<LoggingRule> rules = LogManager.Configuration.LoggingRules;
                // Identified by pattern and by not writing to seq, rather than "not one of the known
                // pre-set rules": other fixtures in this class leave undisposed NLogManager instances
                // subscribed to LogManager.ConfigurationChanged, which can inject unrelated rules into
                // this configuration too.
                string expectedPattern = logRules.Split(':')[0];
                LoggingRule synthesised = rules.Single(r => r.LoggerNamePattern == expectedPattern && r.Targets.All(t => t.Name != "seq"));

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(rules, Does.Contain(seqCatchAll));
                    Assert.That(rules, Does.Contain(seqNetwork));
                    Assert.That(seqCatchAll.Targets.Select(t => t.Name), Is.EqualTo(new[] { "seq" }));
                    Assert.That(seqNetwork.Targets.Select(t => t.Name), Is.EqualTo(new[] { "seq" }));
                    Assert.That(seqCatchAll.IsLoggingEnabledForLevel(Level.Trace), Is.True);
                    Assert.That(seqNetwork.IsLoggingEnabledForLevel(Level.Trace), Is.True);
                    Assert.That(synthesised.Targets.Select(t => t.Name), Does.Not.Contain("seq"));
                    Assert.That(rules.Contains(fileCatchAll), Is.EqualTo(fileRuleSurvives));
                }
            }
        }

        [Test]
        public void Group_target_containing_seq_is_excluded_when_walked()
        {
            MemoryTarget seq = new() { Name = "seq" };
            MemoryTarget file = new() { Name = "file" };
            SplitGroupTarget all = new() { Name = "all" };
            all.Targets.Add(seq);
            all.Targets.Add(file);
            LoggingRule ruleAll = new("*", Level.Trace, Level.Fatal, all);
            LoggingRule ruleFile = new("*", Level.Trace, Level.Fatal, file);
            LogManager.Configuration = ConfigurationWith(ruleAll, ruleFile);

            using (new NLogManager("test", null, "Synchronization.*:Trace"))
            {
                LoggingRule synthesised = LogManager.Configuration.LoggingRules.Single(r => r.LoggerNamePattern == "Synchronization.*");
                string[] targetNames = synthesised.Targets.Select(t => t.Name).ToArray();

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(targetNames, Does.Not.Contain("all"));
                    Assert.That(targetNames, Does.Not.Contain("seq"));
                    Assert.That(targetNames, Does.Contain("file"));
                }
            }
        }

        [Test]
        public void Rule_reaching_seq_through_a_group_is_not_overridden_by_Init_LogRules()
        {
            MemoryTarget seq = new() { Name = "seq" };
            MemoryTarget file = new() { Name = "file" };
            SplitGroupTarget all = new() { Name = "all" };
            all.Targets.Add(seq);
            all.Targets.Add(file);
            LoggingRule networkAll = new("Network.*", Level.Trace, Level.Fatal, all);
            LoggingRule fileCatchAll = new("*", Level.Trace, Level.Fatal, file);
            LogManager.Configuration = ConfigurationWith(networkAll, fileCatchAll);

            using (new NLogManager("test", null, "Network.*:Warn"))
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(LogManager.Configuration.LoggingRules, Does.Contain(networkAll));
                    Assert.That(networkAll.IsLoggingEnabledForLevel(Level.Trace), Is.True);
                }
            }
        }

        [Test]
        public void No_rule_synthesised_when_every_target_reaches_seq()
        {
            MemoryTarget seq = new() { Name = "seq" };
            LoggingRule seqCatchAll = new("*", Level.Trace, Level.Fatal, seq);
            LogManager.Configuration = ConfigurationWith(seqCatchAll);

            Assert.DoesNotThrow(() =>
            {
                using (new NLogManager("test", null, "Synchronization.*:Trace")) { }
            });

            Assert.That(LogManager.Configuration.LoggingRules.Any(r => r.LoggerNamePattern == "Synchronization.*"), Is.False);
        }
    }
}
