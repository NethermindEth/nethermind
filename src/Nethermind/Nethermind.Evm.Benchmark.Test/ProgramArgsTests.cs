// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using Nethermind.Evm.Benchmark.GasBenchmarks;
using NUnit.Framework;

namespace Nethermind.Evm.Benchmark.Test;

/// <summary>
/// Tests for the Program.cs argument-parsing functions.
/// Since Program.cs uses top-level statements with local functions, we invoke them via reflection
/// on the compiler-generated Program class. These tests guard against breaking changes in the
/// CLI parsing logic and mode resolution.
/// </summary>
[TestFixture]
public class ProgramArgsTests
{
    private static readonly Type s_programType;
    private static readonly MethodInfo s_resolveModeDefinition;
    private static readonly MethodInfo s_removeArguments;
    private static readonly MethodInfo s_mergeWithClassFilter;
    private static readonly MethodInfo s_getOptionValue;
    private static readonly MethodInfo s_matchesScenarioFilter;
    private static readonly MethodInfo s_applyModeFilter;
    private static readonly MethodInfo s_applyChunkFilter;

    static ProgramArgsTests()
    {
        // The C# compiler generates a Program class (or <Program>$ in some versions)
        // with local functions as static methods prefixed with <<Main>$>g__ or similar.
        Assembly asm = typeof(GasBenchmarkConfig).Assembly;
        s_programType = asm.GetType("Program")
            ?? asm.GetType("<Program>$");

        if (s_programType is null)
            return;

        BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
        foreach (MethodInfo method in s_programType.GetMethods(flags))
        {
            string name = method.Name;
            if (name.Contains("ResolveModeDefinition"))
                s_resolveModeDefinition = method;
            else if (name.Contains("RemoveArguments"))
                s_removeArguments = method;
            else if (name.Contains("MergeWithClassFilter"))
                s_mergeWithClassFilter = method;
            else if (name.Contains("GetOptionValue"))
                s_getOptionValue = method;
            else if (name.Contains("MatchesScenarioFilter"))
                s_matchesScenarioFilter = method;
            else if (name.Contains("ApplyModeFilter"))
                s_applyModeFilter = method;
            else if (name.Contains("ApplyChunkFilter"))
                s_applyChunkFilter = method;
        }
    }

    [SetUp]
    public void SetUp()
    {
        if (s_programType is null)
            Assert.Ignore("Program type not found in assembly — skipping reflection-based tests");
    }

    // --- ResolveModeDefinition ---

    [TestCase("EVM", "*GasPayloadExecuteBenchmarks*")]
    [TestCase("evm", "*GasPayloadExecuteBenchmarks*")]
    [TestCase("EVMExecute", "*GasPayloadExecuteBenchmarks*")]
    [TestCase("EVMEXECUTE", "*GasPayloadExecuteBenchmarks*")]
    [TestCase("BlockBuilding", "*GasBlockBuildingBenchmarks*")]
    [TestCase("NewPayload", "*GasNewPayloadBenchmarks*")]
    [TestCase("NewPayloadMeasured", "*GasNewPayloadMeasuredBenchmarks*")]
    public void ResolveModeDefinition_Returns_Expected_ClassFilter(string mode, string expectedFilter)
    {
        if (s_resolveModeDefinition is null)
            Assert.Ignore("ResolveModeDefinition method not found");

        string classFilter = (string)s_resolveModeDefinition.Invoke(null, [mode]);

        Assert.That(classFilter, Is.EqualTo(expectedFilter));
    }

    [Test]
    public void ResolveModeDefinition_Throws_On_Unknown_Mode()
    {
        if (s_resolveModeDefinition is null)
            Assert.Ignore("ResolveModeDefinition method not found");

        TargetInvocationException ex = Assert.Throws<TargetInvocationException>(
            () => s_resolveModeDefinition.Invoke(null, ["UnknownMode"]));
        Assert.That(ex.InnerException, Is.TypeOf<ArgumentException>());
        Assert.That(ex.InnerException.Message, Does.Contain("Unknown --mode value"));
    }

