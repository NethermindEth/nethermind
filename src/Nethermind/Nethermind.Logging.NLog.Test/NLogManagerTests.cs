// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using NLog.Common;
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
        /// <remarks>
        /// Every test in this fixture mutates the ambient <see cref="LogManager.Configuration"/>, so each one
        /// starts from a freshly loaded copy of the shipped config rather than from whatever its predecessor left.
        /// </remarks>
        [SetUp]
        public void SetUp() => LogManager.Configuration = ShippedConfiguration();

        [Test]
        public void Logger_name_is_set_to_full_class_name()
        {
            using NLogManager manager = new("test", null);
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
            using (new NLogManager("test", null, logRules)) { }
            CheckRules(rulePatterns, true);
        }

        [Test]
        public void Create_removes_overwritten_rules()
        {
            using (new NLogManager("test", null, "*:Error")) { }

            // Every shipped rule that does not reach seq matches "*" and is replaced by the synthesised one,
            // so exactly one non-seq rule is left however many rules NLog.config grows.
            LoggingRule seqRule = LogManager.Configuration.LoggingRules.Single(r => r.Targets.Any(t => t.Name == "seq"));
            LoggingRule synthesisedRule = LogManager.Configuration.LoggingRules.Single(r => r.Targets.All(t => t.Name != "seq"));

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

        /// <summary>Loads the linked <c>NLog.config</c> into a fresh configuration.</summary>
        private static LoggingConfiguration ShippedConfiguration() =>
            new XmlLoggingConfiguration(Path.Combine(AppContext.BaseDirectory, "NLog.config"));

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
                // The synthesised rule shares its pattern with a pre-set seq rule in the catch-all case,
                // so it is identified by not writing to seq as well as by pattern.
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

        [Test]
        public void Init_LogRules_that_cannot_be_applied_are_reported()
        {
            MemoryTarget seq = new() { Name = "seq" };
            LogManager.Configuration = ConfigurationWith(new LoggingRule("*", Level.Trace, Level.Fatal, seq));

            TextWriter previousWriter = InternalLogger.LogWriter;
            Level previousLevel = InternalLogger.LogLevel;
            using StringWriter internalLog = new();
            InternalLogger.LogWriter = internalLog;
            InternalLogger.LogLevel = Level.Warn;
            try
            {
                using (new NLogManager("test", null, "Synchronization.*:Trace")) { }
            }
            finally
            {
                InternalLogger.LogWriter = previousWriter;
                InternalLogger.LogLevel = previousLevel;
            }

            Assert.That(internalLog.ToString(), Does.Contain("Synchronization.*:Trace"));
        }

        [Test]
        public void Wrapper_target_around_seq_is_excluded_from_Init_LogRules()
        {
            MemoryTarget seq = new() { Name = "seq" };
            MemoryTarget file = new() { Name = "file" };
            BufferingTargetWrapper buffered = new("seq-buffer", seq);
            LogManager.Configuration = ConfigurationWith(
                new LoggingRule("*", Level.Trace, Level.Fatal, buffered),
                new LoggingRule("*", Level.Trace, Level.Fatal, file));

            using (new NLogManager("test", null, "Synchronization.*:Trace"))
            {
                LoggingRule synthesised = LogManager.Configuration.LoggingRules.Single(r => r.LoggerNamePattern == "Synchronization.*");
                string[] targetNames = synthesised.Targets.Select(t => t.Name).ToArray();

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(targetNames, Does.Not.Contain("seq-buffer"));
                    Assert.That(targetNames, Does.Not.Contain("seq"));
                    Assert.That(targetNames, Does.Contain("file"));
                }
            }
        }

        [Test]
        public void Target_graph_with_a_cycle_is_walked_once()
        {
            MemoryTarget file = new() { Name = "file" };
            MemoryTarget seq = new() { Name = "seq" };
            SplitGroupTarget cyclic = new() { Name = "cyclic" };
            cyclic.Targets.Add(file);
            cyclic.Targets.Add(seq);
            cyclic.Targets.Add(cyclic);
            LogManager.Configuration = ConfigurationWith(new LoggingRule("*", Level.Trace, Level.Fatal, cyclic));

            try
            {
                using (new NLogManager("test", null, "Synchronization.*:Trace"))
                {
                    LoggingRule synthesised = LogManager.Configuration.LoggingRules.Single(r => r.LoggerNamePattern == "Synchronization.*");
                    string[] targetNames = synthesised.Targets.Select(t => t.Name).ToArray();

                    using (Assert.EnterMultipleScope())
                    {
                        Assert.That(targetNames, Does.Contain("file"));
                        Assert.That(targetNames, Does.Not.Contain("seq"));
                    }
                }
            }
            finally
            {
                // NLog closes the outgoing configuration when the next one is installed, and closing a
                // target graph that still contains the cycle overflows the stack and kills the test host.
                cyclic.Targets.Remove(cyclic);
                LogManager.Configuration = new LoggingConfiguration();
            }
        }

        [Test]
        public void Init_LogRules_reach_the_non_seq_members_of_a_group_target()
        {
            MemoryTarget seq = new() { Name = "seq", Layout = "${level}|${message}" };
            MemoryTarget file = new() { Name = "file", Layout = "${level}|${message}" };
            SplitGroupTarget all = new() { Name = "all" };
            all.Targets.Add(seq);
            all.Targets.Add(file);
            LogManager.Configuration = ConfigurationWith(new LoggingRule("*", Level.Info, Level.Fatal, all) { Final = true });

            using (new NLogManager("test", null, "Synchronization.*:Trace"))
            {
                Logger logger = LogManager.GetLogger("Synchronization.Foo");
                logger.Trace("raised");
                logger.Info("baseline");
                LogManager.Flush();

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(file.Logs, Is.EqualTo(new[] { "Trace|raised", "Info|baseline" }));
                    Assert.That(seq.Logs, Is.EqualTo(new[] { "Info|baseline" }));
                }
            }
        }

        [Test]
        public void Non_final_group_rule_writes_shared_levels_twice()
        {
            MemoryTarget seq = new() { Name = "seq", Layout = "${level}|${message}" };
            MemoryTarget file = new() { Name = "file", Layout = "${level}|${message}" };
            SplitGroupTarget all = new() { Name = "all" };
            all.Targets.Add(seq);
            all.Targets.Add(file);
            LogManager.Configuration = ConfigurationWith(new LoggingRule("*", Level.Info, Level.Fatal, all));

            using (new NLogManager("test", null, "Synchronization.*:Trace"))
            {
                Logger logger = LogManager.GetLogger("Synchronization.Foo");
                logger.Trace("raised");
                logger.Info("baseline");
                LogManager.Flush();

                // Without final="true" on the group rule (contrast Init_LogRules_reach_the_non_seq_members_of_a_group_target
                // above), its own Info-and-above delivery and the synthesised rule's both reach `file`, so the
                // Info level they share is written twice; `seq` still gets it once, since the synthesised rule
                // excludes seq entirely.
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(file.Logs, Is.EqualTo(new[] { "Trace|raised", "Info|baseline", "Info|baseline" }));
                    Assert.That(seq.Logs, Is.EqualTo(new[] { "Info|baseline" }));
                }
            }
        }

        [Test]
        public void Group_target_of_the_shipped_config_contributes_no_target_of_its_own()
        {
            LoggingConfiguration configuration = ShippedConfiguration();
            Target all = configuration.FindTargetByName("all");
            configuration.LoggingRules.Add(new LoggingRule("Network.*", Level.Trace, Level.Fatal, all));
            LogManager.Configuration = configuration;

            using (new NLogManager("test", null, "Synchronization.*:Trace"))
            {
                LoggingRule synthesised = LogManager.Configuration.LoggingRules.Single(r => r.LoggerNamePattern == "Synchronization.*");

                // The group's members are the very targets the shipped catch-all rules already name, so
                // descending into it must neither add a target nor repeat one.
                Assert.That(synthesised.Targets.Select(t => t.Name), Is.EquivalentTo(new[] { "file-async", "auto-colored-console-async" }));
            }
        }
    }
}
