// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using ModelContextProtocol.Client;
using Nethermind.Tools.ProfilingMcp.Memory;

namespace Nethermind.Tools.ProfilingMcp;

/// <summary>
/// Connects to a server for a Docker container's processes, running in a sidecar container that shares the target's process namespace.
/// </summary>
/// <remarks>
/// EventPipe reaches a process through a socket in its own <c>/tmp</c>, which a server outside the container cannot see.
/// The sidecar sees the target's processes under their own PIDs and reaches the socket through <c>/proc/1/root/tmp</c>, so nothing is
/// copied into the target and the target needs no capability.
/// There is one sidecar per target and sidecar image, shared by every session: each connection runs its own server in it with
/// <c>docker exec</c>. It ends with the target, since the target's process namespace ends with the target's first process, or after
/// it has served no session for <see cref="DefaultIdleMinutes"/> minutes.
/// The image is built from the Linux build published next to this executable, and results go to a volume per target. Neither needs
/// a bind mount, so the engine can be remote.
/// </remarks>
internal static class ContainerSidecar
{
    /// <summary>Set in the sidecar to its container name, so the server knows it runs there and can say how to copy files out.</summary>
    public const string NameVariable = "PROFILING_MCP_SIDECAR";

    /// <summary>Set in the sidecar to the docker command that reaches its engine, e.g. <c>docker --context node1</c>.</summary>
    public const string DockerCommandVariable = "PROFILING_MCP_DOCKER";

    /// <summary>The argument that keeps the sidecar's first process running, waiting for servers started with <c>docker exec</c>.</summary>
    public const string IdleOption = "--sidecar-idle";

    /// <summary>The target container's file system as the sidecar sees it.</summary>
    public const string TargetRoot = "/proc/1/root";

    /// <summary>Set to an image to run the sidecar in instead of one built from this server's Linux build.</summary>
    public const string ImageVariable = "PROFILING_MCP_SIDECAR_IMAGE";

    /// <summary>Minutes a sidecar keeps running without a session before it exits; 0 keeps it until its target stops.</summary>
    public const string IdleMinutesVariable = "PROFILING_MCP_SIDECAR_IDLE_MINUTES";

    public const int DefaultIdleMinutes = 30;

    /// <summary>How long one docker command may run; pulls and builds are the slow ones.</summary>
    internal static TimeSpan CommandTimeout { get; set; } = TimeSpan.FromMinutes(10);

    private const string TargetTempVariable = "PROFILING_MCP_TARGET_TMPDIR";
    private const string LocalRepository = "profiling-mcp";
    private const string TargetLabel = "nethermind.profmcp.target";
    private const string RuntimeImage = "mcr.microsoft.com/dotnet/runtime-deps:10.0";
    private const string Dockerfile = "sidecar.Dockerfile";
    private const string Executable = "Nethermind.Tools.ProfilingMcp";
    private const string ServerPath = $"/profmcp/{Executable}";

    /// <summary>Returns the docker CLI options that select an engine: a context name, or an address such as <c>ssh://user@host</c>.</summary>
    /// <param name="dockerHost">The engine, or null for the one the docker CLI uses by default.</param>
    public static string[] EngineArguments(string? dockerHost) => dockerHost switch
    {
        null or "" => [],
        _ when dockerHost.Contains("://", StringComparison.Ordinal) => ["--host", dockerHost],
        _ => ["--context", dockerHost],
    };

