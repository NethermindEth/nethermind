// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;

namespace Nethermind.Core.Test;

/// <summary>Compares JSON against fixtures embedded in a test assembly, or writes them when capturing.</summary>
/// <remarks>Set <c>NETHERMIND_JSON_FIXTURE_CAPTURE</c> to a directory to write the fixtures instead of comparing.</remarks>
public static class JsonFixture
{
    private static readonly string? CaptureDirectory = Environment.GetEnvironmentVariable("NETHERMIND_JSON_FIXTURE_CAPTURE");

    /// <summary>Asserts that <paramref name="json"/> equals the fixture <paramref name="name"/>, or writes it there when capturing.</summary>
    public static void AssertMatches(Assembly assembly, string name, string json)
    {
        if (CaptureDirectory is not null)
        {
            File.WriteAllText(Path.Combine(CaptureDirectory, name + ".json"), json);
            return;
        }

        string suffix = "." + name + ".json";
        string? resource = assembly.GetManifestResourceNames().SingleOrDefault(r => r.EndsWith(suffix, StringComparison.Ordinal));
        Assert.That(resource, Is.Not.Null, $"missing fixture {name}.json");

        using StreamReader reader = new(assembly.GetManifestResourceStream(resource!)!);
        Assert.That(json, Is.EqualTo(reader.ReadToEnd()));
    }

    /// <summary>Reads the fixture <paramref name="name"/> embedded in <paramref name="assembly"/>.</summary>
    public static string Read(Assembly assembly, string name)
    {
        string suffix = "." + name + ".json";
        string resource = assembly.GetManifestResourceNames().Single(r => r.EndsWith(suffix, StringComparison.Ordinal));
        using StreamReader reader = new(assembly.GetManifestResourceStream(resource)!);
        return reader.ReadToEnd();
    }
}
