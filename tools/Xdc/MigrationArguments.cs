// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.CommandLine;

namespace Xdc;

public class MigrationArguments
{
    public string SourceDir { get; private init; } = "";
    public string TargetDir { get; private init; } = "";
    public bool Verify { get; private init; }

    public static MigrationArguments FromParseResult(ParseResult parseResult) => new()
    {
        SourceDir = parseResult.GetRequiredValue(MigrationOptions.SourceDir),
        TargetDir = parseResult.GetRequiredValue(MigrationOptions.TargetDir),
        Verify = parseResult.GetRequiredValue(MigrationOptions.Verify)
    };
}
