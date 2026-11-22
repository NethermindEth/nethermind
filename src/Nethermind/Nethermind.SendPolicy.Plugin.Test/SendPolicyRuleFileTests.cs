// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Abstractions;
using System.Reflection;
using System.Text;
using Autofac;
using Nethermind.Core;
using Nethermind.Core.Test.IO;
using Nethermind.Logging;
using NUnit.Framework;
using Testably.Abstractions;

namespace Nethermind.SendPolicy.Plugin.Test;

[Parallelizable(ParallelScope.All)]
public class SendPolicyRuleFileTests
{
    private static readonly IFileSystem FileSystem = new RealFileSystem();
    private static readonly Address Sender = new("0x1111111111111111111111111111111111111111");
    private static readonly Address PreviousDestination = new("0x2222222222222222222222222222222222222222");
    private static readonly Address NewDestination = new("0x3333333333333333333333333333333333333333");
    private static readonly TimeSpan ThreadTimeout = TimeSpan.FromSeconds(10);

    [Test]
    public void Same_length_and_timestamp_replacement_reloads_rules([Values] bool malformed, [Values] bool atomic)
    {
        using TempPath directory = WriteRules();
        string path = RulesPath(directory);
        long size = new FileInfo(path).Length;
        DateTime timestamp = File.GetLastWriteTimeUtc(path);
        using IContainer container = Open(path);
        SendPolicyRuleFile file = container.Resolve<SendPolicyRuleFile>();
        Assert.That(file.Current.Destinations.ContainsKey(PreviousDestination), Is.True);

        ReplaceRules(path, [$"from {Sender}", malformed ? $"zz {PreviousDestination} 1" : $"to {NewDestination} 1"], timestamp, atomic);

        SendPolicyRules rules = file.Current;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(new FileInfo(path).Length, Is.EqualTo(size), "the cached length must not change");
            Assert.That(File.GetLastWriteTimeUtc(path), Is.EqualTo(timestamp), "the cached timestamp must not change");
            Assert.That(rules.GuardedSenders, Does.Contain(Sender));
            Assert.That(rules.Destinations.ContainsKey(PreviousDestination), Is.False);
            if (malformed)
            {
                Assert.That(rules.Error, Does.Contain("unknown rule 'zz'"));
                Assert.That(rules.Destinations, Is.Empty);
            }
            else
            {
                Assert.That(rules.Error, Is.Null);
                Assert.That(rules.Destinations.ContainsKey(NewDestination), Is.True);
            }
        }
    }

    [Test]
    public void Identical_content_preserves_cached_rules()
    {
        using TempPath directory = WriteRules();
        string path = RulesPath(directory);
        DateTime timestamp = File.GetLastWriteTimeUtc(path);
        using IContainer container = Open(path);
        SendPolicyRuleFile file = container.Resolve<SendPolicyRuleFile>();
        SendPolicyRules rules = file.Current;

        ReplaceRules(path, [$"from {Sender}", $"to {PreviousDestination} 1"], timestamp.AddSeconds(1));

        Assert.That(file.Current, Is.SameAs(rules));
    }

    [Test]
    public void Content_comparison_handles_chunk_boundaries(
        [Values(0, 1023, 1024, 1025, 4096)] int prefixLength,
        [Values("", "# appended\n", "from 0x3333333333333333333333333333333333333333\n")] string suffix,
        [Values] bool utf16)
    {
        using TempPath directory = WriteRules();
        string path = RulesPath(directory);
        string prefix = "#" + new string('\u00e9', prefixLength) + "\n";
        string original = prefix + $"from {Sender}\nto {PreviousDestination} 1\n";
        Encoding encoding = utf16 ? Encoding.Unicode : Encoding.UTF8;
        File.WriteAllText(path, original, encoding);
        using IContainer container = Open(path);
        SendPolicyRuleFile file = container.Resolve<SendPolicyRuleFile>();
        SendPolicyRules initial = file.Current;
        Assert.That(file.Current, Is.SameAs(initial));

        File.WriteAllText(path, prefix + suffix, encoding);
        SendPolicyRules changed = file.Current;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(changed.Error, Is.Null);
            Assert.That(changed.GuardedSenders.Contains(Sender), Is.False);
            Assert.That(changed.GuardedSenders.Contains(NewDestination), Is.EqualTo(suffix.StartsWith("from")));
            Assert.That(changed.Destinations, Is.Empty);
            Assert.That(file.Current, Is.SameAs(changed));
        }

        File.WriteAllText(path, original, encoding);
        SendPolicyRules restored = file.Current;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(restored.Error, Is.Null);
            Assert.That(restored.GuardedSenders, Does.Contain(Sender));
            Assert.That(restored.Destinations.ContainsKey(PreviousDestination), Is.True);
        }
    }

    [Test]
    public void Missing_rule_file_preserves_guarded_senders_and_recovers()
    {
        using TempPath directory = WriteRules();
        string path = RulesPath(directory);
        using IContainer container = Open(path);
        SendPolicyRuleFile file = container.Resolve<SendPolicyRuleFile>();

        File.Delete(path);
        SendPolicyRules unusable = file.Current;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(unusable.Error, Is.Not.Null);
            Assert.That(unusable.GuardedSenders, Does.Contain(Sender));
            Assert.That(unusable.Destinations, Is.Empty);
        }

        File.WriteAllLines(path, [$"from {Sender}", $"to {NewDestination} 1"]);
        SendPolicyRules recovered = file.Current;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(recovered.Error, Is.Null);
            Assert.That(recovered.Destinations.ContainsKey(NewDestination), Is.True);
        }
    }

    [Test]
    public void Delayed_reload_preserves_latest_guarded_senders_when_file_becomes_unusable()
    {
        using TempPath directory = WriteRules();
        string path = RulesPath(directory);
        DateTime timestamp = File.GetLastWriteTimeUtc(path);
        using IContainer container = Open(path);
        SendPolicyRuleFile file = container.Resolve<SendPolicyRuleFile>();
        ReplaceRules(path, [$"from {Sender}", $"to {PreviousDestination} 2"], timestamp.AddSeconds(1));
        Lock reloadLock = (Lock)typeof(SendPolicyRuleFile).GetField("_reloadLock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(file)!;
        SendPolicyRules? delayedRules = null;
        Exception? readerError = null;
        Thread reader = new(() =>
        {
            try { delayedRules = file.Current; }
            catch (Exception exception) { readerError = exception; }
        })
        { IsBackground = true };

        try
        {
            // Park a reader at the reload lock, then publish the newer policy on the owning thread.
            lock (reloadLock)
            {
                reader.Start();
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(SpinWait.SpinUntil(() => !reader.IsAlive ||
                        (reader.ThreadState & ThreadState.WaitSleepJoin) != 0, ThreadTimeout), Is.True);
                    Assert.That(reader.IsAlive, Is.True, "the delayed reader must wait at the reload lock");
                }

                ReplaceRules(path, [$"from {NewDestination}", $"to {PreviousDestination} 3"], timestamp.AddSeconds(2));
                SendPolicyRules latestRules = file.Current;
                Assert.That(latestRules.GuardedSenders, Does.Contain(NewDestination));
            }
        }
        finally
        {
            Assert.That(reader.Join(ThreadTimeout), Is.True, "the delayed reader must finish after the lock is released");
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(readerError, Is.Null);
            Assert.That(delayedRules, Is.Not.Null);
        }

        ReplaceRules(path, [$"from {NewDestination}", $"zz {PreviousDestination} 3"], timestamp.AddSeconds(3));
        SendPolicyRules unusable = file.Current;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(delayedRules!.GuardedSenders, Does.Contain(NewDestination));
            Assert.That(delayedRules.GuardedSenders, Does.Not.Contain(Sender));
            Assert.That(unusable.Error, Is.Not.Null);
            Assert.That(unusable.GuardedSenders, Does.Contain(NewDestination));
            Assert.That(unusable.GuardedSenders, Does.Not.Contain(Sender));
            Assert.That(unusable.Destinations, Is.Empty);
        }
    }

    [TestCase(FileAccess.Read, FileOptions.DeleteOnClose, TestName = "Rule_file_open_for_deletion_remains_readable")]
    [TestCase(FileAccess.Write, FileOptions.None, TestName = "Rule_file_open_for_writing_remains_readable")]
    [Platform("Win")]
    public void Rule_file_held_open_by_another_handle_remains_readable(FileAccess access, FileOptions options)
    {
        using TempPath directory = WriteRules();
        string path = RulesPath(directory);
        using IContainer container = Open(path);
        SendPolicyRuleFile file = container.Resolve<SendPolicyRuleFile>();
        SendPolicyRules rules = file.Current;

        using (new FileStream(path, FileMode.Open, access, FileShare.Read | FileShare.Delete, 4096, options))
        {
            Assert.That(file.Current, Is.SameAs(rules));
        }
    }

    [Test]
    public void Exclusive_writer_refuses_guarded_senders_and_recovers()
    {
        using TempPath directory = WriteRules();
        string path = RulesPath(directory);
        using IContainer container = Open(path);
        SendPolicyRuleFile file = container.Resolve<SendPolicyRuleFile>();

        using (new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            SendPolicyRules unusable = file.Current;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(unusable.Error, Is.Not.Null);
                Assert.That(unusable.GuardedSenders, Does.Contain(Sender));
                Assert.That(unusable.Destinations, Is.Empty);
            }
        }

        Assert.That(file.Current.Error, Is.Null);
    }

    private static TempPath WriteRules()
    {
        TempPath directory = TempPath.GetTempDirectory();
        Directory.CreateDirectory(directory.Path);
        File.WriteAllLines(RulesPath(directory), [$"from {Sender}", $"to {PreviousDestination} 1"]);
        return directory;
    }

    private static string RulesPath(TempPath directory) => Path.Combine(directory.Path, "rules");

    private static IContainer Open(string path) => new ContainerBuilder()
        .AddModule(new SendPolicyModule())
        .AddSingleton<ISendPolicyConfig>(new SendPolicyConfig { Enabled = true, RulesPath = path })
        .AddSingleton<IFileSystem>(FileSystem)
        .AddSingleton<ILogManager>(LimboLogs.Instance)
        .Build();

    private static void ReplaceRules(string path, string[] lines, DateTime timestamp, bool atomic = false)
    {
        string replacement = atomic ? path + ".replacement" : path;
        File.WriteAllLines(replacement, lines);
        File.SetLastWriteTimeUtc(replacement, timestamp);
        if (atomic) File.Move(replacement, path, true);
    }
}
