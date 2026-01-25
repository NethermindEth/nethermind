// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.CommandLine;
using Xdc;

RootCommand rootCmd = [];

MigrationCommand.Configure(ref rootCmd);
return rootCmd.Parse(args).Invoke();
