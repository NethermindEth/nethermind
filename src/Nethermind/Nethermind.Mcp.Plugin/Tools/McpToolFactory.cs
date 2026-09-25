// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Nethermind.Mcp.Plugin.Tools;

/// <summary>A set of MCP tools; every implementation in this assembly is registered and served automatically.</summary>
public interface IMcpToolSet
{
    /// <summary>Creates the MCP server tool descriptors of this set.</summary>
    IEnumerable<McpServerTool> CreateServerTools();
}

/// <summary>Declares the JSON Schema of a tool's successful <c>structuredContent</c> (the whole <c>{"result": ...}</c> object).</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class McpToolOutputSchemaAttribute(string json) : Attribute
{
    /// <summary>Gets the JSON Schema text.</summary>
    public string Json { get; } = json;
}

/// <summary>Builds MCP tool descriptors from methods annotated with <see cref="McpServerToolAttribute"/>.</summary>
internal static class McpToolFactory
{
    /// <summary>
    /// Creates a tool for each public instance method of <paramref name="target"/> annotated with <see cref="McpServerToolAttribute"/>.
    /// </summary>
    /// <param name="target">The instance the tool methods are invoked on.</param>
    /// <param name="descriptionSuffix">Returns node-specific text (such as configured limits) appended to a method's description.</param>
    /// <param name="maxResultSize">The configured result size limit, stated in every description.</param>
    public static IEnumerable<McpServerTool> Create(object target, Func<MethodInfo, string> descriptionSuffix, int maxResultSize)
    {
        MethodInfo[] methods = target.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
        List<McpServerTool> tools = new(methods.Length);
        foreach (MethodInfo method in methods)
        {
            if (method.GetCustomAttribute<McpServerToolAttribute>() is null)
            {
                continue;
            }

            McpServerToolCreateOptions options = new()
            {
                Description = $"{method.GetCustomAttribute<DescriptionAttribute>()?.Description}{descriptionSuffix(method)} Results larger than {maxResultSize} bytes fail with resource_exhausted."
            };

            McpServerTool tool = McpServerTool.Create(method, target, options);
            if (method.GetCustomAttribute<McpToolOutputSchemaAttribute>() is { } schema && tool.ProtocolTool.OutputSchema is null)
            {
                using JsonDocument document = JsonDocument.Parse(schema.Json);
                tool = new DeclaredSchemaTool(tool, document.RootElement.Clone());
            }

            tools.Add(tool);
        }

        return tools;
    }

    /// <summary>Advertises a declared output schema for a tool that builds its own <c>structuredContent</c>.</summary>
    /// <remarks>
    /// <see cref="McpServerTool.Create(MethodInfo, object?, McpServerToolCreateOptions?)"/> overwrites
    /// <see cref="McpServerToolCreateOptions.UseStructuredContent"/> with the value on <see cref="McpServerToolAttribute"/>
    /// (false by default) and then drops <see cref="McpServerToolCreateOptions.OutputSchema"/>. Setting the schema on the
    /// inner descriptor instead would make the SDK serialize every returned <see cref="CallToolResult"/> into a structured
    /// response it then discards, so the schema lives on a separate descriptor copy and invocation passes straight through.
    /// </remarks>
    private sealed class DeclaredSchemaTool(McpServerTool inner, JsonElement outputSchema) : DelegatingMcpServerTool(inner)
    {
        public override Tool ProtocolTool { get; } = new()
        {
            Name = inner.ProtocolTool.Name,
            Title = inner.ProtocolTool.Title,
            Description = inner.ProtocolTool.Description,
            InputSchema = inner.ProtocolTool.InputSchema,
            OutputSchema = outputSchema,
            Annotations = inner.ProtocolTool.Annotations,
            Icons = inner.ProtocolTool.Icons,
            Meta = inner.ProtocolTool.Meta
        };
    }
}
