#!/usr/bin/env dotnet
#:include Gcp.Helpers.cs
#:include Lifecycle.Helpers.cs
using System.Runtime.InteropServices;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
using var signal = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; cancellation.Cancel(); });
return await new Lifecycle(Gcp.Run, Environment.GetEnvironmentVariable, Task.Delay).Create(Environment.GetEnvironmentVariable("GITHUB_ACTION_PATH") ?? Path.GetFullPath(".github/actions/gcp-runner"), cancellation.Token);
