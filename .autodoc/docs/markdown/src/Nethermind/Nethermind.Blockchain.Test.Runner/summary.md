[View code on GitHub](https://github.com/nethermindeth/nethermind/son/src/Nethermind/Nethermind.Blockchain.Test.Runner)

The `Nethermind.Blockchain.Test.Runner` folder contains code related to testing the blockchain implementation in the Nethermind project. Specifically, it contains test runners for blockchain tests and state tests, as well as a configuration file for the NLog logging library.

The `BlockchainTestsBugHunter.cs` file is a test runner for blockchain tests that implements the `IBlockchainTestRunner` interface. It takes an `ITestSourceLoader` object as a constructor parameter, which is used to load the tests. The `RunTestsAsync` method is the main entry point for running the tests. It loads the tests using the `_testsSource` object and iterates over them, setting up the test environment, running the test, and reporting the result. The class also writes test failures to a log file on the user's desktop.

The `PerfTest.cs` file is a test runner for state tests that implements the `IStateTestRunner` interface. It loads the tests from a source, runs each test, and measures its performance using a `Stopwatch` object. The `RunTests()` method returns an `IEnumerable<EthereumTestResult>` object, which contains the results of each test.

The `StateTestsBugHunter.cs` file is a test runner for Ethereum state tests that implements the `IStateTestRunner` interface. It loads the tests from a source, runs each test, and reports the results to the console. The class also writes test failures to a log file.

The `NLog.config` file is an XML configuration file for the NLog logging library. It sets up two logging targets: a file target and a colored console target. It also specifies which loggers should write to which targets and at what log levels.

These test runners and logging configuration file are important parts of the Nethermind project as they allow developers to test the blockchain and EVM implementations and ensure that they are compliant with the Ethereum specification. They can be used in the project's continuous integration (CI) pipeline to automatically run tests and detect any regressions. They can also be used by developers to manually run tests and debug any issues that arise during testing.

Here is an example of how to use the `PerfTest` class:

```csharp
ITestSourceLoader testsSource = new MyTestSourceLoader();
PerfTest testRunner = new PerfTest(testsSource);
IEnumerable<EthereumTestResult> results = testRunner.RunTests();
foreach (EthereumTestResult result in results)
{
    Console.WriteLine($"{result.Name}: {result.Pass} ({result.ExecutionTime} ms)");
}
```

In this example, `MyTestSourceLoader` is a custom implementation of the `ITestSourceLoader` interface that loads the state tests from a specific source. The `RunTests()` method returns a list of `EthereumTestResult` objects, which contain the name of the test, whether it passed or failed, and its execution time. The example code iterates over the results and writes them to the console, including the execution time.
