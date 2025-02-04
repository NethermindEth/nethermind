// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.CommandLine;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Ethereum.Test.Base;
using Ethereum.Test.Base.Interfaces;

namespace Nethermind.Test.Runner;

internal class Program
{
    public class Options
    {
        public static CliOption<string> Input { get; } =
            new("--input", "-i") { Description = "Set the state test input file or directory. Either 'input' or 'stdin' is required." };

        public static CliOption<string> Filter { get; } =
            new("--filter", "-f") { Description = "Set the test name that you want to run. Could also be a regular expression." };

        public static CliOption<bool> BlockTest { get; } =
            new("--blockTest", "-b") { Description = "Set test as blockTest. if not, it will be by default assumed a state test." };

        public static CliOption<bool> TraceAlways { get; } =
            new("--trace", "-t") { Description = "Set to always trace (by default traces are only generated for failing tests). [Only for State Test]" };

        public static CliOption<bool> TraceNever { get; } =
            new("--neverTrace", "-n") { Description = "Set to never trace (by default traces are only generated for failing tests). [Only for State Test]" };

        public static CliOption<bool> ExcludeMemory { get; } =
            new("--memory", "-m") { Description = "Exclude memory trace. [Only for State Test]" };

        public static CliOption<bool> ExcludeStack { get; } =
            new("--stack", "-s") { Description = "Exclude stack trace. [Only for State Test]" };

        public static CliOption<bool> Wait { get; } =
            new("--wait", "-w") { Description = "Wait for input after the test run." };

        public static CliOption<bool> Stdin { get; } =
            new("--stdin", "-x") { Description = "If stdin is used, the state runner will read inputs (filenames) from stdin, and continue executing until empty line is read." };

        public static CliOption<bool> GnosisTest { get; } =
            new("--gnosisTest", "-g") { Description = "Set test as gnosisTest. if not, it will be by default assumed a mainnet test." };
    }

    public static async Task<int> Main(params string[] args)
    {
        CliRootCommand rootCommand =
        [
            Options.Input,
            Options.Filter,
            Options.BlockTest,
            Options.TraceAlways,
            Options.TraceNever,
            Options.ExcludeMemory,
            Options.ExcludeStack,
            Options.Wait,
            Options.Stdin
        ];
        rootCommand.SetAction(Run);

        CliConfiguration configuration = new(rootCommand);

        return await configuration.InvokeAsync(args);
    }

    private static async Task<int> Run(ParseResult parseResult, CancellationToken cancellationToken)
    {
        WhenTrace whenTrace = WhenTrace.WhenFailing;

        if (parseResult.GetValue(Options.TraceNever))
            whenTrace = WhenTrace.Never;

        if (parseResult.GetValue(Options.TraceAlways))
            whenTrace = WhenTrace.Always;

        string input = parseResult.GetValue(Options.Input);

        if (parseResult.GetValue(Options.Stdin))
            input = Console.ReadLine();
        ulong chainId = parseResult.GetValue(Options.GnosisTest) ? 100ul : 1ul;


        while (!string.IsNullOrWhiteSpace(input))
        {
            if (parseResult.GetValue(Options.BlockTest))
                await RunBlockTest(input, source => new BlockchainTestsRunner(source, parseResult.GetValue(Options.Filter), chainId));
            else
                RunStateTest(input, source => new StateTestsRunner(source, whenTrace,
                    !parseResult.GetValue(Options.ExcludeMemory),
                    !parseResult.GetValue(Options.ExcludeStack),
                    chainId,
                    parseResult.GetValue(Options.Filter)));

            if (!parseResult.GetValue(Options.Stdin))
                break;

            input = Console.ReadLine();
        }

        if (parseResult.GetValue(Options.Wait))
            Console.ReadLine();

        return 0;
    }

    private static async Task RunBlockTest(string path, Func<ITestSourceLoader, IBlockchainTestRunner> testRunnerBuilder)
    {
        ITestSourceLoader source = Path.HasExtension(path)
            ? new TestsSourceLoader(new LoadBlockchainTestFileStrategy(), path)
            : new TestsSourceLoader(new LoadBlockchainTestsStrategy(), path);
        await testRunnerBuilder(source).RunTestsAsync();
    }

    private static void RunStateTest(string path, Func<ITestSourceLoader, IStateTestRunner> testRunnerBuilder)
    {
        ITestSourceLoader source = Path.HasExtension(path)
            ? new TestsSourceLoader(new LoadGeneralStateTestFileStrategy(), path)
            : new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), path);
        testRunnerBuilder(source).RunTests();
    }
}
