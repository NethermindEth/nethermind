#!/usr/bin/env dotnet
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
                Cases.True(service.Contains("-- --run-runner", StringComparison.Ordinal), "C# service entrypoint");
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
Cases.Finish();