    /// <summary>Starts a server in the container's sidecar, starting the sidecar unless another session already did, and connects to it.</summary>
    /// <remarks>The first start on an engine builds the sidecar image, which can take minutes.</remarks>
    public static async Task<McpClient> StartAsync(string? dockerHost, string container)
    {
        string[] engine = EngineArguments(dockerHost);
        string sidecar = await EnsureSidecarAsync(engine, container);
        StdioClientTransport transport = new(new StdioClientTransportOptions
        {
            Name = sidecar,
            Command = "docker",
            Arguments = [.. engine, "exec", "--interactive", sidecar, ServerPath],
            StandardErrorLines = static line => Console.Error.WriteLine(line),
        });

        using CancellationTokenSource timeout = new(CommandTimeout);
        return await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);
    }

    /// <summary>Copies trusted DACs for the target's .NET runtimes into its sidecar, for the tools that load one (<see cref="DebuggingLibraries"/>).</summary>
    public static async Task ProvideDebuggingLibrariesAsync(string? dockerHost, string container, CancellationToken cancellationToken)
    {
        string[] engine = EngineArguments(dockerHost);
        string sidecar = await EnsureSidecarAsync(engine, container);
        string architecture = await DockerAsync(engine, "version", "--format", "{{.Server.Arch}}");
        await DebuggingLibraries.ProvideAsync(engine, sidecar, architecture, WorkspacePaths.FromEnvironment().Root, cancellationToken);
    }

    private static async Task<string> EnsureSidecarAsync(string[] engine, string container)
    {
        Target target = await InspectAsync(engine, container);
        string image = await SidecarImageAsync(engine);
        string imageId = await DockerAsync(engine, "image", "inspect", "--format", "{{.Id}}", image);
        // The name carries the target's ID, which never changes, so a container that later takes the target's name gets its own sidecar.
        string name = $"profmcp-{target.Name}-{target.Id[..12]}-{imageId.Replace("sha256:", "", StringComparison.Ordinal)[..12]}";
        if (await IsRunningAsync(engine, name))
            return name;

        // perf needs it to sample another process, and Docker's seccomp profile allows perf_event_open only with it; kernels before 5.8 lack it.
        string[] perfmon = HasPerfmon(await DockerAsync(engine, "version", "--format", "{{.Server.KernelVersion}}")) ? ["--cap-add", "PERFMON"] : [];
        try
        {
            await DockerAsync(engine,
            [
                "run", "--detach", "--rm", "--network", "none", "--name", name,
                "--label", $"{TargetLabel}={target.Id}",
                "--pid", $"container:{target.Id}",
                // Reaching the target's files through /proc/1/root needs it when the target runs as another user than root.
                "--cap-add", "SYS_PTRACE",
                .. perfmon,
                "--volume", $"profmcp-home-{target.Name}:/profmcp-home",
                // The client finds diagnostic sockets in its temp folder; the sidecar's own runtime must not create one there.
                "--env", $"TMPDIR={TargetRoot}{target.TempFolder}",
                "--env", "DOTNET_EnableDiagnostics=0",
                "--env", "PROFILING_MCP_HOME=/profmcp-home",
                "--env", $"{NameVariable}={name}",
                .. (string[])(target.ExplicitTempFolder ? ["--env", $"{TargetTempVariable}={target.TempFolder}"] : []),
                "--env", $"{DockerCommandVariable}={string.Join(' ', ["docker", .. engine])}",
                "--env", $"{IdleMinutesVariable}={IdleLimit(Environment.GetEnvironmentVariable(IdleMinutesVariable)).TotalMinutes.ToString(CultureInfo.InvariantCulture)}",
                image,
            ]);
        }
        catch (InvalidOperationException)
        {
            // Fine when another session started it first.
            if (!await IsRunningAsync(engine, name))
                throw;
        }

        return name;
    }

    internal static bool HasPerfmon(string kernelVersion)
    {
        string[] parts = kernelVersion.Split('.', 3);
        return parts.Length >= 2 && int.TryParse(parts[0], out int major) && int.TryParse(parts[1], out int minor) && (major, minor).CompareTo((5, 8)) >= 0;
    }

    private static async Task<bool> IsRunningAsync(string[] engine, string container)
    {
        try
        {
            return await DockerAsync(engine, "inspect", "--type", "container", "--format", "{{.State.Running}}", container) == "true";
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Run as the sidecar's first process: waits until no server has run in the sidecar for the idle limit, then returns.</summary>
    /// <remarks>
    /// The sidecar is privileged and can read the target's files, so it does not outlive its use: when this returns, the container stops and
    /// is removed. Servers started with <c>docker exec</c> show in the shared process namespace, whose other processes are the target's.
    /// </remarks>
    public static async Task IdleAsync()
    {
        TimeSpan limit = IdleLimit(Environment.GetEnvironmentVariable(IdleMinutesVariable));
        if (limit == TimeSpan.Zero)
        {
            await Task.Delay(Timeout.Infinite);
            return;
        }

        long lastBusy = Environment.TickCount64;
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, limit.TotalSeconds)));
            if (ServerRunning())
                lastBusy = Environment.TickCount64;
            else if (Environment.TickCount64 - lastBusy >= limit.TotalMilliseconds)
                return;
        }
    }

    internal static TimeSpan IdleLimit(string? minutes) =>
        TimeSpan.FromMinutes(double.TryParse(minutes, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && value >= 0 ? value : DefaultIdleMinutes);

    /// <remarks>
    /// Every sidecar of a target shares its process namespace, so servers of another sidecar, such as a newer version's, are visible too.
    /// Only the ones <c>docker exec</c> started in this container count, told apart by sharing this process's mount namespace.
    /// </remarks>
    private static bool ServerRunning()
    {
        string? ownMounts = new FileInfo("/proc/self/ns/mnt").LinkTarget;
        foreach (string process in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(process), out int pid) || pid == Environment.ProcessId)
                continue;

            try
            {
                if (IsServer(File.ReadAllText(Path.Combine(process, "cmdline"))) && new FileInfo(Path.Combine(process, "ns", "mnt")).LinkTarget == ownMounts)
                    return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The process ended, or belongs to the target and is not readable.
            }
        }

        return false;
    }

    /// <summary>Whether a <c>/proc/&lt;pid&gt;/cmdline</c> is a server started in the sidecar, as opposed to its idle process or the target's.</summary>
    internal static bool IsServer(string cmdline)
    {
        string[] arguments = cmdline.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        return arguments.Length > 0 && arguments[0] == ServerPath && !arguments.Contains(IdleOption);
    }

    /// <summary>Run inside the sidecar: makes a target's own temp folder resolve at the same path in the sidecar.</summary>
    /// <remarks>
    /// When the target's environment sets TMPDIR, the diagnostics client looks for its socket at that literal path instead of in its own
    /// temp folder, so the path is linked to the target's folder. An explicit <c>/tmp</c> or <c>/var/tmp</c> replaces the sidecar's own empty
    /// folder. The path comes from the target, so <see cref="CheckTargetTempFolder"/> keeps the link away from the sidecar's system folders.
    /// </remarks>
    public static void LinkTargetTempFolder()
    {
        if (Environment.GetEnvironmentVariable(TargetTempVariable) is not { Length: > 0 } temp)
            return;

        if (CheckTargetTempFolder(temp) is { } problem)
            throw new InvalidOperationException($"The target's TMPDIR {temp} {problem}.");

        string target = TargetRoot + temp;
        // Servers starting together race to replace the folder; whatever one of them did, the link is what must be there afterwards.
        for (int attempt = 0; new FileInfo(temp).LinkTarget != target; attempt++)
        {
            if (attempt == 5)
                throw new InvalidOperationException($"Could not link the target's TMPDIR {temp} in the sidecar.");

            try
            {
                if (Path.Exists(temp))
                {
                    if (temp is not ("/tmp" or "/var/tmp") || Directory.EnumerateFileSystemEntries(temp).GetEnumerator().MoveNext())
                    {
                        if (new FileInfo(temp).LinkTarget == target)
                            continue;
                        throw new InvalidOperationException($"The target's TMPDIR {temp} already exists in the sidecar.");
                    }

                    Directory.Delete(temp);
                }

                Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
                File.CreateSymbolicLink(temp, target);
            }
            catch (IOException)
            {
                // Another server in the sidecar is replacing it at the same time; look again.
            }
        }
    }

    /// <summary>Top-level folders of the sidecar image that a target's TMPDIR must not be linked into.</summary>
    private static readonly HashSet<string> SidecarSystemFolders =
        ["bin", "boot", "dev", "etc", "home", "lib", "lib32", "lib64", "libx32", "media", "mnt", "opt", "proc", "profmcp", "profmcp-home", "root", "run", "sbin", "srv", "sys", "usr", "var"];

    /// <summary>Says why a target's TMPDIR cannot be linked into the sidecar, or returns null when it can.</summary>
    /// <remarks>
    /// The path comes from the target's configuration, and the link is created as root in the sidecar, so it is limited to the temp folders
    /// and to folders that do not exist in the sidecar image; a path such as <c>/etc/ld.so.preload</c> would otherwise plant target-controlled
    /// configuration in the sidecar.
    /// </remarks>
    internal static string? CheckTargetTempFolder(string tmpdir)
    {
        if (!tmpdir.StartsWith('/'))
            return "is not an absolute path";

        string[] segments = tmpdir.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return "is the root folder";
        foreach (string segment in segments)
        {
            if (segment is "." or "..")
                return "is not a normalized path";
        }

        bool inTempFolder = segments[0] == "tmp" || segments is ["var", "tmp", ..];
        return inTempFolder || !SidecarSystemFolders.Contains(segments[0]) ? null : $"is under /{segments[0]}, a system folder of the sidecar, so it is not linked";
    }

    /// <param name="Name">The container's name, whichever of its name or IDs it was given by, so they all share one sidecar and volume.</param>
    /// <param name="TempFolder">Where the target's .NET runtime creates its diagnostic socket.</param>
    /// <param name="ExplicitTempFolder">Whether the target sets TMPDIR, so the diagnostics client looks for the socket at that literal path.</param>
    /// <param name="Id">The container's full ID, which identifies it for the sidecar's lifetime even if it is renamed.</param>
    private sealed record Target(string Id, string Name, string Image, string TempFolder, bool ExplicitTempFolder);

    private static async Task<Target> InspectAsync(string[] engine, string container)
    {
        using JsonDocument inspection = JsonDocument.Parse(await DockerAsync(engine, "inspect", "--type", "container", "--format", "{{json .}}", container));
        JsonElement root = inspection.RootElement;
        if (!root.GetProperty("State").GetProperty("Running").GetBoolean())
            throw new InvalidOperationException($"Container '{container}' is not running.");

        JsonElement config = root.GetProperty("Config");
        Dictionary<string, string> environment = [];
        if (config.TryGetProperty("Env", out JsonElement variables) && variables.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement variable in variables.EnumerateArray())
            {
                string text = variable.GetString() ?? "";
                int equals = text.IndexOf('=');
                if (equals > 0)
                    environment[text[..equals]] = text[(equals + 1)..];
            }
        }

        if (environment.GetValueOrDefault("DOTNET_EnableDiagnostics") == "0" || environment.GetValueOrDefault("DOTNET_EnableDiagnostics_IPC") == "0")
            throw new InvalidOperationException($"Container '{container}' disables the .NET diagnostics port (DOTNET_EnableDiagnostics=0), so it cannot be profiled.");

        string? tmpdir = environment.GetValueOrDefault("TMPDIR") is { Length: > 0 } set ? set : null;
        if (tmpdir is not null && CheckTargetTempFolder(TempFolder(tmpdir)) is { } problem)
            throw new InvalidOperationException($"Container '{container}' sets TMPDIR={tmpdir}, which {problem}; the sidecar cannot reach its diagnostics socket there.");

        return new Target(root.GetProperty("Id").GetString()!, ContainerName(root.GetProperty("Name").GetString()), config.GetProperty("Image").GetString()!, TempFolder(tmpdir), tmpdir is not null);
    }

    /// <summary>Turns the <c>.Name</c> docker inspect reports, such as <c>/nethermind-hoodi</c>, into the name the CLI takes.</summary>
    internal static string ContainerName(string? inspectedName) =>
        inspectedName?.TrimStart('/') is { Length: > 0 } name ? name : throw new InvalidOperationException("docker inspect reported no container name.");

    internal static string TempFolder(string? tmpdir) =>
        tmpdir is not { Length: > 0 } ? "/tmp" : tmpdir.TrimEnd('/') is { Length: > 0 } trimmed ? trimmed : "/";

    /// <summary>Returns the sidecar image for this server: the image named by <see cref="ImageVariable"/>, or one built from this server's Linux build.</summary>
    /// <remarks>The build for the engine's architecture is used, so amd64 and arm64 engines both work.</remarks>
    private static async Task<string> SidecarImageAsync(string[] engine)
    {
        string? choice = Environment.GetEnvironmentVariable(ImageVariable);
        if (choice is { Length: > 0 })
            return await IsPresentOrPulledAsync(engine, choice) ? choice : throw new InvalidOperationException($"Could not pull {choice}, the sidecar image set in {ImageVariable}.");

        string platform = await DockerAsync(engine, "version", "--format", "{{.Server.Os}}/{{.Server.Arch}}");
        string buildFolder = BuildFolder(platform)
            ?? throw new InvalidOperationException($"The Docker engine runs {platform}; only linux/amd64 and linux/arm64 are supported.");
        string build = Path.Combine(AppContext.BaseDirectory, buildFolder);
        string local = BuildVersion(build) is { } version
            ? $"{LocalRepository}:{version}"
            : throw new InvalidOperationException($"There is no Linux build in {build} to build the sidecar image from. Publish the server without a runtime identifier, which also publishes the Linux builds.");
        if (await SucceedsAsync(engine, "image", "inspect", local))
            return local;

        await DockerAsync(engine, "build", "--quiet", "--tag", local, "--build-arg", $"BASE={await BaseImageAsync(engine)}",
            "--file", Path.Combine(AppContext.BaseDirectory, Dockerfile), build);
        return local;
    }

    /// <summary>The folder of the Linux build for an engine's <c>os/arch</c>, or null for a platform without one.</summary>
    internal static string? BuildFolder(string platform) => platform switch
    {
        "linux/amd64" => "linux-x64",
        "linux/arm64" => "linux-arm64",
        _ => null,
    };

    private static async Task<bool> IsPresentOrPulledAsync(string[] engine, string image) =>
        await SucceedsAsync(engine, "image", "inspect", image) || await SucceedsAsync(engine, "pull", "--quiet", image);

    /// <summary>Returns the image to build the sidecar image on: .NET's runtime-deps image.</summary>
    /// <remarks>
    /// The sidecar needs only the native libraries of a self-contained .NET app. A fixed image keeps it independent of the target's, which may
    /// lack them (Alpine, distroless) and is never trusted: the sidecar runs as root with ptrace and perf access to the target.
    /// </remarks>
    private static async Task<string> BaseImageAsync(string[] engine) =>
        await IsPresentOrPulledAsync(engine, RuntimeImage)
            ? RuntimeImage
            : throw new InvalidOperationException($"Could not pull {RuntimeImage} to build the sidecar image on. Pull it on the engine, or set {ImageVariable} to an image to use.");

    /// <summary>Returns the full ID of a container given by name or ID, which stays the same if the container is renamed.</summary>
    public static Task<string> ContainerIdAsync(string? dockerHost, string container) =>
        DockerAsync(EngineArguments(dockerHost), "inspect", "--type", "container", "--format", "{{.Id}}", container);

    /// <summary>Identifies a Linux build and the Dockerfile by a hash, or returns null when the server was published without that build.</summary>
    private static string? BuildVersion(string build)
    {
        string server = Path.Combine(build, Executable + ".dll");
        if (!File.Exists(server))
            return null;

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(File.ReadAllBytes(server));
        string dockerfile = Path.Combine(AppContext.BaseDirectory, Dockerfile);
        if (File.Exists(dockerfile))
            hash.AppendData(File.ReadAllBytes(dockerfile));
        return Convert.ToHexStringLower(hash.GetHashAndReset())[..12];
    }

    internal static async Task<bool> SucceedsAsync(string[] engine, params string[] arguments)
    {
        try
        {
            await DockerAsync(engine, arguments);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Runs a docker CLI command on an engine and returns its output.</summary>
    public static Task<string> DockerAsync(string[] engine, params string[] arguments) => RunAsync("docker", [.. engine, .. arguments], CommandTimeout);

    /// <summary>Runs a command and returns its output, killing it when it runs longer than <paramref name="timeout"/>.</summary>
    /// <exception cref="InvalidOperationException">The command failed or timed out.</exception>
    /// <remarks>A docker command can hang, for example on an unreachable SSH engine; failing it lets the next call try again.</remarks>
    internal static async Task<string> RunAsync(string command, string[] arguments, TimeSpan timeout)
    {
        ProcessStartInfo info = new(command, arguments) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using Process process = Process.Start(info) ?? throw new InvalidOperationException($"Could not start {command}.");
        using CancellationTokenSource timer = new(timeout);
        await using CancellationTokenRegistration kill = timer.Token.Register(() =>
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // It has already exited.
            }
        });

        Task<string> error = process.StandardError.ReadToEndAsync();
        string output = (await process.StandardOutput.ReadToEndAsync()).Trim();
        await process.WaitForExitAsync();
        string commandLine = $"{command} {string.Join(' ', arguments)}";
        if (timer.IsCancellationRequested)
            throw new InvalidOperationException($"{commandLine} did not finish within {timeout.TotalSeconds:N0} s and was stopped.");

        return process.ExitCode == 0 ? output : throw new InvalidOperationException($"{commandLine}: {(await error).Trim()}");
    }
}
