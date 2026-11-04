// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Reflection;
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
/// There is one sidecar per target and sidecar image, shared by every session: each connection runs its own server in it with
/// <c>docker exec</c>. It ends with the target, since the target's process namespace ends with the target's first process.
/// The image is pulled from Docker Hub or built from the Linux build published next to this executable, and results go to a volume
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

    /// <summary>
    /// Set to an image to run the sidecar in instead of the published one, or to <c>local</c> to build the image from this server's Linux build.
    /// </summary>
    public const string ImageVariable = "PROFILING_MCP_SIDECAR_IMAGE";

    private const string LocalChoice = "local";
    private const string PublishedRepository = "nethermindeth/profiling-mcp";
    private const string LocalRepository = "profiling-mcp";
    private const string TargetLabel = "nethermind.profmcp.target";
    private const string RuntimeImage = "mcr.microsoft.com/dotnet/runtime-deps:10.0";
    private const string BuildFolder = "linux-x64";
    private const string Dockerfile = "sidecar.Dockerfile";
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
    /// <remarks>The first start on an engine pulls or builds the sidecar image, which can take minutes.</remarks>
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
        string image = await SidecarImageAsync(engine, target.Image);
        string imageId = await DockerAsync(engine, "image", "inspect", "--format", "{{.Id}}", image);
        string name = $"profmcp-{container}-{imageId.Replace("sha256:", "", StringComparison.Ordinal)[..12]}";
        if (await IsRunningAsync(engine, name))
            return name;

        // perf needs it to sample another process, and Docker's seccomp profile allows perf_event_open only with it; kernels before 5.8 lack it.
        string[] perfmon = HasPerfmon(await DockerAsync(engine, "version", "--format", "{{.Server.KernelVersion}}")) ? ["--cap-add", "PERFMON"] : [];
        try
        {
            await DockerAsync(engine,
            [
                "run", "--detach", "--rm", "--network", "none", "--name", name,
                "--label", $"{TargetLabel}={container}",
                "--pid", $"container:{container}",
                // Reaching the target's files through /proc/1/root needs it when the target runs as another user than root.
                "--cap-add", "SYS_PTRACE",
                .. perfmon,
                "--volume", $"profmcp-home-{container}:/profmcp-home",
                // The client finds diagnostic sockets in its temp folder; the sidecar's own runtime must not create one there.
                "--env", $"TMPDIR={TargetRoot}{target.TempFolder}",
                "--env", "DOTNET_EnableDiagnostics=0",
                "--env", "PROFILING_MCP_HOME=/profmcp-home",
                "--env", $"{NameVariable}={name}",
                "--env", $"{TargetTempVariable}={target.TempFolder}",
                "--env", $"{DockerCommandVariable}={string.Join(' ', ["docker", .. engine])}",
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

        return new Target(config.GetProperty("Image").GetString()!, TempFolder(environment.GetValueOrDefault("TMPDIR")));
    }

    internal static string TempFolder(string? tmpdir) =>
        tmpdir is not { Length: > 0 } ? "/tmp" : tmpdir.TrimEnd('/') is { Length: > 0 } trimmed ? trimmed : "/";

    /// <summary>Returns the sidecar image for this server, pulling or building it when the engine does not have it.</summary>
    /// <remarks>
    /// In order: the image named by <see cref="ImageVariable"/>; one built earlier from this server's Linux build; the published image of
    /// the commit this server was built from; and last, one built from the Linux build. The published image serves the same tools, so it
    /// suits any server built from that commit, but not one built with uncommitted changes, which sets <see cref="ImageVariable"/> to
    /// <c>local</c> to skip it.
    /// </remarks>
    private static async Task<string> SidecarImageAsync(string[] engine, string targetImage)
    {
        string platform = await DockerAsync(engine, "version", "--format", "{{.Server.Os}}/{{.Server.Arch}}");
        if (platform != "linux/amd64")
            throw new InvalidOperationException($"The Docker engine runs {platform}; only linux/amd64 is supported.");

        string? choice = Environment.GetEnvironmentVariable(ImageVariable);
        if (choice is { Length: > 0 } && choice != LocalChoice)
            return await IsPresentOrPulledAsync(engine, choice) ? choice : throw new InvalidOperationException($"Could not pull {choice}, the sidecar image set in {ImageVariable}.");

        string? local = BuildVersion() is { } version ? $"{LocalRepository}:{version}" : null;
        if (local is not null && await SucceedsAsync(engine, "image", "inspect", local))
            return local;

        if (choice != LocalChoice && PublishedImage(ServerVersion) is { } published && await IsPresentOrPulledAsync(engine, published))
            return published;

        if (local is null)
            throw new InvalidOperationException($"Could not pull {PublishedImage(ServerVersion) ?? "a published sidecar image"} and there is no Linux build in {Path.Combine(AppContext.BaseDirectory, BuildFolder)} to build one from. Publish the server without a Linux runtime identifier, which also publishes it there.");

        await DockerAsync(engine, "build", "--quiet", "--tag", local, "--build-arg", $"BASE={await BaseImageAsync(engine, targetImage)}",
            "--file", Path.Combine(AppContext.BaseDirectory, Dockerfile), Path.Combine(AppContext.BaseDirectory, BuildFolder));
        return local;
    }

    /// <summary>Returns the published sidecar image for a server version such as <c>1.0.0+&lt;commit&gt;</c>, or null without a commit.</summary>
    internal static string? PublishedImage(string? serverVersion) =>
        serverVersion?.IndexOf('+') is int plus and >= 0 && plus < serverVersion.Length - 1 ? $"{PublishedRepository}:{serverVersion[(plus + 1)..]}" : null;

    private static string? ServerVersion => typeof(ContainerSidecar).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

    private static async Task<bool> IsPresentOrPulledAsync(string[] engine, string image) =>
        await SucceedsAsync(engine, "image", "inspect", image) || await SucceedsAsync(engine, "pull", "--quiet", image);

    /// <summary>Returns the image to build the sidecar image on: .NET's runtime-deps image, or the target's when that cannot be pulled.</summary>
    /// <remarks>
    /// The sidecar needs only the native libraries of a self-contained .NET app. Using a fixed image keeps it independent of the target's,
    /// which may lack them (Alpine, distroless). An engine without access to the registry falls back to the target's image.
    /// </remarks>
    private static async Task<string> BaseImageAsync(string[] engine, string targetImage)
    {
        if (await IsPresentOrPulledAsync(engine, RuntimeImage))
            return RuntimeImage;

        await Console.Error.WriteLineAsync($"Could not pull {RuntimeImage}; building the sidecar image on the target's image {targetImage}.");
        return targetImage;
    }

    /// <summary>Identifies the Linux build and the Dockerfile by a hash, or returns null when the server was published without a Linux build.</summary>
    private static string? BuildVersion()
    {
        string server = Path.Combine(AppContext.BaseDirectory, BuildFolder, Executable + ".dll");
        if (!File.Exists(server))
            return null;

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(File.ReadAllBytes(server));
        string dockerfile = Path.Combine(AppContext.BaseDirectory, Dockerfile);
        if (File.Exists(dockerfile))
            hash.AppendData(File.ReadAllBytes(dockerfile));
        return Convert.ToHexStringLower(hash.GetHashAndReset())[..12];
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