    // --- RemoveArguments ---

    [Test]
    public void RemoveArguments_Removes_Single_Argument()
    {
        if (s_removeArguments is null)
            Assert.Ignore("RemoveArguments method not found");

        string[] args = ["--a", "--b", "--c"];
        string[] result = (string[])s_removeArguments.Invoke(null, [args, 1, 1]);

        Assert.That(result, Is.EqualTo(new[] { "--a", "--c" }));
    }

    [Test]
    public void RemoveArguments_Removes_Two_Arguments()
    {
        if (s_removeArguments is null)
            Assert.Ignore("RemoveArguments method not found");

        string[] args = ["--mode", "EVM", "--filter", "*foo*"];
        string[] result = (string[])s_removeArguments.Invoke(null, [args, 0, 2]);

        Assert.That(result, Is.EqualTo(new[] { "--filter", "*foo*" }));
    }

    [Test]
    public void RemoveArguments_Handles_Last_Element()
    {
        if (s_removeArguments is null)
            Assert.Ignore("RemoveArguments method not found");

        string[] args = ["--a", "--b"];
        string[] result = (string[])s_removeArguments.Invoke(null, [args, 1, 1]);

        Assert.That(result, Is.EqualTo(new[] { "--a" }));
    }

    // --- MergeWithClassFilter ---

    [Test]
    public void MergeWithClassFilter_Appends_Filter_When_None_Exists()
    {
        if (s_mergeWithClassFilter is null)
            Assert.Ignore("MergeWithClassFilter method not found");

        string[] args = ["--inprocess"];
        string[] result = (string[])s_mergeWithClassFilter.Invoke(null, [args, "*GasBlockBuildingBenchmarks*"]);

        Assert.That(result, Does.Contain("--filter"));
        Assert.That(result, Does.Contain("*GasBlockBuildingBenchmarks*"));
    }

    [Test]
    public void MergeWithClassFilter_Merges_With_Existing_Filter()
    {
        if (s_mergeWithClassFilter is null)
            Assert.Ignore("MergeWithClassFilter method not found");

        string[] args = ["--filter", "*MULMOD*"];
        string[] result = (string[])s_mergeWithClassFilter.Invoke(null, [args, "*GasBlockBuildingBenchmarks*"]);

        // Should merge: class filter prefix + existing filter
        Assert.That(result.Length, Is.EqualTo(2));
        string merged = result[1];
        Assert.That(merged, Does.Contain("GasBlockBuildingBenchmarks"));
        Assert.That(merged, Does.Contain("MULMOD"));
    }

    // --- GetOptionValue ---

    [Test]
    public void GetOptionValue_Reads_Equals_Syntax()
    {
        if (s_getOptionValue is null)
            Assert.Ignore("GetOptionValue method not found");

        string[] args = ["--mode=EVM"];
        object result = s_getOptionValue.Invoke(null, [args, 0, "--mode"]);
        ITuple tuple = (ITuple)result;
        string value = (string)tuple[0];
        int removeCount = (int)tuple[1];

        Assert.That(value, Is.EqualTo("EVM"));
        Assert.That(removeCount, Is.EqualTo(1));
    }

    [Test]
    public void GetOptionValue_Reads_Colon_Syntax()
    {
        if (s_getOptionValue is null)
            Assert.Ignore("GetOptionValue method not found");

        string[] args = ["--mode:BlockBuilding"];
        object result = s_getOptionValue.Invoke(null, [args, 0, "--mode"]);
        ITuple tuple = (ITuple)result;
        string value = (string)tuple[0];
        int removeCount = (int)tuple[1];

        Assert.That(value, Is.EqualTo("BlockBuilding"));
        Assert.That(removeCount, Is.EqualTo(1));
    }

    [Test]
    public void GetOptionValue_Reads_Space_Separated_Syntax()
    {
        if (s_getOptionValue is null)
            Assert.Ignore("GetOptionValue method not found");

        string[] args = ["--mode", "NewPayload"];
        object result = s_getOptionValue.Invoke(null, [args, 0, "--mode"]);
        ITuple tuple = (ITuple)result;
        string value = (string)tuple[0];
        int removeCount = (int)tuple[1];

        Assert.That(value, Is.EqualTo("NewPayload"));
        Assert.That(removeCount, Is.EqualTo(2));
    }

