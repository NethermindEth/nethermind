// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

static class StartupProvision
{
    internal static async Task<int> Run(string root, Func<string, string[], string?, CancellationToken, Task<CommandResult>> execute, string[] args, CancellationToken token)
    {
        string PathAt(string path) => Path.Combine(root, path.TrimStart('/'));
        var metadata = "http://metadata.google.internal/computeMetadata/v1/instance";
        async Task<CommandResult> Run(string executable, params string[] arguments)
        {
            var result = await execute(executable, arguments, null, token);
            Console.Write(result.Output);
            Console.Error.Write(result.Error);
            if (result.Code != 0) throw new InvalidOperationException($"{executable} exited {result.Code}");
            return result;
        }
        async Task<string> Attribute(string key, string? fallback = null)
        {
            var result = await execute("curl", ["-sf", "-H", "Metadata-Flavor: Google", $"{metadata}/attributes/{key}"], null, token);
            if (result.Code != 0) return fallback ?? throw new InvalidOperationException($"Cannot read metadata attribute {key}");
            return result.Output.TrimEnd('\r', '\n');
        }
        async Task Stage(string stage)
        {
            Console.WriteLine($"=== stage: {stage} ===");
            await execute("curl", ["-sf", "-X", "PUT", "--data", stage, "-H", "Metadata-Flavor: Google", $"{metadata}/guest-attributes/gh-runner/stage"], null, token);
        }
        Task<CommandResult> Apt(params string[] arguments) => Run("apt-get", ["-o", "DPkg::Lock::Timeout=900", "-y", .. arguments]);
        try
        {
            if (args.FirstOrDefault() == "--run-runner")
            {
                // The vendor runner itself requires Bash; keep its single-use credential off disk copies and logs.
                var config = (await File.ReadAllTextAsync(PathAt("/etc/gh-runner.jitconfig"), token)).Trim();
                var info = new ProcessStartInfo(Path.Combine(Environment.CurrentDirectory, "run.sh")) { UseShellExecute = false };
                info.ArgumentList.Add("--jitconfig");
                info.ArgumentList.Add(config);
                using var process = Process.Start(info)!;
                try { await process.WaitForExitAsync(token); return process.ExitCode; }
                finally
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None);
                }
            }
            await Stage("starting");
            var mount = PathAt(await Attribute("data-mount", "/mnt/gh-runner"));
            var runner = Path.Combine(mount, "actions-runner");
            var home = Path.Combine(mount, "home");
            var docker = Path.Combine(mount, "docker");
            var version = await Attribute("runner-version");
            Environment.SetEnvironmentVariable("DEBIAN_FRONTEND", "noninteractive");
            await Stage("apt-quiesce");
            foreach (var command in new[] { new[] { "stop", "unattended-upgrades.service" }, ["disable", "--now", "apt-daily.timer", "apt-daily-upgrade.timer"], ["stop", "apt-daily.service", "apt-daily-upgrade.service"] })
                await execute("systemctl", command, null, token);
            await Stage("disks");
            await Apt("update");
            var disks = Directory.GetFiles(PathAt("/dev/disk/by-id"), "google-local-nvme-ssd-*").Where(path => File.Exists(path) && !Regex.IsMatch(path, @"-part[0-9]")).Order(StringComparer.Ordinal).ToArray();
            var expected = int.Parse(await Attribute("local-ssd-count", "0"));
            Console.WriteLine($"local NVMe SSDs: found {disks.Length}, expected {expected} -> {string.Join(' ', disks)}");
            if (disks.Length != expected) throw new InvalidOperationException($"expected {expected} local SSDs but found {disks.Length}");
            Directory.CreateDirectory(mount);
            if (disks.Length == 0) Console.WriteLine($"WARNING: no local SSD requested, using the boot disk for {mount}");
            else
            {
                var device = disks[0];
                if (disks.Length > 1)
                {
                    await Apt("install", "--no-install-recommends", "mdadm");
                    await Run("mdadm", ["--create", "/dev/md0", "--level=0", "--force", $"--raid-devices={disks.Length}", .. disks]);
                    device = "/dev/md0";
                }
                await Run("mkfs.ext4", "-F", "-m", "0", "-E", "lazy_itable_init=1,lazy_journal_init=1", device);
                await Run("mount", "-o", "discard,defaults,noatime", device, mount);
            }
            await Run("chmod", "0755", mount);
            foreach (var path in new[] { runner, home, docker }) Directory.CreateDirectory(path);
            await Run("df", "-h", mount);
            await Stage("docker");
            await Run("install", "-d", "-m", "0755", "/etc/docker");
            await File.WriteAllTextAsync(PathAt("/etc/docker/daemon.json"), new JsonObject { ["data-root"] = docker, ["storage-driver"] = "overlay2", ["log-driver"] = "json-file", ["log-opts"] = new JsonObject { ["max-size"] = "100m", ["max-file"] = "3" } }.ToJsonString(), token);
            await Run("install", "-d", "-m", "0755", "/etc/apt/keyrings");
            await Run("curl", "-fsSL", "https://download.docker.com/linux/ubuntu/gpg", "-o", "/etc/apt/keyrings/docker.asc");
            await Run("chmod", "a+r", "/etc/apt/keyrings/docker.asc");
            var codename = File.ReadLines(PathAt("/etc/os-release")).First(line => line.StartsWith("VERSION_CODENAME=", StringComparison.Ordinal)).Split('=', 2)[1].Trim('"', '\'');
            var arch = (await Run("dpkg", "--print-architecture")).Output.Trim();
            await File.WriteAllTextAsync(PathAt("/etc/apt/sources.list.d/docker.list"), $"deb [arch={arch} signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/ubuntu {codename} stable\n", token);
            await Apt("update");
            await Apt("install", "docker-ce", "docker-ce-cli", "containerd.io", "docker-buildx-plugin", "docker-compose-plugin");
            await Run("systemctl", "enable", "--now", "docker");
            var actual = (await Run("docker", "info", "--format", "{{.DockerRootDir}}")).Output.Trim();
            if (actual != docker) throw new InvalidOperationException($"docker root is {actual}, expected {docker}");
            await Stage("tools");
            await Apt("install", "ca-certificates", "curl", "jq", "git", "git-lfs", "unzip", "zip", "tar", "make", "build-essential", "screen", "lshw", "dmidecode", "fio", "python3", "python3-venv");
            await Stage("runner-install");
            Environment.CurrentDirectory = runner;
            await Run("curl", "-fsSL", "-o", "runner.tar.gz", $"https://github.com/actions/runner/releases/download/v{version}/actions-runner-linux-x64-{version}.tar.gz");
            await Run("tar", "xzf", "runner.tar.gz");
            File.Delete("runner.tar.gz");
            await Run("./bin/installdependencies.sh");
            await Stage("runner-start");
            var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var writer = new StreamWriter(new FileStream(PathAt("/etc/gh-runner.jitconfig"), options))) await writer.WriteLineAsync((await Attribute("runner-jit-config")).AsMemory(), token);
            await File.WriteAllTextAsync(PathAt("/etc/systemd/system/gh-runner.service"), $"""
                [Unit]
                Description=GitHub Actions ephemeral JIT runner
                After=network-online.target docker.service
                Requires=docker.service
                Wants=network-online.target

                [Service]
                Type=simple
                User=root
                WorkingDirectory={runner}
                Environment=HOME={home}
                Environment=RUNNER_ALLOW_RUNASROOT=1
                Environment=DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
                ExecStart=/opt/gh-runner-bootstrap/dotnet/dotnet run --file /opt/gh-runner-bootstrap/Startup.cs -- --run-runner
                Restart=no
                KillMode=process
                TimeoutStopSec=60

                [Install]
                WantedBy=multi-user.target
                """ + "\n", token);
            await Run("systemctl", "daemon-reload");
            await Run("systemctl", "start", "gh-runner.service");
            await Stage("runner-started");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            await execute("curl", ["-sf", "-X", "PUT", "--data", exception.Message, "-H", "Metadata-Flavor: Google", $"{metadata}/guest-attributes/gh-runner/error"], null, CancellationToken.None);
            await Stage("failed");
            return 1;
        }


    }
}
