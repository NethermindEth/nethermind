// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Ethereum.Test.Base;
using NUnit.Framework;

namespace Ethereum.Basic.Test;

/// <summary>
/// A fixture file that cannot be loaded reaches a typed suite as one failing case that names the file, next to
/// the unchanged cases of the files that load.
/// </summary>
[NonParallelizable]
public class FixtureLoadTests : UnchunkedFixture
{
    private const int ChunkCount = 8;
    private const string ZeroHash = "0x0000000000000000000000000000000000000000000000000000000000000000";
    private const string TransitionDirectory = "LegacyTests/Cancun/BlockchainTests/TransitionTests/bcArrowGlacierToParis";
    private const string ValueOverflowDirectory = "LegacyTests/Cancun/GeneralStateTests/stTransactionTest";
    private const string TotalDifficultyNetwork = "ArrowGlacierToParisAtDiffC0000";
    private const string OverWideValue = "0x:bigint 0x10000000000000000000000000000000000000000000000000000000000000001";

    public enum Unreadable { Bom, MalformedJson, UnknownFork }

    public sealed record DirectoryStrategy(ITestLoadStrategy Strategy, TestType Type, bool IsLegacy)
    {
        public override string ToString() => Strategy.GetType().Name;
    }

    public sealed record QuarantinedFile(string RelativePath, TestType Type, string Reason)
    {
        public override string ToString() => Path.GetFileName(RelativePath);
    }

    private static readonly DirectoryStrategy[] DirectoryStrategies =
    [
        new(new LoadGeneralStateTestsStrategy(), TestType.State, IsLegacy: false),
        new(new LoadLegacyGeneralStateTestsStrategy(), TestType.State, IsLegacy: true),
        new(new LoadEipTestsStrategy(), TestType.State, IsLegacy: false),
        new(new LoadBlockchainTestsStrategy(), TestType.Blockchain, IsLegacy: false),
        new(new LoadLegacyBlockchainTestsStrategy(), TestType.Blockchain, IsLegacy: false),
    ];

    private static readonly QuarantinedFile[] QuarantinedFiles =
    [
        new($"{TransitionDirectory}/difficultyFormula.json", TestType.Blockchain, "fork transition at a total difficulty"),
        new($"{TransitionDirectory}/powToPosBlockRejection.json", TestType.Blockchain, "fork transition at a total difficulty"),
        new($"{TransitionDirectory}/powToPosTest.json", TestType.Blockchain, "fork transition at a total difficulty"),
        new($"{ValueOverflowDirectory}/ValueOverflow.json", TestType.State, "transaction value wider than 256 bits"),
        new($"{ValueOverflowDirectory}/ValueOverflowParis.json", TestType.State, "transaction value wider than 256 bits"),
    ];

