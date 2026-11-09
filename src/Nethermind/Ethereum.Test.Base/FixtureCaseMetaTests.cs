// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;

namespace Ethereum.Test.Base;

/// <summary>
/// Base class for MetaTests that verify every test case source of every test class in the assembly yields at least one
/// case.
/// </summary>
/// <remarks>
/// NUnit passes a test whose case source yields nothing, so a fixture class that finds no fixture files would otherwise
/// pass without running anything. A file that fails to load still yields a case, which fails.
/// </remarks>
[TestFixture]
public abstract class FixtureCaseMetaTests
{
    private const string ChunkVariable = "TEST_CHUNK";

    /// <summary>Test classes allowed to have a case source without cases, each with the reason.</summary>
    protected virtual IReadOnlyDictionary<Type, string> EmptyOnPurpose { get; } = new Dictionary<Type, string>();

    /// <remarks>
    /// Clears <c>TEST_CHUNK</c> while the sources run, since a chunk holds only part of a class's cases, and runs alone
    /// so that no other test sees the change.
    /// </remarks>
    [Test]
    [NonParallelizable]
    public void All_test_case_sources_have_cases()
    {
        string? chunk = Environment.GetEnvironmentVariable(ChunkVariable);
        Environment.SetEnvironmentVariable(ChunkVariable, null);
        try
        {
            List<string> problems = FindSourcesWithoutCases(
                GetType().Assembly.GetTypes().Where(static t => t.IsClass && !t.IsAbstract),
                EmptyOnPurpose);

            foreach (string problem in problems)
                Console.WriteLine(problem);

            Assert.That(problems, Is.Empty);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ChunkVariable, chunk);
        }
    }

    /// <summary>
    /// Lists every <see cref="TestCaseSourceAttribute"/> of <paramref name="testClasses"/> that yields no case or cannot
    /// be read, and every entry of <paramref name="emptyOnPurpose"/> that is not a class here with such a source.
    /// </summary>
    public static List<string> FindSourcesWithoutCases(IEnumerable<Type> testClasses, IReadOnlyDictionary<Type, string> emptyOnPurpose)
    {
        Type[] classes = [.. testClasses.OrderBy(static t => t.FullName, StringComparer.Ordinal)];
        List<string> problems = [];
        foreach (Type testClass in classes)
        {
            List<string> emptySources = [];
            foreach (MethodInfo method in testClass.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy))
            {
                foreach (TestCaseSourceAttribute source in method.GetCustomAttributes<TestCaseSourceAttribute>())
                {
                    if (FindProblem(testClass, source) is { } problem)
                        emptySources.Add($"{testClass.FullName}.{method.Name} ({source.SourceName}): {problem}");
                }
            }

            if (!emptyOnPurpose.ContainsKey(testClass))
                problems.AddRange(emptySources);
            else if (emptySources.Count == 0)
                problems.Add($"{testClass.FullName} is listed as empty on purpose but has no empty case source");
        }

        foreach (Type listed in emptyOnPurpose.Keys.Where(t => !classes.Contains(t)))
            problems.Add($"{listed.FullName} is listed as empty on purpose but is not a test class here");

        return problems;
    }

    private static string? FindProblem(Type testClass, TestCaseSourceAttribute source)
    {
        try
        {
            if (GetCases(testClass, source) is not { } cases)
                return "the source returned null";

            IEnumerator enumerator = cases.GetEnumerator();
            try
            {
                return enumerator.MoveNext() ? null : "the source yields no cases";
            }
            finally
            {
                (enumerator as IDisposable)?.Dispose();
            }
        }
        catch (Exception e)
        {
            return $"the source threw {(e is TargetInvocationException { InnerException: { } inner } ? inner : e)}";
        }
    }

    private static IEnumerable? GetCases(Type testClass, TestCaseSourceAttribute source)
    {
        Type sourceType = source.SourceType ?? testClass;
        if (source.SourceName is null)
            return (IEnumerable?)Activator.CreateInstance(sourceType);

        MemberInfo[] members = sourceType.GetMember(source.SourceName,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy);
        return members switch
        {
            [MethodInfo method] => (IEnumerable?)method.Invoke(null, source.MethodParams),
            [PropertyInfo property] => (IEnumerable?)property.GetValue(null),
            [FieldInfo field] => (IEnumerable?)field.GetValue(null),
            _ => throw new MissingMemberException(sourceType.FullName, source.SourceName)
        };
    }
}
