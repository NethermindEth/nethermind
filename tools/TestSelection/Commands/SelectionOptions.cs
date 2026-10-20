// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.CommandLine;

namespace Nethermind.TestSelection.Commands;

/// <summary>The options every selection command takes: the model, the change and the run.</summary>
public sealed class SelectionOptions
{
    public Option<string> Model { get; } = new("--model") { Required = true };
    public Option<string> ChangedFiles { get; } = new("--changed-files") { Required = true, Description = "One changed path per line" };
    public Option<string> Run { get; } = new("--run") { Required = true, Description = "The workflow run id; seeds the sample and the holdout" };

    public void AddTo(Command command)
    {
        command.Options.Add(Model);
        command.Options.Add(ChangedFiles);
        command.Options.Add(Run);
    }

    public PullRequestDecision Decide(ParseResult result) =>
        PullRequestSelection.Decide(RepositoryRoot.Find(), result.GetValue(Model)!, result.GetValue(ChangedFiles)!, result.GetValue(Run)!, DateTimeOffset.UtcNow);
}
