// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Nodes;

namespace Nethermind.TestSelection.Matrices;

/// <summary>
/// NUnit job matrices as scripts/ci/test-matrices.json holds them. A matrix value names test projects in one of three
/// shapes: a project name, a <c>test</c> object with a <c>project</c>, or a pack <c>{name, list}</c> whose list
/// holds several projects that run one after another in one job.
/// </summary>
public static class TestMatrix
{
    // Matrix keys whose values name test projects; the other keys (runner, variant, chunk) are dimensions.
    private static readonly string[] ProjectKeys = ["project", "test", "include", "exclude"];
    private const string Include = "include";
    private const string Exclude = "exclude";

    /// <summary>
    /// The matrix with only the selected test projects, or <c>null</c> when none remains. Every include entry of
    /// these matrices adds its own combination, so filtering by project keeps GitHub's expansion intact.
    /// </summary>
    public static JsonObject? Filter(JsonObject matrix, Func<string, bool> keepsProject)
    {
        JsonObject filtered = [];
        foreach ((string key, JsonNode? values) in matrix)
        {
            filtered[key] = ProjectKeys.Contains(key) ? FilterValues(values!.AsArray(), keepsProject) : values!.DeepClone();
        }

        bool hasProduct = filtered.Any(static pair => pair.Key is not (Include or Exclude));
        if (hasProduct && filtered.Where(static pair => pair.Key is not (Include or Exclude)).All(static pair => pair.Value!.AsArray().Count > 0))
        {
            return filtered;
        }

        // Without the product, an include entry that names no project would start a job of its own.
        JsonArray include = [.. (filtered[Include]?.AsArray() ?? []).Where(static entry => ProjectsOf(entry!).Count > 0).Select(static entry => entry!.DeepClone())];
        return include.Count > 0 ? new JsonObject { [Include] = include } : null;
    }

    /// <summary>Every test project that the matrices of one workflow name.</summary>
    public static IReadOnlySet<string> AllProjects(JsonObject workflowMatrices)
    {
        HashSet<string> projects = [];
        foreach ((string _, JsonNode? matrix) in workflowMatrices)
        {
            foreach (string key in ProjectKeys)
            {
                foreach (JsonNode? value in matrix![key]?.AsArray() ?? [])
                {
                    projects.UnionWith(ProjectsOf(value!));
                }
            }
        }

        return projects;
    }

    /// <summary>The test projects a matrix value names: none for an include entry that only sets a dimension.</summary>
    public static IReadOnlyList<string> ProjectsOf(JsonNode value) => value switch
    {
        JsonValue name => [name.GetValue<string>()],
        JsonObject pack when pack["list"] is { } list => list.GetValue<string>().Split(' ', StringSplitOptions.RemoveEmptyEntries),
        JsonObject entry when entry["project"] is { } project => ProjectsOf(project),
        JsonObject entry when entry["test"] is { } test => ProjectsOf(test),
        _ => [],
    };

    private static JsonArray FilterValues(JsonArray values, Func<string, bool> keepsProject)
    {
        JsonArray kept = [];
        foreach (JsonNode? value in values)
        {
            if (Reduce(value!, keepsProject) is { } reduced)
            {
                kept.Add(reduced);
            }
        }

        return kept;
    }

    /// <summary>
    /// The value with only the selected projects, or <c>null</c> when it names projects and none is selected. A pack
    /// keeps its name and runs only its selected projects.
    /// </summary>
    private static JsonNode? Reduce(JsonNode value, Func<string, bool> keepsProject)
    {
        IReadOnlyList<string> projects = ProjectsOf(value);
        if (projects.Count == 0)
        {
            return value.DeepClone();
        }

        string[] kept = [.. projects.Where(keepsProject)];
        if (kept.Length == 0)
        {
            return null;
        }

        return kept.Length == projects.Count ? value.DeepClone() : WithProjects(value, kept);
    }

    private static JsonNode WithProjects(JsonNode value, string[] projects)
    {
        JsonObject copy = value.DeepClone().AsObject();
        JsonObject pack = copy["list"] is not null ? copy : copy["project"]!.AsObject();
        pack["list"] = string.Join(' ', projects);
        return copy;
    }
}
