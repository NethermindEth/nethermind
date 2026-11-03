// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using ModelContextProtocol.Client;

namespace Nethermind.Tools.ProfilingMcp;

/// <summary>
/// Connects to a server for a Docker container's processes, running in a sidecar container that shares the target's process namespace.
/// </summary>
/// <remarks>
/// EventPipe reaches a process through a socket in its own <c>/tmp</c>, which a server outside the container cannot see.
/// The sidecar sees the target's processes under their own PIDs and reaches the socket through <c>/proc/1/root/tmp</c>, so nothing is
/// copied into the target and the target needs no capability.
/// There is one sidecar per target and server version, shared by every session: each connection runs its own server in it with
/// <c>docker exec</c>. It ends with the target, since the target's process namespace ends with the target's first process.
/// The Linux build published next to this executable is copied into a Docker volume once per version, and results go to a volume
/// per target. Neither needs a bind mount, so the engine can be remote.
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

    private const string TargetTempVariable = "PROFILING_MCP_TARGET_TMPDIR";

    private const string TargetLabel = "nethermind.profmcp.target";
    private const string LauncherLabel = "nethermind.profmcp.launcher";
    private const string RuntimeImage = "mcr.microsoft.com/dotnet/runtime-deps:10.0";
    private const string BuildFolder = "linux-x64";
    private const string CompleteMarker = ".complete";
    private static readonly TimeSpan LoaderWait = TimeSpan.FromMinutes(10);
    private const string Executable = "Nethermind.Tools.ProfilingMcp";

    /// <summary>Returns the docker CLI options that select an engine: a context name, or an address such as <c>ssh://user@host</c>.</summary>
    /// <param name="dockerHost">The engine, or null for the one the docker CLI uses by default.</param>
    public static string[] EngineArguments(string? dockerHost) => dockerHost switch
    {
        null or "" => [],
        _ when dockerHost.Contains("://", StringComparison.Ordinal) => ["--host", dockerHost],
        _ => ["--context", dockerHost],
    };

    /// <summary>Starts a server in the container's sidecar, starting the sidecar unless another session already did, and connects to it.</summary>
    /// <remarks>The first start on an engine pulls the sidecar image and copies the Linux build, which can take minutes.</remarks>
    public static async Task<McpClient> StartAsync(string? dockerHost, string container, CancellationToken cancellationToken)
    {
        string[] engine = EngineArguments(dockerHost);
        string sidecar = await EnsureSidecarAsync(engine, container);
        StdioClientTransport transport = new(new StdioClientTransportOptions
        {
            Name = sidecar,
            Command = "docker",
            Arguments = [.. engine, "exec", "--interactive", sidecar, $"/profmcp/{Executable}"],
            StandardErrorLines = static line => Console.Error.WriteLine(line),
        });

        return await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
    }

    private static async Task<string> EnsureSidecarAsync(string[] engine, string container)
    {
        Target target = await InspectAsync(engine, container);
        string version = BuildVersion();
        string name = $"profmcp-{container}-{version}";
        if (await IsRunningAsync(engine, name))
            return name;

        string platform = await DockerAsync(engine, "version", "--format", "{{.Server.Os}}/{{.Server.Arch}}");
        if (platform != "linux/amd64")
            throw new InvalidOperationException($"The Docker engine runs {platform}; only linux/amd64 is supported.");

        string image = await SidecarImageAsync(engine, target.Image);
        await RemoveOrphansAsync(engine, container);
        string buildVolume = await EnsureBuildVolumeAsync(engine, image, container, version);
        try
        {
            await DockerAsync(engine,
            [
                "run", "--detach", "--rm", "--network", "none", "--name", name,
                "--label", $"{TargetLabel}={container}",
                "--pid", $"container:{container}",
                // Reaching the target's files through /proc/1/root needs it when the target runs as another user than root.
                "--cap-add", "SYS_PTRACE",
                "--volume", $"{buildVolume}:/profmcp:ro",
                "--volume", $"profmcp-home-{container}:/profmcp-home",
                // The client finds diagnostic sockets in its temp folder; the sidecar's own runtime must not create one there.
                "--env", $"TMPDIR={TargetRoot}{target.TempFolder}",
                "--env", "DOTNET_EnableDiagnostics=0",
                "--env", "PROFILING_MCP_HOME=/profmcp-home",
                "--env", $"{NameVariable}={name}",
                "--env", $"{TargetTempVariable}={target.TempFolder}",
                "--env", $"{DockerCommandVariable}={string.Join(' ', ["docker", .. engine])}",
                "--entrypoint", $"/profmcp/{Executable}",
                image, IdleOption,
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

    /// <summary>Run inside the sidecar: makes a target's own temp folder resolve at the same path in the sidecar.</summary>
    /// <remarks>
    /// When the target's environment sets TMPDIR, the diagnostics client looks for its socket at that path instead of in its own
    /// temp folder, so the path is linked to the target's folder.
    /// </remarks>
    public static void LinkTargetTempFolder()
    {
        if (Environment.GetEnvironmentVariable(TargetTempVariable) is not { } temp || temp == "/tmp" || Path.Exists(temp))
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
        try
        {
            File.CreateSymbolicLink(temp, TargetRoot + temp);
        }
        catch (IOException) when (Path.Exists(temp))
        {
            // Another server in the sidecar linked it first.
        }
    }

    /// <param name="TempFolder">Where the target's .NET runtime creates its diagnostic socket.</param>
    private sealed record Target(string Image, string TempFolder);

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

        string temp = environment.GetValueOrDefault("TMPDIR") is { Length: > 0 } tmpdir ? tmpdir.TrimEnd('/') : "/tmp";
        return new Target(config.GetProperty("Image").GetString()!, temp);
    }

    /// <summary>Returns the image to run the sidecar in: .NET's runtime-deps image, or the target's when that cannot be pulled.</summary>
    /// <remarks>
    /// The sidecar needs only the native libraries of a self-contained .NET app. Using a fixed image keeps it independent of the target's,
    /// which may lack them (Alpine, distroless). An engine without access to the registry falls back to the target's image.
    /// </remarks>
    private static async Task<string> SidecarImageAsync(string[] engine, string targetImage)
    {
        if (await SucceedsAsync(engine, "image", "inspect", RuntimeImage) || await SucceedsAsync(engine, "pull", "--quiet", RuntimeImage))
            return RuntimeImage;

        await Console.Error.WriteLineAsync($"Could not pull {RuntimeImage}; running the sidecar in the target's image {targetImage}.");
        return targetImage;
    }

    /// <summary>Removes helper containers of the container whose launching server, on this machine, is no longer running.</summary>
    /// <remarks>
    /// These are build loaders left by a server killed while copying the build, and sidecars of earlier versions of this server,
    /// which ran one per session.
    /// </remarks>
    private static async Task RemoveOrphansAsync(string[] engine, string container)
    {
        string sidecars = await DockerAsync(engine, "ps", "--all", "--filter", $"label={TargetLabel}={container}", "--format", $"{{{{.ID}}}} {{{{.Label \"{LauncherLabel}\"}}}}");
        foreach (string line in sidecars.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] parts = line.Split(' ');
            if (parts.Length == 2 && IsOrphan(parts[1]))
                await DockerAsync(engine, "rm", "--force", parts[0]);
        }
    }

    private static bool IsOrphan(string launcher)
    {
        int slash = launcher.LastIndexOf('/');
        if (slash < 0 || launcher[..slash] != Environment.MachineName || !int.TryParse(launcher[(slash + 1)..], out int pid))
            return false;

        try
        {
            using Process process = Process.GetProcessById(pid);
            return process.ProcessName != Process.GetCurrentProcess().ProcessName;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private static string Launcher(int pid) => $"{Environment.MachineName}/{pid}";

    /// <summary>Returns a volume holding this version of the Linux build, copying it in on first use.</summary>
    /// <remarks>
    /// A marker file copied last tells a complete volume from one whose copy was cut short, as when <c>claude mcp list</c> kills a
    /// server whose first start, pulling the image and copying the build, outlasts its health check.
    /// The container used for copying has a fixed name per volume, so servers starting at the same time copy one after another
    /// instead of overwriting files a sidecar may already run.
    /// </remarks>
    private static async Task<string> EnsureBuildVolumeAsync(string[] engine, string image, string container, string version)
    {
        string build = Path.Combine(AppContext.BaseDirectory, BuildFolder);
        string volume = $"profmcp-{version}";

        // docker cp reaches a volume through a container that mounts it; this one is never started. Its launcher label lets another
        // server remove it if this process is killed before it does.
        string loader = $"{volume}-load";
        await AcquireLoaderAsync(engine, loader,
            "--volume", $"{volume}:/profmcp",
            "--label", $"{TargetLabel}={container}", "--label", $"{LauncherLabel}={Launcher(Environment.ProcessId)}",
            "--entrypoint", "/bin/true", image);
        string marker = Path.GetTempFileName();
        try
        {
            if (!await SucceedsAsync(engine, "cp", $"{loader}:/profmcp/{CompleteMarker}", "-"))
            {
                await DockerAsync(engine, "cp", build + Path.DirectorySeparatorChar + ".", $"{loader}:/profmcp");
                await DockerAsync(engine, "cp", marker, $"{loader}:/profmcp/{CompleteMarker}");
            }
        }
        finally
        {
            File.Delete(marker);
            await DockerAsync(engine, "rm", loader);
        }

        return volume;
    }

    /// <summary>Creates the loader container, waiting while another server holds one of that name.</summary>
    private static async Task AcquireLoaderAsync(string[] engine, string loader, params string[] options)
    {
        string[] create = ["create", "--name", loader, .. options];
        Stopwatch waited = Stopwatch.StartNew();
        while (!await SucceedsAsync(engine, create))
        {
            // Without a holder, creating failed for another reason, which this reports.
            if (await LauncherOfAsync(engine, loader) is not { } holder)
            {
                await DockerAsync(engine, create);
                return;
            }

            if (IsOrphan(holder))
                await SucceedsAsync(engine, "rm", "--force", loader);
            else if (waited.Elapsed > LoaderWait)
                throw new InvalidOperationException($"Waited {LoaderWait.TotalMinutes:N0} min for the server {holder} to finish copying the build with {loader}; remove that container if the server is gone.");
            else
                await Task.Delay(TimeSpan.FromSeconds(1));
        }
    }

    private static async Task<string?> LauncherOfAsync(string[] engine, string loader)
    {
        try
        {
            return await DockerAsync(engine, "inspect", "--type", "container", "--format", $"{{{{index .Config.Labels \"{LauncherLabel}\"}}}}", loader);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Identifies the Linux build by a hash of the server assembly.</summary>
    private static string BuildVersion()
    {
        string build = Path.Combine(AppContext.BaseDirectory, BuildFolder);
        string server = Path.Combine(build, Executable + ".dll");
        if (!File.Exists(server))
            throw new InvalidOperationException($"No Linux build in {build}. Publish the server without a Linux runtime identifier, which also publishes it there.");

        using FileStream stream = File.OpenRead(server);
        return Convert.ToHexStringLower(SHA256.HashData(stream))[..12];
    }

    private static async Task<bool> SucceedsAsync(string[] engine, params string[] arguments)
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
    public static async Task<string> DockerAsync(string[] engine, params string[] arguments)
    {
        arguments = [.. engine, .. arguments];
        ProcessStartInfo info = new("docker", arguments) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using Process docker = Process.Start(info) ?? throw new InvalidOperationException("Could not start docker.");
        Task<string> error = docker.StandardError.ReadToEndAsync();
        string output = (await docker.StandardOutput.ReadToEndAsync()).Trim();
        await docker.WaitForExitAsync();
        return docker.ExitCode == 0 ? output : throw new InvalidOperationException($"docker {string.Join(' ', arguments)}: {(await error).Trim()}");
    }
}
