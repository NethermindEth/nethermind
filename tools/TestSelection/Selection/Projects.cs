// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.RegularExpressions;
using Nethermind.TestSelection.Files;

namespace Nethermind.TestSelection.Selection;

/// <summary>
/// The source projects of the repository and the paths they build from, for mapping changed files to projects and
/// projects to the test projects that use them. Projects are repository-relative directories such as
/// <c>src/Nethermind/Nethermind.Evm</c>.
/// </summary>
public sealed partial class Projects
{
    public const string SourceRoot = "src/Nethermind";
    private const string TestSuffix = ".Test";

    private static readonly IReadOnlySet<string> EmptySet = new HashSet<string>();

    private readonly IReadOnlyDictionary<string, IReadOnlySet<string>> _references;
    private readonly Dictionary<string, HashSet<string>> _owners;

    /// <param name="references">Each project's direct project references.</param>
    /// <param name="includes">
    /// Paths outside a project that it builds from, such as the chain specs, mapped to the projects that include them.
    /// </param>
    public Projects(IReadOnlyDictionary<string, IReadOnlySet<string>> references, IReadOnlyDictionary<string, IReadOnlySet<string>> includes)
    {
        _references = references;
        _owners = references.Keys.ToDictionary(static project => project, static project => new HashSet<string> { project });
        foreach ((string path, IReadOnlySet<string> projects) in includes)
        {
            _owners.GetOrAdd(path).UnionWith(projects);
        }
    }

    public static Projects OfRepository(string root) => RepositoryProjects.Read(root);

    /// <summary>The projects the files belong to, or every project when a file can affect every test.</summary>
    public Change Changed(IEnumerable<string> files)
    {
        HashSet<string> changed = [];
        foreach (string file in files)
        {
            HashSet<string> owners = OwnersOf(file);
            if (owners.Count > 0)
            {
                changed.UnionWith(owners);
            }
            else if (AffectsEveryTest(file))
            {
                return Change.Everything;
            }
            else if (NewProjectOf(file) is { } project)
            {
                changed.Add(project);
            }
        }

        return Change.Of(changed);
    }

    /// <summary>Test projects that changed, or that reference a changed project directly.</summary>
    public IReadOnlySet<string> DirectTests(IReadOnlySet<string> changed)
    {
        HashSet<string> tests = [];
        foreach ((string project, IReadOnlySet<string> references) in _references)
        {
            if (project.EndsWith(TestSuffix, StringComparison.Ordinal) && (changed.Contains(project) || references.Overlaps(changed)))
            {
                tests.Add(NameOf(project));
            }
        }

        return tests;
    }

    /// <summary>Projects that the project references, directly or through other projects.</summary>
    public IReadOnlySet<string> Dependencies(string project)
    {
        HashSet<string> found = [];
        Stack<string> pending = new([project]);
        while (pending.TryPop(out string? next))
        {
            foreach (string reference in _references.GetValueOrDefault(next, EmptySet))
            {
                if (found.Add(reference))
                {
                    pending.Push(reference);
                }
            }
        }

        return found;
    }

    public static string NameOf(string project) => project[(project.LastIndexOf('/') + 1)..];

    public static string DirectoryOf(string name) => $"{SourceRoot}/{name}";

    private HashSet<string> OwnersOf(string file)
    {
        HashSet<string> owners = [];
        for (string path = file; path.Length > 0; path = ParentOf(path))
        {
            if (_owners.TryGetValue(path, out HashSet<string>? found))
            {
                owners.UnionWith(found);
            }
        }

        return owners;
    }

    private static string ParentOf(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash < 0 ? string.Empty : path[..slash];
    }

    private static bool AffectsEveryTest(string file) => file == RepositoryProjects.UnknownChanges || EveryTest().IsMatch(file);

    // A file in a directory under src/Nethermind that no project owns yet belongs to a project the change adds;
    // whatever uses it changes its own references too.
    private static string? NewProjectOf(string file)
    {
        string[] parts = file.Split('/');
        return file.StartsWith(SourceRoot + "/", StringComparison.Ordinal) && parts.Length > 3 ? string.Join('/', parts[..3]) : null;
    }

    // Paths outside every project that change how every test builds or runs. Any other such path, such as another
    // workflow, a Dockerfile or a script, cannot change a test result. The submodules under src/ hold the fixtures that
    // tests read at run time.
    [GeneratedRegex(@"^(Directory\.[\w.]+|global\.json|nuget\.config|\.gitmodules|src/Nethermind/[^/]+\.(props|targets|json)" +
        @"|\.github/workflows/(nethermind-tests[\w-]*|run-nethtest|build-test-artifacts)\.yml)$" +
        @"|^(scripts/ci|tools/TestSelection|\.github/actions/(download-test-artifacts|fetch-submodules|select-tests)|src/(?!Nethermind/))")]
    private static partial Regex EveryTest();
}