    [Test]
    public void GetOptionValue_Throws_When_No_Value_Available()
    {
        if (s_getOptionValue is null)
            Assert.Ignore("GetOptionValue method not found");

        string[] args = ["--mode"];
        TargetInvocationException ex = Assert.Throws<TargetInvocationException>(
            () => s_getOptionValue.Invoke(null, [args, 0, "--mode"]));
        Assert.That(ex.InnerException, Is.TypeOf<ArgumentException>());
    }

    // --- MatchesScenarioFilter ---

    [Test]
    public void MatchesScenarioFilter_Returns_True_For_Wildcard()
    {
        if (s_matchesScenarioFilter is null)
            Assert.Ignore("MatchesScenarioFilter method not found");

        string tempPath = Path.Combine(Path.GetTempPath(), "test_file.txt");
        GasPayloadBenchmarks.TestCase testCase = new(tempPath);

        bool result = (bool)s_matchesScenarioFilter.Invoke(null, [testCase, "*"]);

        Assert.That(result, Is.True);
    }

    [Test]
    public void MatchesScenarioFilter_Returns_True_For_Empty_Filter()
    {
        if (s_matchesScenarioFilter is null)
            Assert.Ignore("MatchesScenarioFilter method not found");

        string tempPath = Path.Combine(Path.GetTempPath(), "test_file.txt");
        GasPayloadBenchmarks.TestCase testCase = new(tempPath);

        bool result = (bool)s_matchesScenarioFilter.Invoke(null, [testCase, ""]);

        Assert.That(result, Is.True);
    }

    [Test]
    public void MatchesScenarioFilter_Matches_DisplayName()
    {
        if (s_matchesScenarioFilter is null)
            Assert.Ignore("MatchesScenarioFilter method not found");

        string tempPath = Path.Combine(Path.GetTempPath(), "tests_x.py__test_MULMOD-gas-value_100M.txt");
        GasPayloadBenchmarks.TestCase testCase = new(tempPath);

        bool result = (bool)s_matchesScenarioFilter.Invoke(null, [testCase, "*MULMOD*"]);

        Assert.That(result, Is.True);
    }

    [Test]
    public void MatchesScenarioFilter_No_Match_Returns_False()
    {
        if (s_matchesScenarioFilter is null)
            Assert.Ignore("MatchesScenarioFilter method not found");

        string tempPath = Path.Combine(Path.GetTempPath(), "tests_x.py__test_ADDMOD-gas-value_100M.txt");
        GasPayloadBenchmarks.TestCase testCase = new(tempPath);

        bool result = (bool)s_matchesScenarioFilter.Invoke(null, [testCase, "*MULMOD*"]);

        Assert.That(result, Is.False);
    }

    // --- ApplyModeFilter ---

    [Test]
    public void ApplyModeFilter_Returns_Args_Unchanged_When_No_Mode()
    {
        if (s_applyModeFilter is null)
            Assert.Ignore("ApplyModeFilter method not found");

        string[] args = ["--filter", "*foo*"];
        string[] result = (string[])s_applyModeFilter.Invoke(null, [args]);

        Assert.That(result, Is.EqualTo(args));
    }

    [Test]
    public void ApplyModeFilter_Removes_Mode_Arg_And_Adds_ClassFilter()
    {
        if (s_applyModeFilter is null)
            Assert.Ignore("ApplyModeFilter method not found");

        string[] args = ["--mode", "BlockBuilding"];
        string[] result = (string[])s_applyModeFilter.Invoke(null, [args]);

        Assert.That(result, Does.Contain("--filter"));
        Assert.That(result, Does.Contain("*GasBlockBuildingBenchmarks*"));
        // --mode and BlockBuilding should be removed
        Assert.That(result, Does.Not.Contain("--mode"));
        Assert.That(result, Does.Not.Contain("BlockBuilding"));
    }

