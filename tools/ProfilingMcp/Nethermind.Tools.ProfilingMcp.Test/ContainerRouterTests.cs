// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using NUnit.Framework;

namespace Nethermind.Tools.ProfilingMcp.Test;

public class ContainerRouterTests
{
    [Test]
    public void Splits_the_target_from_the_tool_arguments()
    {
        Dictionary<string, JsonElement> arguments = new()
        {
            ["pid"] = JsonSerializer.SerializeToElement(1),
            ["container"] = JsonSerializer.SerializeToElement("nethermind-hoodi"),
            ["dockerHost"] = JsonSerializer.SerializeToElement("node1"),
        };

        bool routed = ContainerRouter.TryTakeTarget(arguments, out string? dockerHost, out string? container, out Dictionary<string, JsonElement> rest);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(routed, Is.True);
            Assert.That((dockerHost, container), Is.EqualTo(("node1", "nethermind-hoodi")));
            Assert.That(rest.Keys, Is.EquivalentTo(new[] { "pid" }));
        }
    }

    [TestCase(null)]
    [TestCase("")]
    public void Leaves_calls_without_a_container_on_this_machine(string? container)
    {
        Dictionary<string, JsonElement> arguments = new() { ["pid"] = JsonSerializer.SerializeToElement(1) };
        if (container is not null)
            arguments["container"] = JsonSerializer.SerializeToElement(container);

        Assert.That(ContainerRouter.TryTakeTarget(arguments, out _, out _, out _), Is.False);
    }

    [Test]
    public void Adds_the_target_to_a_tool_schema()
    {
        JsonElement schema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { pid = new { type = "integer" } }, required = new[] { "pid" } });

        JsonElement properties = ContainerRouter.WithTargetArguments(schema).GetProperty("properties");

        Assert.That(properties.EnumerateObject().Select(static p => p.Name), Is.EquivalentTo(new[] { "pid", "container", "dockerHost" }));
    }

    [TestCase(null, new string[0])]
    [TestCase("node1", new[] { "--context", "node1" })]
    [TestCase("ssh://ahk@node1", new[] { "--host", "ssh://ahk@node1" })]
    public void Selects_the_engine(string? dockerHost, string[] expected) =>
        Assert.That(ContainerSidecar.EngineArguments(dockerHost), Is.EqualTo(expected));

    [TestCase("linux/amd64", ExpectedResult = "linux-x64")]
    [TestCase("linux/arm64", ExpectedResult = "linux-arm64")]
    [TestCase("windows/amd64", ExpectedResult = null)]
    public string? Picks_the_linux_build_for_the_engine(string platform) => ContainerSidecar.BuildFolder(platform);

    [TestCase("/nethermind-hoodi", ExpectedResult = "nethermind-hoodi")]
    [TestCase("nethermind-hoodi", ExpectedResult = "nethermind-hoodi")]
    public string Names_the_container_as_the_cli_does(string inspectedName) => ContainerSidecar.ContainerName(inspectedName);

    [TestCase("/profmcp/Nethermind.Tools.ProfilingMcp\0", ExpectedResult = true)]
    [TestCase("/profmcp/Nethermind.Tools.ProfilingMcp\0--sidecar-idle\0", ExpectedResult = false)]
    [TestCase("/nethermind/nethermind\0--config=hoodi\0", ExpectedResult = false)]
    [TestCase("", ExpectedResult = false)]
    public bool Tells_servers_from_the_idle_process_and_the_target(string cmdline) => ContainerSidecar.IsServer(cmdline);

    [TestCase(null, ExpectedResult = 30)]
    [TestCase("", ExpectedResult = 30)]
    [TestCase("5", ExpectedResult = 5)]
    [TestCase("0", ExpectedResult = 0)]
    [TestCase("-1", ExpectedResult = 30)]
    public double Reads_the_sidecar_idle_limit(string? minutes) => ContainerSidecar.IdleLimit(minutes).TotalMinutes;

    [Test]
    [CancelAfter(30_000)]
    public void Stops_a_command_that_hangs()
    {
        (string command, string[] arguments) = OperatingSystem.IsWindows() ? ("ping", new[] { "-n", "30", "127.0.0.1" }) : ("sleep", new[] { "30" });

        Assert.That(async () => await ContainerSidecar.RunAsync(command, arguments, TimeSpan.FromSeconds(1)),
            Throws.InvalidOperationException.With.Message.Contains("did not finish within"));
    }

    [Test]
    public async Task Replaces_a_sidecar_that_is_gone_before_calling_it()
    {
        int starts = 0;
        ContainerRouter router = new((_, _) =>
        {
            starts++;
            return Task.FromException<ModelContextProtocol.Client.McpClient>(new InvalidOperationException("container restarted"));
        });

        Lazy<Task<ModelContextProtocol.Client.McpClient>> first = await router.LiveSidecarAsync((null, "node"));
        _ = first.Value;
        Lazy<Task<ModelContextProtocol.Client.McpClient>> second = await router.LiveSidecarAsync((null, "node"));
        _ = second.Value;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(second, Is.Not.SameAs(first));
            Assert.That(starts, Is.EqualTo(2));
        }
    }

    [TestCase(null, ExpectedResult = "/tmp")]
    [TestCase("", ExpectedResult = "/tmp")]
    [TestCase("/", ExpectedResult = "/")]
    [TestCase("//", ExpectedResult = "/")]
    [TestCase("/data/tmp/", ExpectedResult = "/data/tmp")]
    public string Finds_the_target_temp_folder(string? tmpdir) => ContainerSidecar.TempFolder(tmpdir);

    [Test]
    public void Keeps_a_sidecar_that_is_starting_and_drops_one_that_failed_to_start()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ContainerRouter.IsGone(new TaskCompletionSource<ModelContextProtocol.Client.McpClient>().Task), Is.False);
            Assert.That(ContainerRouter.IsGone(Task.FromException<ModelContextProtocol.Client.McpClient>(new InvalidOperationException())), Is.True);
        }
    }
}