    private string _directory;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_directory);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_directory, true);

    [Test]
    public void Directory_strategy_keeps_the_valid_cases_and_reports_the_unreadable_file(
        [ValueSource(nameof(DirectoryStrategies))] DirectoryStrategy strategy,
        [Values] Unreadable unreadable)
    {
        WriteFixture("valid.json", Fixture(strategy.Type, "Berlin"));
        (string json, bool bom, string expectedError) = UnreadableFixture(strategy.Type, unreadable);
        string unreadableFile = WriteFixture("unreadable.json", json, bom);

        TestsSourceLoader loader = new(strategy.Strategy, _directory);
        List<EthereumTest> tests = strategy.Type == TestType.State
            ? [.. loader.LoadTests<GeneralStateTest>()]
            : [.. loader.LoadTests<BlockchainTest>()];
        List<EthereumTest> valid = [.. tests.Where(static t => t.LoadFailure is null)];
        List<EthereumTest> failures = [.. tests.Where(static t => t.LoadFailure is not null)];

        Assert.That(failures, Has.Count.EqualTo(1), "one failing case per unreadable file");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(valid.Select(static t => t.ToString()), Is.EqualTo(ValidCaseNames(strategy.Type)));
            Assert.That(valid.OfType<GeneralStateTest>().Select(static t => t.IsLegacy), Is.All.EqualTo(strategy.IsLegacy));
            Assert.That(failures[0], Is.TypeOf(strategy.Type == TestType.State ? typeof(GeneralStateTest) : typeof(BlockchainTest)));
            Assert.That(failures[0].Name, Is.EqualTo(unreadableFile));
            Assert.That(failures[0].Category, Is.EqualTo(_directory));
            Assert.That(failures[0].LoadFailure, Does.Contain(expectedError));
        }
    }

    [Test]
    public void Raw_loads_keep_the_untyped_marker_and_the_single_file_strategy_its_typed_failure()
    {
        string file = WriteFixture("unreadable.json", "{\"broken\": ");
        TestsSourceLoader loader = new(new LoadGeneralStateTestsStrategy(), _directory);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(TypesOf(new FileTestsSource(file).LoadTests(TestType.State)), Is.EqualTo(new[] { typeof(FailedToLoadTest) }));
            Assert.That(TypesOf(loader.LoadTests()), Is.EqualTo(new[] { typeof(FailedToLoadTest) }));
            Assert.That(TypesOf(loader.LoadTests<EthereumTest>()), Is.EqualTo(new[] { typeof(FailedToLoadTest) }));
            Assert.That(TypesOf(new LoadGeneralStateTestFileStrategy().Load(file)), Is.EqualTo(new[] { typeof(GeneralStateTest) }));
        }
    }

    [Test]
    public void LoadTests_throws_for_a_load_failure_the_requested_type_cannot_carry()
    {
        FailedToLoadTest failed = new() { Name = "/fixtures/broken.json", LoadFailure = "Failed to load: boom" };
        TestsSourceLoader loader = new(new FixedLoadStrategy([failed]), _directory);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => _ = loader.LoadTests<UnrelatedTest>().ToList());

        Assert.That(exception.Message, Does.Contain(failed.Name).And.Contain(failed.LoadFailure));
    }

    [Test]
    public void LoadTests_reads_the_source_lazily_and_lets_its_exception_through()
    {
        int read = 0;
        IEnumerable<GeneralStateTest> tests = new TestsSourceLoader(new FixedLoadStrategy(Source()), _directory).LoadTests<GeneralStateTest>();

        int readBeforeEnumeration = read;
        using IEnumerator<GeneralStateTest> enumerator = tests.GetEnumerator();
        bool hasFirst = enumerator.MoveNext();
        int readForFirst = read;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(readBeforeEnumeration, Is.Zero, "nothing is read before enumeration");
            Assert.That(hasFirst, Is.True);
            Assert.That(readForFirst, Is.EqualTo(1), "the source is read one test at a time");
            Assert.That(() => enumerator.MoveNext(), Throws.InvalidOperationException.With.Message.EqualTo("source failed"));
        }

        IEnumerable<EthereumTest> Source()
        {
            read++;
            yield return new GeneralStateTest { Name = "first" };
            read++;
            throw new InvalidOperationException("source failed");
        }
    }

    [Test]
    public void Chunks_split_the_cases_and_load_failures_by_their_typed_position()
    {
        EthereumTest[] source = [.. Enumerable.Range(0, 30).Select(MixedSourceItem)];
        string[] typed = [.. source.Where(static t => t is not UnrelatedTest).Select(static t => t.Name)];

        List<string> seen = [];
        for (int chunk = 1; chunk <= ChunkCount; chunk++)
        {
            Environment.SetEnvironmentVariable(ChunkVariable, $"{chunk}of{ChunkCount}");
            string[] inChunk = [.. new TestsSourceLoader(new FixedLoadStrategy(source), _directory).LoadTests<GeneralStateTest>().Select(static t => t.Name)];

            Assert.That(inChunk, Is.EqualTo(typed.Where((_, i) => i % ChunkCount == chunk - 1)), $"chunk {chunk}of{ChunkCount}");
            seen.AddRange(inChunk);
        }

        Assert.That(seen, Is.EquivalentTo(typed), "every case and load failure falls in exactly one chunk");
    }

    [Test]
    public async Task BlockchainTestBase_fails_a_load_failure_before_reading_the_network([Values] bool failOnInvalidRlp)
    {
        BlockchainTest test = new() { Name = "/fixtures/broken.json", LoadFailure = "Failed to load: boom" };

        await AssertFailsWithLoadFailure(() => new BlockchainRunner().Run(test, failOnInvalidRlp), test);
    }

    [Test]
    public async Task GeneralStateTestBase_fails_a_load_failure_naming_the_file()
    {
        GeneralStateTest test = new() { Name = "/fixtures/broken.json", LoadFailure = "Failed to load: boom" };

        await AssertFailsWithLoadFailure(() => new StateRunner().Run(test), test);
    }

    [Test]
    public async Task Quarantined_file_failing_with_its_listed_error_is_skipped_with_the_reason_and_the_error(
        [ValueSource(nameof(QuarantinedFiles))] QuarantinedFile quarantined,
        [Values] bool windowsSeparators)
    {
        EthereumTest test = LoadSingle(WriteFixture(quarantined.RelativePath, QuarantineFixture(quarantined.RelativePath, quarantined.Type)), quarantined.Type);
        if (windowsSeparators)
            test.Name = test.Name.Replace('/', '\\');

        IgnoreException exception = await Assert.ThrowsAsync<IgnoreException>(RunInBase(test));

        Assert.That(exception.Message, Does.Contain(quarantined.Reason).And.Contain(test.Name).And.Contain(test.LoadFailure));
    }

    private static IEnumerable<TestCaseData> LoadFailuresOutsideTheQuarantine()
    {
        string unlisted = $"{TransitionDirectory}/unlisted.json";
        yield return new TestCaseData(unlisted, TestType.Blockchain, QuarantineFixture(unlisted, TestType.Blockchain))
            .SetArgDisplayNames("unlisted file next to listed ones");
        yield return new TestCaseData($"Not{TransitionDirectory}/difficultyFormula.json", TestType.Blockchain, QuarantineFixture(unlisted, TestType.Blockchain))
            .SetArgDisplayNames("listed name under another directory");
        yield return new TestCaseData($"{TransitionDirectory}/difficultyFormula.json", TestType.Blockchain, "{\"broken\": ")
            .SetArgDisplayNames("listed blockchain file with another error");
        yield return new TestCaseData($"{ValueOverflowDirectory}/Unlisted.json", TestType.State, QuarantineFixture($"{ValueOverflowDirectory}/ValueOverflow.json", TestType.State))
            .SetArgDisplayNames("unlisted state file with a listed error");
        yield return new TestCaseData($"{ValueOverflowDirectory}/ValueOverflow.json", TestType.State, "{\"broken\": ")
            .SetArgDisplayNames("listed state file with another error");
    }

    [TestCaseSource(nameof(LoadFailuresOutsideTheQuarantine))]
    public async Task Load_failure_outside_the_quarantine_still_fails(string relativePath, TestType type, string json)
    {
        EthereumTest test = LoadSingle(WriteFixture(relativePath, json), type);

        await AssertFailsWithLoadFailure(RunInBase(test), test);
    }

    private static async Task AssertFailsWithLoadFailure(Func<Task> run, EthereumTest test)
    {
        ResultStateException exception = await Assert.CatchAsync<ResultStateException>(run);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception, Is.Not.InstanceOf<IgnoreException>(), "a load failure outside the quarantine is not skipped");
            Assert.That(exception.Message, Does.Contain(test.Name).And.Contain(test.LoadFailure));
        }
    }

    private static Func<Task> RunInBase(EthereumTest test) => test switch
    {
        BlockchainTest blockchainTest => () => new BlockchainRunner().Run(blockchainTest, failOnInvalidRlp: true),
        GeneralStateTest stateTest => () => new StateRunner().Run(stateTest),
        _ => throw new ArgumentException($"No test base runs {test.GetType().Name}", nameof(test)),
    };

    private static EthereumTest LoadSingle(string file, TestType type)
    {
        TestsSourceLoader loader = new(type == TestType.State ? new LoadLegacyGeneralStateTestsStrategy() : new LoadLegacyBlockchainTestsStrategy(),
            Path.GetDirectoryName(file));
        return type == TestType.State ? loader.LoadTests<GeneralStateTest>().Single() : loader.LoadTests<BlockchainTest>().Single();
    }

    private static Type[] TypesOf(IEnumerable<EthereumTest> tests) => [.. tests.Select(static t => t.GetType())];

    private static EthereumTest MixedSourceItem(int index) => (index % 5) switch
    {
        1 => new FailedToLoadTest { Name = $"broken{index}.json", LoadFailure = "Failed to load: boom" },
        3 => new UnrelatedTest { Name = $"unrelated{index}" },
        _ => new GeneralStateTest { Name = $"case{index}" },
    };

    private string WriteFixture(string relativePath, string json, bool bom = false)
    {
        string file = Path.Combine(_directory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(file));
        File.WriteAllText(file, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: bom));
        return file;
    }

    private static (string Json, bool Bom, string Error) UnreadableFixture(TestType type, Unreadable unreadable) => unreadable switch
    {
        Unreadable.Bom => (Fixture(type, "Berlin"), true, "'0xEF' is an invalid start of a value"),
        Unreadable.MalformedJson => ("{\"broken\": ", false, "JsonException"),
        _ => (Fixture(type, "UnknownFork"), false, "Unknown fork name 'UnknownFork'"),
    };

    /// <summary>A fixture that fails to load the way the quarantined file at <paramref name="relativePath"/> does.</summary>
    private static string QuarantineFixture(string relativePath, TestType type) => type == TestType.State
        ? Fixture(type, "Berlin", Path.GetFileNameWithoutExtension(relativePath), OverWideValue)
        : Fixture(type, TotalDifficultyNetwork);

    private string[] ValidCaseNames(TestType type)
    {
        string category = Path.GetFileName(_directory);
        return type == TestType.State
            ? [$"{category}.validTest_d0g0v0__Berlin", $"{category}.validTest_d0g0v0__London"]
            : ["firstTest", "secondTest"];
    }

    private static string Fixture(TestType type, string firstFork, string name = "validTest", string value = "0x01") => type == TestType.State
        ? $$"""
            {
              "{{name}}": {
                "env": {
                  "currentCoinbase": "0x2adc25665018aa1fe0e6bc666dac8fc2697ff9ba",
                  "currentDifficulty": "0x20000",
                  "currentGasLimit": "0x0f4240",
                  "currentNumber": "0x01",
                  "currentTimestamp": "0x03e8",
                  "previousHash": "{{ZeroHash}}"
                },
                "pre": {},
                "transaction": {
                  "nonce": "0x00",
                  "gasPrice": "0x0a",
                  "gasLimit": ["0x5208"],
                  "to": "0x1000000000000000000000000000000000000000",
                  "value": ["{{value}}"],
                  "data": ["0x"],
                  "secretKey": "0x45a915e4d060149eb4365960e6a7a45f334393093061116b197e3240065ff2d8"
                },
                "post": {
                  "{{firstFork}}": [{ "hash": "{{ZeroHash}}", "logs": "{{ZeroHash}}", "indexes": { "data": 0, "gas": 0, "value": 0 } }],
                  "London": [{ "hash": "{{ZeroHash}}", "logs": "{{ZeroHash}}", "indexes": { "data": 0, "gas": 0, "value": 0 } }]
                }
              }
            }
            """
        : $$"""
            {
              "firstTest": { "network": "{{firstFork}}", "lastblockhash": "{{ZeroHash}}", "pre": {} },
              "secondTest": { "network": "London", "lastblockhash": "{{ZeroHash}}", "pre": {} }
            }
            """;

    private sealed class BlockchainRunner : BlockchainTestBase
    {
        public Task<EthereumTestResult> Run(BlockchainTest test, bool failOnInvalidRlp) => RunTest(test, failOnInvalidRlp: failOnInvalidRlp);
    }

    private sealed class StateRunner : GeneralStateTestBase
    {
        public Task<EthereumTestResult> Run(GeneralStateTest test) => RunTest(test);
    }
}

