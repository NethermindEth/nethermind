#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
#:property SaveDiskSpace=true
#:property TreatWarningsAsErrors=true
#:property Nullable=enable
#:property PublishAot=false
#:include SmokeChecks.cs

if (args is ["--help"] or ["-h"])
{
    Console.WriteLine("Check a running deployment: smoke.cs [--rpc-url URL] [--grafana-url URL] [--prometheus-url URL] [--network mainnet|sepolia|hoodi] [--wait-seconds N]");
    return 0;
}
try
{
    SmokeOptions options = SmokeOptions.Parse(args);
    using HttpClient client = new();
    SmokeChecks checks = new(client, TimeProvider.System, Task.Delay, Console.Out);
    await checks.RunAsync(options, Environment.GetEnvironmentVariable("GRAFANA_ADMIN_PASSWORD") ?? "",
        Path.Combine(SmokeChecks.DeploymentDirectory(), "observability/grafana/dashboards/light-client.json"));
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}
