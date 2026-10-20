// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.TestSelection.Journal;

/// <summary>
/// One finished run of a test workflow: what changed and what failed. <see cref="Selected"/> marks a run that used
/// the selection, which can only fail in the tests it ran.
/// </summary>
public sealed record JournalEntry(
    long Run,
    DateTimeOffset At,
    string Event,
    string Branch,
    IReadOnlyList<string> Files,
    Failures Failed,
    bool Selected = false)
{
    public const string PullRequest = "pull_request";
    public const string Push = "push";
    public const string Master = "master";

    public bool IsPullRequest => Event == PullRequest;

    /// <summary>A push to master. A fork can name its branch master, but its runs are pull requests.</summary>
    public bool IsMasterPush => Event == Push && Branch == Master;

    public IReadOnlySet<EestTest> EestFailures() => Failed.EestTests();

    public IReadOnlySet<string> FailedProjects() => Failed.Projects();
}
