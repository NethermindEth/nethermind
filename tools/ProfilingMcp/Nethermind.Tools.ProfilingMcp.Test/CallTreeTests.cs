// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Tools.ProfilingMcp.Analysis;
using NUnit.Framework;

namespace Nethermind.Tools.ProfilingMcp.Test;

public class CallTreeTests
{
    private static readonly Func<string, bool> IsWait = static name => name == "Wait";

    [Test]
    public void Builds_nodes_per_call_path_and_attributes_waits_to_ancestors()
    {
        CallTreeNode root = CallTree.Build(
        [
            Function("Main", Stack(100, 0, "Main")),
            Function("Work", Stack(70, 30, "Main", "Work")),
            Function("Wait", Stack(40, 40, "Main", "Work", "Wait"), Stack(20, 20, "Main", "Idle", "Wait")),
        ], IsWait);

        CallTreeNode main = root.Children.Single();
        CallTreeNode work = main.Children.Single(static c => c.Name == "Work");
        CallTreeNode idle = main.Children.Single(static c => c.Name == "Idle");

        using (Assert.EnterMultipleScope())
        {
            Assert.That((root.TotalTime, root.WaitTime), Is.EqualTo((100d, 60d)));
            Assert.That((main.TotalTime, main.WaitTime), Is.EqualTo((100d, 60d)));
            Assert.That((work.TotalTime, work.OwnTime, work.WaitTime), Is.EqualTo((70d, 30d, 40d)));
            Assert.That((idle.TotalTime, idle.WaitTime), Is.EqualTo((20d, 20d)), "an unlisted path prefix takes its children's time");
        }
    }

    [Test]
    public void Prune_keeps_ancestors_and_counts_time_outside_waits()
    {
        CallTreeNode root = CallTree.Build(
        [
            Function("Main", Stack(10_000, 0, "Main")),
            Function("Wait", Stack(9_900, 9_900, "Main", "Wait")),
            Function("Hot", Stack(99, 99, "Main", "Hot")),
            Function("Tiny", Stack(0.001, 0.001, "Main", "Tiny")),
        ], IsWait);

        int kept = CallTree.Prune(root, maxNodes: 4);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(kept, Is.EqualTo(4));
            Assert.That(root.Children.Single().Children.Select(static c => c.Name), Is.EquivalentTo(new[] { "Wait", "Hot" }));
        }
    }

    [Test]
    public void Report_embeds_method_names_without_closing_its_script()
    {
        FunctionStat[] functions = [Function("Evil</script><b>", Stack(5, 5, "Evil</script><b>"))];
        CallTreeNode tree = CallTree.Build(functions, IsWait);

        string html = HtmlReport.Render("trace.nettrace", functions, tree, CallTree.Count(tree), IsWait);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(html, Does.Not.Contain("Evil</script>"));
            Assert.That(html, Does.Contain("Evil\\u003C/script\\u003E"));
            Assert.That(html, Does.Not.Contain("__DATA__"));
        }
    }

    private static FunctionStat Function(string name, params CallStackStat[] stacks) =>
        new(name, stacks.Sum(static s => s.TotalTime), stacks.Sum(static s => s.OwnTime), 1, stacks);

    private static CallStackStat Stack(double total, double own, params string[] frames) => new(frames, total, own, 1);
}
