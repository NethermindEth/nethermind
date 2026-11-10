// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.CommandLine;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Evm.T8n;
using Evm.T8n.Errors;
using NUnit.Framework;

namespace Evm.Test;

/// <summary>
/// Runs the <c>t8n</c> command against the go-ethereum <c>cmd/evm</c> fixtures and expects the same output and exit code as geth.
/// </summary>
/// <remarks>
/// Mirrors <c>TestT8n</c> in go-ethereum's <c>cmd/evm/t8n_test.go</c>; the fixtures under <c>testdata</c> are copied verbatim.
/// </remarks>
[TestFixture]
public class T8nTests
{
    private static IEnumerable<TestCaseData> GethCases()
    {
        yield return Case("1", "Frontier+1346", T8nErrorCodes.ErrorConfig, name: "unsupported fork");
        yield return Case("1", "Byzantium", expected: "exp.json", name: "nonce too low rejection");
        yield return Case("3", "Berlin", expected: "exp.json", name: "blockhash");
        yield return Case("4", "Berlin", T8nErrorCodes.ErrorMissingBlockhash, name: "missing blockhash");
        yield return Case("5", "Byzantium", expected: "exp.json", reward: "0x80", name: "ommer rewards");
        yield return Case("13", "London", expected: "exp.json", output: Output.Body, name: "sign json transactions");
        yield return Case("13", "London", expected: "exp2.json", txs: "signed_txs.rlp", output: Output.Result, name: "already signed rlp transactions");
        yield return Case("14", "London", expected: "exp.json", output: Output.Result, name: "difficulty without uncles");
        yield return Case("14", "London", expected: "exp2.json", env: "env.uncles.json", output: Output.Result, name: "difficulty with uncles");
        yield return Case("14", "Berlin", expected: "exp_berlin.json", env: "env.uncles.json", output: Output.Result, name: "difficulty with uncles on Berlin");
        yield return Case("19", "London", expected: "exp_london.json", output: Output.Result, name: "difficulty on London");
        yield return Case("19", "ArrowGlacier", expected: "exp_arrowglacier.json", output: Output.Result, name: "difficulty on ArrowGlacier");
        yield return Case("19", "GrayGlacier", expected: "exp_grayglacier.json", output: Output.Result, name: "difficulty on GrayGlacier");
        yield return Case("23", "Berlin", expected: "exp.json", output: Output.Result, name: "sign unprotected transaction");
        yield return Case("24", "Paris", expected: "exp.json", name: "post-merge transition");
        yield return Case("24", "Paris", T8nErrorCodes.ErrorConfig, env: "env-missingrandom.json", output: Output.None, name: "post-merge without random");
        yield return Case("25", "Paris", expected: "exp.json", name: "base fee calculation");
        yield return Case("26", "Shanghai", expected: "exp.json", name: "withdrawals");
        yield return Case("28", "Cancun", expected: "exp.json", txs: "txs.rlp", name: "blob transactions");
        yield return Case("29", "Cancun", expected: "exp.json", name: "beacon root");
        yield return Case("30", "Cancun", expected: "exp.json", txs: "txs_more.rlp", name: "undecodable rlp transaction");
        yield return Case("33", "Prague", expected: "exp.json", name: "set code transaction");
        yield return Case("34", "Osaka", expected: "exp.json", name: "blob base fee reserve price");
        yield return Case("35", "Osaka", T8nErrorCodes.ErrorConfig, output: Output.None, name: "missing base fee");
    }

    [TestCaseSource(nameof(GethCases))]
    public void Matches_geth_output(string[] args, int expectedExitCode, string? expectedOutputFile)
    {
        (int exitCode, string output, string error) = Run(args);

        Assert.That(exitCode, Is.EqualTo(expectedExitCode), error);

        if (expectedOutputFile is not null)
        {
            AssertJsonEqual(output, expectedOutputFile);
        }
    }

    /// <remarks>Mirrors the <c>--trace</c> case of <c>TestEVMTracing</c> in go-ethereum's <c>cmd/evm/t8n_test.go</c>.</remarks>
    [Test]
    public void Writes_geth_traces_to_base_directory()
    {
        string baseDir = Path.Combine(AppContext.BaseDirectory, "testdata", "31");
        string outputDir = Path.Combine(Path.GetTempPath(), $"t8n-{Guid.NewGuid():N}");
        try
        {
            (int exitCode, _, string error) = Run(
            [
                "t8n",
                $"--input.alloc={Path.Combine(baseDir, "alloc.json")}",
                $"--input.txs={Path.Combine(baseDir, "txs.json")}",
                $"--input.env={Path.Combine(baseDir, "env.json")}",
                "--state.fork=Cancun",
                "--trace",
                "--output.basedir", outputDir,
            ]);

            Assert.That(exitCode, Is.Zero, error);
            foreach (string expectedTrace in Directory.GetFiles(baseDir, "trace-*.jsonl"))
            {
                Assert.That(File.ReadAllLines(Path.Combine(outputDir, Path.GetFileName(expectedTrace))), Is.EqualTo(File.ReadAllLines(expectedTrace)));
            }
        }
        finally
        {
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        }
    }

