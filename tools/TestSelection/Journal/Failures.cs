// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.TestSelection.Journal;

/// <summary>An EEST test function on one fork.</summary>
public readonly record struct EestTest(string Name, string Fork);

/// <summary>
/// Test-level failures of one run, EEST as <c>EEST|type|setup|test|fork</c> and NUnit as <c>Project|test</c>,
/// and the units whose failed jobs showed no test-level failure: a runner, checkout or auth problem.
/// </summary>
public sealed record Failures(IReadOnlyList<string> Tests, IReadOnlyList<string> Infra)
{
    private const string EestPrefix = "EEST|";
    private const string HivePrefix = "Hive|";

    public static Failures None { get; } = new([], []);

    public IReadOnlySet<EestTest> EestTests()
    {
        HashSet<EestTest> tests = [];
        foreach (string test in Tests)
        {
            if (test.StartsWith(EestPrefix, StringComparison.Ordinal))
            {
                string[] parts = test.Split('|');
                tests.Add(new EestTest(parts[3], parts[4]));
            }
        }

        return tests;
    }

    public IReadOnlySet<string> Projects()
    {
        HashSet<string> projects = [];
        foreach (string test in Tests)
        {
            if (!test.StartsWith(EestPrefix, StringComparison.Ordinal) && !test.StartsWith(HivePrefix, StringComparison.Ordinal))
            {
                projects.Add(test[..test.IndexOf('|')]);
            }
        }

        return projects;
    }
}
