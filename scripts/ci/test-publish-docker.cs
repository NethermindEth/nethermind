#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#:property PublishAot=false
#:include ../version.helpers.cs
#:include script-process.helpers.cs

var root = ScriptVersion.Root();
var action = File.ReadAllText(Path.Combine(root, ".github/actions/publish-docker/action.yaml")).Replace("\r\n", "\n");
var body = string.Join('\n', action.Split("      run: |\n", 2)[1].Split('\n').Select(line => line.StartsWith("        ", StringComparison.Ordinal) ? line[8..] : line));
var temporary = Directory.CreateTempSubdirectory("publish-docker-");
var saved = new Dictionary<string, string?>();
var passed = 0;
const string clientProject = "src/Nethermind/Nethermind.LightClient/Nethermind.LightClient.csproj";
const string clientDockerfile = "src/Nethermind/Nethermind.LightClient/Deployment/Dockerfile";
var clientVersion = ScriptVersion.Read(Path.Combine(root, clientProject));
var clientTag = $"light-client-{clientVersion}";
var executionVersion = ScriptVersion.Read(Path.Combine(root, "src/Nethermind/Directory.Build.props"));
var executionVersionAsClientTag = $"light-client-{executionVersion}";
try
{
    foreach (var (project, dockerfile, tag, expected) in new[]
    {
        ("src/Nethermind/Directory.Build.props", "Dockerfile", "test", executionVersion),
        (clientProject, clientDockerfile, clientTag, clientVersion),
        ("./" + clientProject, "./" + clientDockerfile, clientTag, clientVersion),
        ("", "./src/../" + clientDockerfile, clientTag, clientVersion)
    })
    {
        var result = await Publish(project, dockerfile, tag);
        Check(result.Code == 0 && result.Output.Contains($"DOCKER_ARG=VERSION={expected}\n", StringComparison.Ordinal) &&
            result.Output.Contains($"DOCKER_ARG=nethermindeth/nethermind:{tag}\n", StringComparison.Ordinal) &&
            result.Output.Contains("DOCKER_ARG=--push\n", StringComparison.Ordinal), $"project version {project}/{dockerfile}", result);
    }
    foreach (var tag in new[] { "latest", "performance", "test", "master", "master-abcdef0", "nightly", "light-client-wrong-version", "" })
        foreach (var reference in new[] { "refs/heads/test", "refs/heads/master" })
            Rejected(await Publish(clientProject, clientDockerfile, tag, reference), $"LC tag isolation {tag}/{reference}");
    foreach (var (dockerfile, project, tag) in new[]
    {
        (clientDockerfile, "src/Nethermind/Directory.Build.props", executionVersionAsClientTag),
        ("Dockerfile", clientProject, "test"), ("Dockerfile", clientProject, "nightly"),
        ("Dockerfile", "", clientTag), ("tools/Bootnode/Dockerfile", "", clientTag),
        ("Dockerfile", "", executionVersionAsClientTag)
    })
        Rejected(await Publish(project, dockerfile, tag, "refs/heads/master"), $"product version isolation {dockerfile}/{project}/{tag}");
    var clientPush = await Publish(clientProject, clientDockerfile, clientTag, "refs/heads/master", "push");
    Check(clientPush.Code == 0 && !clientPush.Output.Contains("DOCKER_ARG=nethermindeth/nethermind:master-", StringComparison.Ordinal),
        "LC master push does not add execution alias", clientPush);
    foreach (var (tag, reference, eventName, allowed, masterAlias) in new[]
    {
        ("latest", "refs/heads/release/1.0", "workflow_dispatch", true, false),
        ("performance", "refs/heads/performance", "push", true, false),
        ("", "refs/heads/test/branch", "push", true, false),
        ("", "refs/heads/light-client-verified-p2p", "push", true, false),
        ("", "refs/heads/light-client/topic", "push", true, false),
        ("light-client-verified-p2p", "refs/heads/test", "workflow_dispatch", true, false),
        ("nightly", "refs/heads/master", "schedule", true, false),
        ("master", "refs/heads/master", "push", true, true),
        ("nightly", "refs/heads/test", "workflow_dispatch", false, false),
        ("master", "refs/heads/test", "push", false, false),
        ("master-abcdef0", "refs/heads/test", "push", false, false)
    })
    {
        var result = await Publish(tag: tag, reference: reference, eventName: eventName);
        var publishedTag = (tag.Length == 0 ? reference["refs/heads/".Length..] : tag).Replace('/', '-');
        Check((result.Code == 0) == allowed && result.Output.Contains("DOCKER_ARG=--push\n", StringComparison.Ordinal) == allowed &&
            result.Output.Contains($"DOCKER_ARG=nethermindeth/nethermind:{publishedTag}\n", StringComparison.Ordinal) == allowed &&
            result.Output.Contains("DOCKER_ARG=nethermindeth/nethermind:master-aaaaaaa\n", StringComparison.Ordinal) == masterAlias,
            $"execution tag policy {tag}/{reference}/{eventName}", result);
    }
    var bootnode = await Publish("tools/Bootnode/Nethermind.Bootnode/Nethermind.Bootnode.csproj", "tools/Bootnode/Dockerfile", "1.0.0", image: "bootnode");
    Check(bootnode.Code == 0 && bootnode.Output.Contains("DOCKER_ARG=nethermindeth/bootnode:1.0.0\n", StringComparison.Ordinal) &&
        bootnode.Output.Contains("DOCKER_ARG=VERSION=1.0.0\n", StringComparison.Ordinal), "other product keeps its repository/version", bootnode);
    var projectWithSpaces = Path.Combine(temporary.FullName, "light client.csproj");
    File.Copy(Path.Combine(root, clientProject), projectWithSpaces);
    var spaced = await Publish(projectWithSpaces.Replace('\\', '/'));
    Check(spaced.Code == 0 && spaced.Output.Contains($"DOCKER_ARG=VERSION={clientVersion}\n", StringComparison.Ordinal), "version path with spaces", spaced);
    var invalidProject = Path.Combine(temporary.FullName, "invalid.csproj");
    foreach (var declaration in new[] { "", "1.0.0 extra", "1.0.0\nextra" })
    {
        File.WriteAllText(invalidProject, $"<Project><VersionPrefix>{declaration}</VersionPrefix></Project>\n");
        Rejected(await Publish(invalidProject.Replace('\\', '/')), $"invalid version declaration {declaration}");
    }
    Rejected(await Publish(Path.Combine(temporary.FullName, "missing.csproj").Replace('\\', '/')), "missing version file");
    foreach (var output in new[] { "", "1.0.0 extra", "1.0.0\nextra", "1.0.0;echo injected", "1.0.0$(echo injected)" })
        Rejected(await Publish(cliOutput: output), $"unusable version CLI output {output}");
    Console.WriteLine($"Passed {passed} tests.");
}
finally
{
    foreach (var (name, value) in saved) Environment.SetEnvironmentVariable(name, value);
    temporary.Delete(recursive: true);
}

