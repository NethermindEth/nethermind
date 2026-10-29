// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using NUnit.Framework;

namespace Nethermind.Mcp.Plugin.Test;

[Parallelizable(ParallelScope.All)]
public class McpSchemaValidatorTests
{
    [Test]
    public void Tool_schema_validation_rejects_undeclared_properties_without_an_explicit_closed_marker()
    {
        using JsonDocument schema = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{\"known\":{\"type\":\"integer\"}}}");
        using JsonDocument value = JsonDocument.Parse("{\"known\":1,\"extra\":2}");

        AssertionException? failure = Assert.Throws<AssertionException>(() => McpSchemaValidator.AssertConforms(schema.RootElement, value.RootElement));

        Assert.That(failure!.Message, Does.Contain("undeclared property 'extra'"));
    }

    [Test]
    public void Closed_schema_rejects_undeclared_properties([Values] bool nested, [Values] bool properties)
    {
        string closed = "{\"type\":\"object\",\"additionalProperties\":false"
            + (properties ? ",\"properties\":{\"known\":{\"type\":\"integer\"}}" : "") + "}";
        using JsonDocument schema = JsonDocument.Parse(nested
            ? "{\"type\":\"array\",\"items\":{\"$ref\":\"#/$defs/closed\"},\"$defs\":{\"closed\":" + closed + "}}" : closed);
        using JsonDocument value = JsonDocument.Parse(nested ? "[{\"extra\":1}]" : "{\"extra\":1}");

        AssertionException? failure = Assert.Throws<AssertionException>(() => McpSchemaValidator.AssertConforms(schema.RootElement, value.RootElement));

        Assert.That(failure!.Message, Does.Contain("extra"));
    }
}
