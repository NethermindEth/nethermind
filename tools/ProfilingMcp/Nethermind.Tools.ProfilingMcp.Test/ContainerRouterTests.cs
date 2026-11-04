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

    [TestCase("1.0.0+4f761854596684de2666471afc8d8c6f694b3a12", ExpectedResult = "nethermindeth/profiling-mcp:4f761854596684de2666471afc8d8c6f694b3a12")]
    [TestCase("1.0.0", ExpectedResult = null)]
    [TestCase("1.0.0+", ExpectedResult = null)]
    [TestCase(null, ExpectedResult = null)]
    public string? Names_the_published_sidecar_image_by_commit(string? serverVersion) => ContainerSidecar.PublishedImage(serverVersion);
}
