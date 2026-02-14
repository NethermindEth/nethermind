// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.CommandLine;

namespace Xdc;

public static class MigrationOptions
{
    public static Option<string> SourceDir { get; } = new("--source")
    {
        Required = true,
        Aliases = { "s", "in" },
        Description = "Source database directory"
    };

    public static Option<string> TargetDir { get; } = new("--target")
    {
        Required = true,
        Aliases = { "t", "out" },
        Description = "Target database directory"
    };

    public static Option<bool> Verify { get; } = new("--verify")
    {
        Aliases = { "v" },
        Description = "Whether to verify resulting database"
    };

    public static Command CreateCommand() => new("migrate", "XDC state migration")
    {
        SourceDir,
        TargetDir,
        Verify
    };
}
