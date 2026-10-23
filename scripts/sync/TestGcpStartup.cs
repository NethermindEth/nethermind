#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#:include SyncTests.Helpers.cs
#:include ../../.github/actions/gcp-runner/Gcp.Helpers.cs
#:include ../../.github/actions/gcp-runner/StartupProvision.Helpers.cs

foreach (var scenario in new[] { "zero", "single", "raid", "mismatch", "docker-root", "apt-failure", "runner-failure", "metadata-failure" })
    await Cases.Test($"startup {scenario}", async () =>
    {
        var temporary = Directory.CreateTempSubdirectory("startup fixtures ");
        var originalDirectory = Environment.CurrentDirectory;
        var frontend = Environment.GetEnvironmentVariable("DEBIAN_FRONTEND");
        try
        {
            string At(string path) => Path.GetFullPath(Path.Combine(temporary.FullName, path));
            foreach (var path in new[] { "dev/disk/by-id", "etc/docker", "etc/apt/sources.list.d", "etc/systemd/system" }) Directory.CreateDirectory(At(path));
            File.WriteAllText(At("etc/os-release"), "VERSION_CODENAME=noble\n");
            var count = scenario == "zero" ? 0 : scenario == "raid" ? 2 : 1;
            for (var i = 0; i < count; i++) File.WriteAllText(At($"dev/disk/by-id/google-local-nvme-ssd-{i}"), "");
            File.WriteAllText(At("dev/disk/by-id/google-local-nvme-ssd-0-part1"), "");
            File.WriteAllText(At("dev/disk/by-id/google-persistent-disk-0"), "");
            var stages = new List<string>();
            var commands = new List<string[]>();
            Task<CommandResult> Execute(string executable, string[] arguments, string? input, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                commands.Add([executable, .. arguments]);
                if (executable == "curl")
                {
                    var url = arguments.Last();
                    if (url.EndsWith("/stage", StringComparison.Ordinal)) { stages.Add(arguments[Array.IndexOf(arguments, "--data") + 1]); return Task.FromResult(new CommandResult(0)); }
                    if (url.Contains("/attributes/", StringComparison.Ordinal))
                    {
                        var key = url[(url.LastIndexOf('/') + 1)..];
                        if (key == "runner-version" && scenario == "metadata-failure") return Task.FromResult(new CommandResult(1));
                        return Task.FromResult(new CommandResult(0, key switch { "runner-version" => "2.336.0", "data-mount" => "/data", "local-ssd-count" => (scenario == "mismatch" ? count + 1 : count).ToString(), "runner-jit-config" => "fake-jit", _ => "" }));
                    }
                }
                if (executable == "apt-get")
                {
                    Cases.True(arguments.Contains("DPkg::Lock::Timeout=900"), "apt lock timeout");
                    if (scenario == "apt-failure") return Task.FromResult(new CommandResult(1));
                }
                if (executable == "docker") return Task.FromResult(new CommandResult(0, scenario == "docker-root" ? "/var/lib/docker" : At("data/docker")));
                if (executable == "dpkg") return Task.FromResult(new CommandResult(0, "amd64"));
                if (executable == "./bin/installdependencies.sh" && scenario == "runner-failure") return Task.FromResult(new CommandResult(1));
                return Task.FromResult(new CommandResult(0));
            }
            var result = await StartupProvision.Run(temporary.FullName, Execute, [], CancellationToken.None);
            var success = scenario is "zero" or "single" or "raid";
            Cases.Equal(success ? 0 : 1, result);
            Cases.Equal(success ? "runner-started" : "failed", stages[^1]);
            Cases.True(commands.All(command => command[0] != "mkfs.ext4" || !command.Any(arg => arg.Contains("persistent", StringComparison.Ordinal) || arg.Contains("-part", StringComparison.Ordinal))), "never format boot disk or partition");
            if (scenario == "mismatch") Cases.True(commands.All(command => command[0] != "mkfs.ext4"), "reject disk mismatch before format");
            if (success)
            {
                Cases.Equal(count == 0 ? 0 : 1, commands.Count(command => command[0] == "mkfs.ext4"));
                Cases.Equal(count > 1, commands.Any(command => command[0] == "mdadm"));
                Cases.Equal("fake-jit" + Environment.NewLine, File.ReadAllText(At("etc/gh-runner.jitconfig")));
                if (!OperatingSystem.IsWindows()) Cases.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(At("etc/gh-runner.jitconfig")));
                var service = File.ReadAllText(At("etc/systemd/system/gh-runner.service"));
                Cases.True(service.Contains("app/Startup.dll --run-runner", StringComparison.Ordinal), "compiled C# service entrypoint");
                Cases.True(!service.Contains("dotnet run", StringComparison.Ordinal), "service does not restore or compile");
                Cases.True(service.Contains("KillMode=control-group", StringComparison.Ordinal), "service stops runner descendants");
                Cases.True(!service.Contains("fake-jit", StringComparison.Ordinal), "credential not embedded in unit");
            }
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
            Environment.SetEnvironmentVariable("DEBIAN_FRONTEND", frontend);
            temporary.Delete(recursive: true);
        }
    });