    [Test]
    public void Accepts_dash_separated_option_names()
    {
        TestCaseData withdrawals = Case("26", "Shanghai", expected: "exp.json");
        string[] args = [.. ((string[])withdrawals.Arguments[0]!).Select(arg => arg.StartsWith("--") ? arg.Replace('.', '-') : arg)];

        (int exitCode, string output, string error) = Run(args);

        Assert.That(exitCode, Is.Zero, error);
        AssertJsonEqual(output, (string)withdrawals.Arguments[2]!);
    }

    [Test]
    public void Reads_inputs_from_stdin()
    {
        string baseDir = Path.Combine(AppContext.BaseDirectory, "testdata", "13");
        JsonObject input = new()
        {
            ["alloc"] = JsonNode.Parse(File.ReadAllText(Path.Combine(baseDir, "alloc.json"))),
            ["env"] = JsonNode.Parse(File.ReadAllText(Path.Combine(baseDir, "env.json"))),
            ["txs"] = JsonNode.Parse(File.ReadAllText(Path.Combine(baseDir, "txs.json"))),
        };
        ProcessStartInfo startInfo = new("dotnet")
        {
            ArgumentList =
            {
                Path.Combine(AppContext.BaseDirectory, "Evm.dll"), "t8n",
                "--input.alloc=stdin", "--input.env=stdin", "--input.txs=stdin", "--state.fork=London",
                "--output.alloc", "", "--output.result", "", "--output.body=stdout",
            },
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using Process process = Process.Start(startInfo)!;
        process.StandardInput.Write(input.ToJsonString());
        process.StandardInput.Close();
        Task<string> error = process.StandardError.ReadToEndAsync();
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        Assert.That(process.ExitCode, Is.Zero, error.Result);
        AssertJsonEqual(output, Path.Combine(baseDir, "exp.json"));
    }

    private static void AssertJsonEqual(string actualJson, string expectedFile)
    {
        JsonNode? expected = JsonNode.Parse(File.ReadAllText(expectedFile));
        JsonNode? actual = JsonNode.Parse(actualJson);
        Assert.That(JsonNode.DeepEquals(actual, expected), Is.True, $"have:{Environment.NewLine}{actualJson}{Environment.NewLine}want:{Environment.NewLine}{expected}");
    }

    private static (int ExitCode, string Output, string Error) Run(string[] args)
    {
        RootCommand rootCommand = [];
        T8nCommand.Configure(ref rootCommand);

        using StringWriter output = new();
        using StringWriter error = new();
        int exitCode = rootCommand.Parse(args).Invoke(new InvocationConfiguration { Output = output, Error = error });
        return (exitCode, output.ToString(), error.ToString());
    }

    [Flags]
    private enum Output
    {
        None = 0,
        Alloc = 1,
        Result = 2,
        Body = 4,
    }

    private static TestCaseData Case(
        string directory,
        string fork,
        int expectedExitCode = 0,
        string? expected = null,
        string alloc = "alloc.json",
        string txs = "txs.json",
        string env = "env.json",
        string? reward = null,
        Output output = Output.Alloc | Output.Result,
        string name = "")
    {
        string baseDir = Path.Combine(AppContext.BaseDirectory, "testdata", directory);
        List<string> args =
        [
            "t8n",
            "--input.alloc", Path.Combine(baseDir, alloc),
            "--input.txs", Path.Combine(baseDir, txs),
            "--input.env", Path.Combine(baseDir, env),
            "--state.fork", fork,
            "--output.alloc", output.HasFlag(Output.Alloc) ? "stdout" : "",
            "--output.result", output.HasFlag(Output.Result) ? "stdout" : "",
            "--output.body", output.HasFlag(Output.Body) ? "stdout" : "",
        ];
        if (reward is not null) args.AddRange(["--state.reward", reward]);

        return new TestCaseData(args.ToArray(), expectedExitCode, expected is null ? null : Path.Combine(baseDir, expected))
            .SetName($"{nameof(Matches_geth_output)}({directory}, {fork}, {name})");
    }
}