    [Test]
    public void ApplyModeFilter_Handles_Equals_Syntax()
    {
        if (s_applyModeFilter is null)
            Assert.Ignore("ApplyModeFilter method not found");

        string[] args = ["--mode=EVM"];
        string[] result = (string[])s_applyModeFilter.Invoke(null, [args]);

        Assert.That(result, Does.Contain("--filter"));
        Assert.That(result, Does.Contain("*GasPayloadExecuteBenchmarks*"));
    }

    // --- ApplyChunkFilter ---

    [Test]
    public void ApplyChunkFilter_Returns_Args_Unchanged_When_No_Chunk()
    {
        if (s_applyChunkFilter is null)
            Assert.Ignore("ApplyChunkFilter method not found");

        string[] args = ["--filter", "*foo*"];
        string[] result = (string[])s_applyChunkFilter.Invoke(null, [args]);

        Assert.That(result, Is.EqualTo(args));
    }

    [Test]
    public void ApplyChunkFilter_Sets_ChunkIndex_And_ChunkTotal()
    {
        if (s_applyChunkFilter is null)
            Assert.Ignore("ApplyChunkFilter method not found");

        try
        {
            string[] args = ["--chunk", "2/5"];
            string[] result = (string[])s_applyChunkFilter.Invoke(null, [args]);

            Assert.That(GasBenchmarkConfig.ChunkIndex, Is.EqualTo(2));
            Assert.That(GasBenchmarkConfig.ChunkTotal, Is.EqualTo(5));
            Assert.That(result, Does.Not.Contain("--chunk"));
            Assert.That(result, Does.Not.Contain("2/5"));
        }
        finally
        {
            GasBenchmarkConfig.ChunkIndex = 0;
            GasBenchmarkConfig.ChunkTotal = 0;
        }
    }

    [Test]
    public void ApplyChunkFilter_Handles_Equals_Syntax()
    {
        if (s_applyChunkFilter is null)
            Assert.Ignore("ApplyChunkFilter method not found");

        try
        {
            string[] args = ["--chunk=3/10"];
            string[] result = (string[])s_applyChunkFilter.Invoke(null, [args]);

            Assert.That(GasBenchmarkConfig.ChunkIndex, Is.EqualTo(3));
            Assert.That(GasBenchmarkConfig.ChunkTotal, Is.EqualTo(10));
        }
        finally
        {
            GasBenchmarkConfig.ChunkIndex = 0;
            GasBenchmarkConfig.ChunkTotal = 0;
        }
    }

    [Test]
    public void ApplyChunkFilter_Throws_On_Invalid_Format()
    {
        if (s_applyChunkFilter is null)
            Assert.Ignore("ApplyChunkFilter method not found");

        string[] args = ["--chunk", "abc"];
        TargetInvocationException ex = Assert.Throws<TargetInvocationException>(
            () => s_applyChunkFilter.Invoke(null, [args]));
        Assert.That(ex.InnerException, Is.TypeOf<ArgumentException>());
        Assert.That(ex.InnerException.Message, Does.Contain("Invalid --chunk value"));
    }

    [Test]
    public void ApplyChunkFilter_Throws_When_N_Greater_Than_M()
    {
        if (s_applyChunkFilter is null)
            Assert.Ignore("ApplyChunkFilter method not found");

        string[] args = ["--chunk", "6/5"];
        TargetInvocationException ex = Assert.Throws<TargetInvocationException>(
            () => s_applyChunkFilter.Invoke(null, [args]));
        Assert.That(ex.InnerException, Is.TypeOf<ArgumentException>());
    }

    [Test]
    public void ApplyChunkFilter_Throws_When_N_Is_Zero()
    {
        if (s_applyChunkFilter is null)
            Assert.Ignore("ApplyChunkFilter method not found");

        string[] args = ["--chunk", "0/5"];
        TargetInvocationException ex = Assert.Throws<TargetInvocationException>(
            () => s_applyChunkFilter.Invoke(null, [args]));
        Assert.That(ex.InnerException, Is.TypeOf<ArgumentException>());
    }
}
