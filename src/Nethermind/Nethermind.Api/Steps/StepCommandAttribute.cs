// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Api.Steps;

/// <summary>Exposes a step as a standalone command that can be run instead of the node.</summary>
/// <remarks>
/// A step carrying this attribute never runs as part of a normal node start. When selected — by name from the
/// command line, or by a module calling <see cref="ContainerBuilderExtensions.SelectStepTarget"/> — only it and
/// its transitive dependencies execute, and the process exits once it completes. The step therefore must not
/// shut the application down itself; it only calls <see cref="Nethermind.Config.IProcessExitSource.Exit"/> to
/// report a non-zero exit code, or throws.
/// </remarks>
[AttributeUsage(AttributeTargets.Class)]
public sealed class StepCommandAttribute(string name, string description) : Attribute
{
    /// <summary>The command name, as typed on the command line (e.g. <c>verify-trie</c>).</summary>
    public string Name { get; } = name;

    /// <summary>One line describing the command, shown when an unknown command is requested.</summary>
    public string Description { get; } = description;
}
