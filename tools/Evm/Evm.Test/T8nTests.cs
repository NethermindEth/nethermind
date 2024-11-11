// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Evm.T8n;
using Evm.T8n.JsonTypes;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Newtonsoft.Json.Linq;

namespace Evm.Test;

public class InputParams(string basedir, string alloc, string env, string txs, string stateFork, string? stateReward = null)
{
    public readonly string Alloc = Path.Combine(basedir, alloc);
    public readonly string Env = Path.Combine(basedir, env);
    public readonly string Txs = Path.Combine(basedir, txs);
    public readonly string StateFork = stateFork;
    public readonly string? StateReward = stateReward;
}

public class OutputParams(string? alloc = null, string? result = null, string? body = null)
{
    public readonly string? Alloc = alloc;
    public readonly string? Result = result;
    public readonly string? Body = body;
}

public class T8nTests
{
    private readonly EthereumJsonSerializer _ethereumJsonSerializer = new();

    [Test]
    public void Test1()
    {
        Execute(
            new InputParams("testdata/1/", "alloc.json", "env.json", "txs.json", "Frontier+1346"),
            new OutputParams(alloc: "stdout", result: "stdout"),
            expectedExitCode: 3);
    }

    [Test]
    public void Test2()
    {
        Execute(
            new InputParams("testdata/1/", "alloc.json", "env.json", "txs.json", "Byzantium"),
            new OutputParams(alloc: "stdout", result: "stdout"),
            expectedExitCode: 0,
            expectedOutputFile: "testdata/1/exp.json");
    }

    [Test]
    public void Test3()
    {
        Execute(
            new InputParams("testdata/3/", "alloc.json", "env.json", "txs.json", "Berlin"),
            new OutputParams(alloc: "stdout", result: "stdout"),
            expectedExitCode: 0,
            expectedOutputFile: "testdata/3/exp.json");
    }

    [Test]
    public void Test4()
    {
        Execute(
            new InputParams("testdata/4/", "alloc.json", "env.json", "txs.json", "Berlin"),
            new OutputParams(alloc: "stdout", result: "stdout"),
            expectedExitCode: 4);
    }

    [Test]
    public void Test5()
    {
        Execute(
            new("testdata/5/", "alloc.json", "env.json", "txs.json", "Byzantium", "0x80"),
            new OutputParams(alloc: "stdout", result: "stdout"),
            expectedExitCode: 0,
            expectedOutputFile: "testdata/5/exp.json");
    }

    [Test]
    public void Test6()
    {
        Execute(
            new InputParams("testdata/13/", "alloc.json", "env.json", "txs.json", "London"),
            new OutputParams(body: "stdout"),
            expectedExitCode: 0,
            expectedOutputFile: "testdata/13/exp.json");
    }

    [Test]
    public void Test7()
    {
        Execute(
            new InputParams("testdata/13/", "alloc.json", "env.json", "signed_txs.rlp", "London"),
            new OutputParams(result: "stdout"),
            expectedExitCode: 0,
            expectedOutputFile: "testdata/13/exp2.json");
    }

    [Test]
    public void Test8()
    {
        Execute(
            new InputParams("testdata/14/", "alloc.json", "env.json", "txs.json", "London"),
            new OutputParams(result: "stdout"),
            expectedExitCode: 0,
            expectedOutputFile: "testdata/14/exp.json");
    }

    [Test]
    public void Test9()
    {
        Execute(
            new InputParams("testdata/14/", "alloc.json", "env.uncles.json", "txs.json", "London"),
            new OutputParams(result: "stdout"),
            expectedExitCode: 0,
            expectedOutputFile: "testdata/14/exp2.json");
    }

    [Test]
    public void Test10()
    {
        Execute(
            new InputParams("testdata/14/", "alloc.json", "env.uncles.json", "txs.json", "Berlin"),
            new OutputParams(result: "stdout"),
            expectedExitCode: 0,
            expectedOutputFile: "testdata/14/exp_berlin.json");
    }

    [Test]
    public void Test11()
    {
        Execute(
            new InputParams("testdata/19/", "alloc.json", "env.json", "txs.json", "London"),
            new OutputParams(result: "stdout"),
            expectedExitCode: 0,
            expectedOutputFile: "testdata/19/exp_london.json");
    }

