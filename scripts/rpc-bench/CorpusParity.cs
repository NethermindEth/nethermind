#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
#:include CorpusParity.Helpers.cs

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
using var parity = new CorpusParity(Environment.GetEnvironmentVariable);
return await parity.Main(args, cancellation.Token);
