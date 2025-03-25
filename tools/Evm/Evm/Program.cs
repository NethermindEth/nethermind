// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Evm.T8n;
using System.CommandLine;

CliRootCommand rootCmd = [];

T8nCommand.Configure(rootCmd);

CliConfiguration cli = new(rootCmd);

return cli.Invoke(args);
