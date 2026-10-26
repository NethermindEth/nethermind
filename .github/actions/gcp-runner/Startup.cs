#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#:include Gcp.Helpers.cs
#:include StartupProvision.Helpers.cs
using System.Runtime.InteropServices;

using var cancellation = new CancellationTokenSource();
using var signal = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; cancellation.Cancel(); });
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
return await StartupProvision.Run("/", Gcp.Run, args, cancellation.Token);
