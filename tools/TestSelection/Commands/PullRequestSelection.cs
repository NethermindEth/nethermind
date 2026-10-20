// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.TestSelection.Selection;

namespace Nethermind.TestSelection.Commands;

/// <summary>What a pull request runs, or why it runs everything.</summary>
public sealed record PullRequestDecision(RunSelection? Selection, string Reason);

/// <summary>Decides for one pull request run from the published model and the files it changed.</summary>
public static class PullRequestSelection
{
    public static PullRequestDecision Decide(string root, string modelPath, string changedFilesPath, string run, DateTimeOffset now)
    {
        if (SelectionModel.TrustProblem(modelPath, now) is { } problem)
        {
            return new PullRequestDecision(null, problem);
        }

        Projects projects = Projects.OfRepository(root);
        Change change = projects.Changed(File.ReadAllText(changedFilesPath).Split((char[])['\n', '\r', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries));
        Selector selector = new(SelectionModel.Read(modelPath).Links);
        return new PullRequestDecision(
            selector.Decide(run, now, change, projects.DirectTests(change.Projects)),
            "the change touches the build or the test harness, or the run is a holdout");
    }
}
