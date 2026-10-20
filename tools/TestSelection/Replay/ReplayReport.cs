// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using System.Text;
using Nethermind.TestSelection.Journal;

namespace Nethermind.TestSelection.Replay;

public sealed class ReplayReport
{
    public int Measured { get; set; }
    public SuiteReplay Eest { get; } = new();
    public SuiteReplay NUnit { get; } = new();

    public string ToMarkdown(string repository, IReadOnlyList<JournalEntry> journal)
    {
        StringBuilder markdown = new();
        markdown.Append("## Test selection replay\n\n");
        markdown.Append(CultureInfo.InvariantCulture,
            $"{journal.Count} runs from {journal[0].At:yyyy-MM-dd} to {journal[^1].At:yyyy-MM-dd}, {Measured} pull request runs measured. ");
        markdown.Append(CultureInfo.InvariantCulture,
            $"The first {SelectionReplay.Warmup.Days} days only build history. Runs that used the selection are not measured: they could only fail in the tests they ran.\n\n");
        markdown.Append("| Tests | Cost the selector would run | Runs with failures | Runs it would report green |\n|---|---|---|---|\n");
        markdown.Append(Row("EEST", Eest)).Append(Row("NUnit test projects", NUnit));

        List<HiddenRun> hidden = [.. Eest.Hidden, .. NUnit.Hidden];
        if (hidden.Count > 0)
        {
            markdown.Append("\n| Run | Branch | Failed tests |\n|---|---|---|\n");
            foreach (HiddenRun run in hidden)
            {
                markdown.Append(HiddenRow(repository, run));
            }
        }

        return markdown.ToString();
    }

    /// <summary>Branch names and test names come from pull requests; keep them from breaking or injecting markup.</summary>
    public static string MarkdownCell(string text) => text.Replace("|", "\\|").Replace("`", "'").Replace("<", "&lt;");

    private string Row(string label, SuiteReplay suite)
    {
        double share = Measured > 0 ? 100 * suite.SelectedCost / Measured : 0;
        int branches = suite.Failing.Select(static entry => entry.Branch).Distinct().Count();
        return string.Create(CultureInfo.InvariantCulture, $"| {label} | {share:F1}% | {suite.Failing.Count} ({branches} branches) | {suite.Hidden.Count} |\n");
    }

    private static string HiddenRow(string repository, HiddenRun run)
    {
        string link = $"[{run.Entry.Run}](https://github.com/{repository}/actions/runs/{run.Entry.Run})";
        string tests = string.Join(", ", run.Failures.Take(3)) + (run.Failures.Count > 3 ? " ..." : string.Empty);
        return $"| {link} | {MarkdownCell(run.Entry.Branch)} | {MarkdownCell(tests)} |\n";
    }
}
