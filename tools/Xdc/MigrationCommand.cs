// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.CommandLine;

namespace Xdc;

public static class MigrationCommand
{
    public static void Configure(ref RootCommand rootCmd)
    {
        Command cmd = MigrationOptions.CreateCommand();

        cmd.SetAction(parseResult =>
        {
            var arguments = MigrationArguments.FromParseResult(parseResult);

            try
            {
                MigrationResult result = Migrator.Migrate(arguments);

                Console.WriteLine(result);
                Console.WriteLine("Migration completed successfully.");
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
            }
        });

        rootCmd.Add(cmd);
    }
}
