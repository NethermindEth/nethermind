#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
#:include FetchArtifacts.Helpers.cs
#:include VerifierArtifacts.Helpers.cs

using System.Runtime.InteropServices;

using var cancellation = new CancellationTokenSource();
using var termination = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; cancellation.Cancel(); });
Console.CancelKeyPress += (_, context) => { context.Cancel = true; cancellation.Cancel(); };
using var handler = new HttpClientHandler { AllowAutoRedirect = false };
using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
return await ArtifactFetch.Run(http, Environment.GetEnvironmentVariable("REQUESTED_VERSION") ?? "", Environment.GetEnvironmentVariable("RUNNER_TEMP") ?? "", Environment.GetEnvironmentVariable("GITHUB_ENV") ?? "", Environment.GetEnvironmentVariable("GH_TOKEN") ?? "", cancellation.Token);