    [Test]
    public void Test12()
    {
        Execute(
            new InputParams("testdata/19/", "alloc.json", "env.json", "txs.json", "ArrowGlacier"),
            new OutputParams(result: "stdout"),
            expectedExitCode: 0,
            expectedOutputFile: "testdata/19/exp_arrowglacier.json");
    }

    [Test]
    public void Test13()
    {
        Execute(
            new InputParams("testdata/19/", "alloc.json", "env.json", "txs.json", "GrayGlacier"),
            new OutputParams(result: "stdout"),
            expectedExitCode: 0,
            expectedOutputFile: "testdata/19/exp_grayglacier.json");
    }

    [Test]
    public void Test14()
    {
        Execute(
            new InputParams("testdata/23/", "alloc.json", "env.json", "txs.json", "Berlin"),
            new OutputParams(result: "stdout"),
            expectedExitCode: 0,
            expectedOutputFile: "testdata/23/exp.json");
    }

    [Test]
    public void Test15()
    {
        Execute(
            new InputParams("testdata/24/", "alloc.json", "env.json", "txs.json", "Merge"),
            new OutputParams(result: "stdout", alloc: "stdout"),
            expectedExitCode: 0,
            expectedOutputFile: "testdata/24/exp.json");
    }

    [Test]
    public void Test16()
    {
        Execute(
            new InputParams("testdata/24/", "alloc.json", "env-missingrandom.json", "txs.json", "Merge"),
            new OutputParams(result: "stdout", alloc: "stdout"),
            expectedExitCode: 3);
    }

    [Test]
    public void Test17()
    {
        Execute(
            new InputParams("testdata/26/", "alloc.json", "env.json", "txs.json", "Shanghai"),
            new OutputParams(result: "stdout", alloc: "stdout"),
            expectedExitCode: 0,
            expectedOutputFile: "testdata/26/exp.json");
    }

    [Test]
    public void Test18()
    {
        Execute(
            new InputParams("testdata/28/", "alloc.json", "env.json", "txs.rlp", "Cancun"),
            new OutputParams(result: "stdout", alloc: "stdout"),
            expectedExitCode: 0,
            expectedOutputFile: "testdata/28/exp.json");
    }

    [Test]
    public void Test19()
    {
        Execute(
            new InputParams("testdata/29/", "alloc.json", "env.json", "txs.json", "Cancun"),
            new OutputParams(result: "stdout", alloc: "stdout"),
            expectedExitCode: 0,
            expectedOutputFile: "testdata/29/exp.json");
    }

    [Test]
    public void Test20()
    {
        Execute(
            new InputParams("testdata/30/", "alloc.json", "env.json", "txs_more.rlp", "Cancun"),
            new OutputParams(result: "stdout", alloc: "stdout"),
            expectedExitCode: 0,
            expectedOutputFile: "testdata/30/exp.json");
    }

    private void Execute(InputParams inputParams, OutputParams outputParams, int expectedExitCode, string? expectedOutputFile = null)
    {
        var arguments = new T8nCommandArguments
        {
            InputAlloc = inputParams.Alloc,
            InputEnv = inputParams.Env,
            InputTxs = inputParams.Txs,
            OutputBody = outputParams.Body,
            StateFork = inputParams.StateFork,
        };
        if (outputParams.Alloc is not null) arguments.OutputAlloc = outputParams.Alloc;
        if (outputParams.Result is not null) arguments.OutputResult = outputParams.Result;
        if (inputParams.StateReward is not null) arguments.StateReward = inputParams.StateReward;

        T8nOutput output = T8nTool.Run(arguments, NullLogManager.Instance);

        Assert.That(output.ExitCode, Is.EqualTo(expectedExitCode));

        if (expectedOutputFile == null) return;

        var outputString = _ethereumJsonSerializer.Serialize(output, true);
        var fileContent = File.ReadAllText(expectedOutputFile);
        Assert.That(AreEqual(fileContent, outputString));
    }

    private static bool AreEqual(string json1, string json2)
    {
        var expected = JToken.Parse(json1);
        var actual = JToken.Parse(json2);
        return JToken.DeepEquals(actual, expected);
    }
}