Cases.Test("bootstrap pins SDK digest and transports included source", () =>
{
    var action = Path.Combine(Cases.Root(), ".github/actions/gcp-runner");
    var bootstrap = File.ReadAllText(Path.Combine(action, "startup-script.sh"));
    Cases.True(bootstrap.Contains("dotnet-sdk-10.0.402-linux-x64.tar.gz", StringComparison.Ordinal), "pinned SDK");
    Cases.True(bootstrap.IndexOf("sha512sum -c -", StringComparison.Ordinal) < bootstrap.IndexOf("tar xzf", StringComparison.Ordinal), "verify before extraction");
    foreach (var file in new[] { "Startup.cs", "Gcp.Helpers.cs", "StartupProvision.Helpers.cs" }) Cases.True(bootstrap.Contains("-o " + file, StringComparison.Ordinal), "source delivery " + file);
    Cases.True(bootstrap.Contains("set -euo pipefail", StringComparison.Ordinal), "bootstrap fail-fast");
});
if (OperatingSystem.IsLinux())
{
    var actionDirectory = Path.Combine(Cases.Root(), ".github/actions/gcp-runner");
    foreach (var scenario in new[] { "success", "publish-failure", "runtime-failure" })
        await Cases.Test($"bootstrap without HOME {scenario}", async () =>
        {
            var temporary = Directory.CreateTempSubdirectory("bootstrap fixtures ");
            try
            {
                var bin = Directory.CreateDirectory(Path.Combine(temporary.FullName, "bin"));
                var bootstrap = Path.Combine(temporary.FullName, "bootstrap");
                var calls = Path.Combine(temporary.FullName, "calls");
                void Tool(string name, string body)
                {
                    var path = Path.Combine(bin.FullName, name);
                    File.WriteAllText(path, "#!/bin/bash\nset -eu\n" + body + "\n");
                    if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
                Tool("curl", "printf '%s\\n' \"$@\" >> \"$FIXTURE_CALLS\"; while (($#)); do if [[ $1 == -o ]]; then shift; touch \"$1\"; fi; shift; done");
                Tool("sha512sum", "cat >/dev/null");
                Tool("tar", "cp \"$FIXTURE_BIN/dotnet-stub\" dotnet/dotnet");
                Tool("dotnet-stub", """
                    test -d "$HOME"
                    test "$DOTNET_CLI_HOME" = "$BOOTSTRAP_DIR"
                    printf 'dotnet %s\n' "$*" >> "$FIXTURE_CALLS"
                    if [[ $1 == publish ]]; then
                      [[ $FIXTURE_SCENARIO != publish-failure ]] || exit 43
                    else
                      [[ $1 == app/Startup.dll ]]
                      [[ $FIXTURE_SCENARIO != runtime-failure ]] || exit 44
                    fi
                    """);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var result = await Gcp.Run("env", ["-u", "HOME", "-u", "DOTNET_CLI_HOME",
                    "PATH=" + bin.FullName + ":" + Environment.GetEnvironmentVariable("PATH"), "BOOTSTRAP_DIR=" + bootstrap,
                    "FIXTURE_BIN=" + bin.FullName, "FIXTURE_CALLS=" + calls, "FIXTURE_SCENARIO=" + scenario,
                    "bash", Path.Combine(actionDirectory, "startup-script.sh")], null, timeout.Token);
                Cases.True(result.Code == (scenario == "success" ? 0 : scenario == "publish-failure" ? 43 : 44), result.Output + result.Error);
                var transcript = File.ReadAllText(calls);
                Cases.Equal(scenario != "success", transcript.Contains("/guest-attributes/gh-runner/error", StringComparison.Ordinal));
                Cases.Equal(scenario != "success", transcript.Contains("/guest-attributes/gh-runner/stage", StringComparison.Ordinal));
                if (scenario != "success")
                {
                    Cases.True(transcript.Contains("bootstrap line", StringComparison.Ordinal) && transcript.Contains("failed", StringComparison.Ordinal), "bootstrap failure diagnosis");
                    Cases.True(!transcript.Contains("set -eu", StringComparison.Ordinal), "failure does not expose source or credentials");
                }
            }
            finally { temporary.Delete(recursive: true); }
        });
    await Cases.Test("publish startup offline with explicit CLI home", async () =>
    {
        var temporary = Directory.CreateTempSubdirectory("startup publish ");
        var originalDirectory = Environment.CurrentDirectory;
        var home = Environment.GetEnvironmentVariable("HOME");
        var cliHome = Environment.GetEnvironmentVariable("DOTNET_CLI_HOME");
        try
        {
            foreach (var file in new[] { "Startup.cs", "Gcp.Helpers.cs", "StartupProvision.Helpers.cs" }) File.Copy(Path.Combine(actionDirectory, file), Path.Combine(temporary.FullName, file));
            Environment.CurrentDirectory = temporary.FullName;
            Environment.SetEnvironmentVariable("HOME", null);
            Environment.SetEnvironmentVariable("DOTNET_CLI_HOME", temporary.FullName);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var result = await Gcp.Run("dotnet", ["publish", "Startup.cs", "--no-self-contained", "-p:PublishAot=false", "-p:NuGetAudit=false", "-p:RestoreSources=" + temporary.FullName, "-o", "app"], null, timeout.Token);
            Cases.True(result.Code == 0, result.Output + result.Error);
            Cases.True(File.Exists(Path.Combine(temporary.FullName, "app/Startup.dll")), "published service DLL location");
            Cases.True(File.Exists(Path.Combine(temporary.FullName, "app/Startup.runtimeconfig.json")), "published runtime configuration");
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
            Environment.SetEnvironmentVariable("HOME", home);
            Environment.SetEnvironmentVariable("DOTNET_CLI_HOME", cliHome);
            temporary.Delete(recursive: true);
        }
    });
}
Cases.Finish();
