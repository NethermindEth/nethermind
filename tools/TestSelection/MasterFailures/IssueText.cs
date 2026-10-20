// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;

namespace Nethermind.TestSelection.MasterFailures;

/// <summary>The title and body of the issue for a broken master workflow.</summary>
public static class IssueText
{
    public const string Label = "master-broken";
    public const int MaxListedTests = 30;

    /// <summary>A hidden marker that ties the open issue to its workflow, so later failures comment on it.</summary>
    public static string Marker(string workflow) => $"<!-- {Label}: {workflow} -->";

    public static string Title(string workflow, IReadOnlyList<string> tests) =>
        $"{workflow} fails on master: {tests[0].Replace('|', ' ')}{(tests.Count > 1 ? $" and {tests.Count - 1} more" : string.Empty)}";

    public static string Body(string workflow, string runUrl, string headSha, string greenSha, IReadOnlyList<string> tests, IReadOnlyList<Suspect> suspects)
    {
        StringBuilder body = new();
        body.Append(Marker(workflow)).Append('\n');
        body.Append($"[{workflow}]({runUrl}) fails on master at {Short(headSha)}. It was green at {Short(greenSha)}.\n\n");
        body.Append("Pull requests run only the tests their change can affect, so this regression was not run before the merge.\n\n");
        body.Append("**Failing tests**\n");
        foreach (string test in tests.Take(MaxListedTests))
        {
            body.Append($"- `{test}`\n");
        }

        if (tests.Count > MaxListedTests)
        {
            body.Append($"- and {tests.Count - MaxListedTests} more\n");
        }

        body.Append("\n**Likely cause**\n");
        if (suspects.Count == 0)
        {
            body.Append("- No merge since the last green run explains the failures.\n");
        }

        foreach (Suspect suspect in suspects)
        {
            body.Append(suspect.Merge.Number is { } number
                ? $"- #{number} by @{suspect.Merge.Author}: {suspect.Reason}\n"
                : $"- {suspect.Merge.Sha}: {suspect.Reason}\n");
        }

        body.Append("\nPlease fix or revert. This issue closes when the workflow is green on master again.\n");
        return body.ToString();
    }

    private static string Short(string sha) => sha[..Math.Min(10, sha.Length)];
}