/// <summary>A file that failed to load becomes a <typeparamref name="T"/> carrying the file's metadata and error.</summary>
[TestFixture(typeof(GeneralStateTest))]
[TestFixture(typeof(BlockchainTest))]
[TestFixture(typeof(TransactionTest))]
[NonParallelizable]
public class TypedLoadFailureTests<T> : UnchunkedFixture where T : EthereumTest, new()
{
    [Test]
    public void LoadTests_passes_tests_of_the_type_through_and_restates_a_load_failure_as_one()
    {
        T loaded = new() { Name = "loaded" };
        T typedFailure = new() { Name = "/fixtures/typed.json", LoadFailure = "Failed to load: typed" };
        FailedToLoadTest failed = new() { Name = "/fixtures/broken.json", Category = "/fixtures", ChainId = 100, LoadFailure = "Failed to load: boom" };

        List<T> tests = [.. new TestsSourceLoader(new FixedLoadStrategy([loaded, new UnrelatedTest { Name = "unrelated" }, typedFailure, failed]), "/fixtures").LoadTests<T>()];

        Assert.That(tests, Has.Count.EqualTo(3), "a test of another type is left out");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(tests[0], Is.SameAs(loaded));
            Assert.That(tests[1], Is.SameAs(typedFailure));
            Assert.That(tests[2], Is.TypeOf<T>());
            Assert.That(tests[2].Name, Is.EqualTo(failed.Name));
            Assert.That(tests[2].Category, Is.EqualTo(failed.Category));
            Assert.That(tests[2].ChainId, Is.EqualTo(failed.ChainId));
            Assert.That(tests[2].LoadFailure, Is.EqualTo(failed.LoadFailure));
        }
    }
}

/// <summary>Runs each test with <c>TEST_CHUNK</c> unset, since the tests read whole, unchunked sequences.</summary>
/// <remarks>Derived fixtures are <see cref="NonParallelizableAttribute"/>, so no other test reads the variable meanwhile.</remarks>
public abstract class UnchunkedFixture
{
    protected const string ChunkVariable = "TEST_CHUNK";

    private string _testChunk;

    [SetUp]
    public void UnsetTestChunk()
    {
        _testChunk = Environment.GetEnvironmentVariable(ChunkVariable);
        Environment.SetEnvironmentVariable(ChunkVariable, null);
    }

    [TearDown]
    public void RestoreTestChunk() => Environment.SetEnvironmentVariable(ChunkVariable, _testChunk);
}

file sealed class FixedLoadStrategy(IEnumerable<EthereumTest> tests) : ITestLoadStrategy
{
    public IEnumerable<EthereumTest> Load(string testDirectoryName, string wildcard = null) => tests;
}

file sealed class UnrelatedTest : EthereumTest;
