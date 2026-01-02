// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NethCat;
using System.CommandLine;

RootCommand rootCommand = new("NethCat - Nethermind network utilities");

Discv5Command.Setup(rootCommand);

return await rootCommand.Parse(args).InvokeAsync();
