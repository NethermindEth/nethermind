// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Evm.T8n;
using System.CommandLine;

RootCommand rootCmd = [];

T8nCommand.Configure(ref rootCmd);

return rootCmd.Parse(args).Invoke();
