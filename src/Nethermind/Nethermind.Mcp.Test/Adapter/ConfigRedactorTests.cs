// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System.Collections.Generic;
using Nethermind.Mcp.Adapter;
using NUnit.Framework;

namespace Nethermind.Mcp.Test.Adapter;

public class ConfigRedactorTests
{
    [Test]
    public void Redact_replaces_value_when_key_matches_secret_regex()
    {
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> input =
            new Dictionary<string, IReadOnlyDictionary<string, object?>>
            {
                ["Auth"] = new Dictionary<string, object?>
                {
                    ["JwtSecretFile"] = "/secret.txt",
                },
            };

        ConfigRedactor redactor = new();
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> result = redactor.Redact(input);

        Assert.That(result["Auth"]["JwtSecretFile"], Is.EqualTo("[REDACTED]"));
    }

    [Test]
    public void Redact_leaves_unmatched_keys_alone()
    {
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> input =
            new Dictionary<string, IReadOnlyDictionary<string, object?>>
            {
                ["JsonRpc"] = new Dictionary<string, object?>
                {
                    ["Port"] = 8545,
                },
            };

        ConfigRedactor redactor = new();
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> result = redactor.Redact(input);

        Assert.That(result["JsonRpc"]["Port"], Is.EqualTo(8545));
    }

    [Test]
    public void Redact_is_case_insensitive()
    {
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> input =
            new Dictionary<string, IReadOnlyDictionary<string, object?>>
            {
                ["Section"] = new Dictionary<string, object?>
                {
                    ["apiKey"] = "abc",
                    ["ApiKEY"] = "xyz",
                    ["APIKEY"] = "123",
                },
            };

        ConfigRedactor redactor = new();
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> result = redactor.Redact(input);

        Assert.That(result["Section"]["apiKey"], Is.EqualTo("[REDACTED]"));
        Assert.That(result["Section"]["ApiKEY"], Is.EqualTo("[REDACTED]"));
        Assert.That(result["Section"]["APIKEY"], Is.EqualTo("[REDACTED]"));
    }

    [Test]
    public void Redact_descends_into_nested_dictionaries()
    {
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> input =
            new Dictionary<string, IReadOnlyDictionary<string, object?>>
            {
                ["Outer"] = new Dictionary<string, object?>
                {
                    ["Inner"] = (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
                    {
                        ["Password"] = "topsecret",
                        ["Port"] = 8545,
                    },
                },
            };

        ConfigRedactor redactor = new();
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> result = redactor.Redact(input);

        IReadOnlyDictionary<string, object?> inner = (IReadOnlyDictionary<string, object?>)result["Outer"]["Inner"]!;
        Assert.That(inner["Password"], Is.EqualTo("[REDACTED]"));
        Assert.That(inner["Port"], Is.EqualTo(8545));
    }

    [Test]
    public void Redact_returns_new_dictionary_instances()
    {
        Dictionary<string, object?> innerSection = new()
        {
            ["JwtSecretFile"] = "/secret.txt",
            ["Port"] = 8545,
        };
        Dictionary<string, IReadOnlyDictionary<string, object?>> input = new()
        {
            ["Auth"] = innerSection,
        };

        ConfigRedactor redactor = new();
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> result = redactor.Redact(input);

        Assert.That(result, Is.Not.SameAs(input));
        Assert.That(result["Auth"], Is.Not.SameAs(innerSection));
        Assert.That(innerSection["JwtSecretFile"], Is.EqualTo("/secret.txt"));
    }
}
