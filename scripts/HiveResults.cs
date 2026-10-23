#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
#:include HiveResults.Helpers.cs

return HiveResults.Run(args, Environment.CurrentDirectory, Console.Out, Console.Error);
