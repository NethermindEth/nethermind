// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Evm.T8n;

using System.CommandLine;

public static class Program
{
    public static int Main(string[] args)
    {
        RootCommand rootCmd = [];

        T8nCommand.Configure(ref rootCmd);

        CommandLineConfiguration cli = new(rootCmd);

        return cli.Invoke(args);
    }
}