void Set(string name, string? value)
{
    saved.TryAdd(name, Environment.GetEnvironmentVariable(name));
    Environment.SetEnvironmentVariable(name, value);
}

async Task<(int Code, string Output, string Error)> Publish(string versionFile = "", string dockerfile = "Dockerfile", string tag = "test",
    string reference = "refs/heads/test", string eventName = "workflow_dispatch", string image = "nethermind", string? cliOutput = null)
{
    foreach (var (name, value) in new[]
    {
        ("IMAGE_NAME", image), ("TAG", tag), ("DOCKERFILE", dockerfile), ("VERSION_FILE", versionFile),
        ("BUILD_CONFIG", "release"), ("PLATFORMS", "linux/amd64,linux/arm64"), ("ADD_MASTER_TAG", "true"),
        ("GITHUB_REF", reference), ("GITHUB_EVENT_NAME", eventName), ("GITHUB_SHA", new string('a', 40)),
        ("GITHUB_OUTPUT", Path.Combine(temporary.FullName, "output").Replace('\\', '/')), ("CI", "true")
    }) Set(name, value);
    Set("TEST_VERSION_OUTPUT", cliOutput);
    var mock = """
        docker() { printf 'DOCKER_ARG=%s\n' "$@"; }
        git() { printf '1793836800\n'; }
        """;
    if (cliOutput is not null) mock += "\ndotnet() { printf '%s\\n' \"$TEST_VERSION_OUTPUT\"; }\n";
    var script = Path.Combine(temporary.FullName, "publish.sh");
    File.WriteAllText(script, mock + "\n" + body);
    var shell = OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git/bin/bash.exe") : "bash";
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
    return await ScriptProcess.Run(shell, ["-e", script.Replace('\\', '/')], timeout.Token, workingDirectory: root);
}

void Check(bool condition, string name, (int Code, string Output, string Error) result)
{
    if (!condition) throw new InvalidOperationException($"{name}: exit {result.Code}\n{result.Output}{result.Error}");
    Console.WriteLine($"PASS {name}");
    passed++;
}

void Rejected((int Code, string Output, string Error) result, string name) =>
    Check(result.Code != 0 && !result.Output.Contains("DOCKER_ARG=", StringComparison.Ordinal), name, result);
