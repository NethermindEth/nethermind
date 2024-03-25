// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;

namespace Evm.Test;
using T8NTool;

public class InputParams
{
    public readonly string Alloc;
    public readonly string Env;
    public readonly string Txs;
    public readonly string StateFork;
    public readonly string? StateReward;

    public InputParams(string basedir, string alloc, string env, string txs, string stateFork, string? stateReward = null)
    {
        Alloc = basedir + alloc;
        Env = basedir + env;
        Txs = basedir + txs;
        StateFork = stateFork;
        StateReward = stateReward;
    }
}

public class OutputParams
{
    public string? Alloc;
    public string? Result;
    public string? Body;
    
    public OutputParams(string? alloc = null, string? result = null, string? body = null)
    {
        Alloc = alloc;
        Result = result;
        Body = body;
    }
}

public class T8NTests
{
    private T8NTool _t8NTool;

    [SetUp]
    public void Setup()
    {
        _t8NTool = new T8NTool();
    }

    [Test]
    public void Test1()
    {
        Execute(
            new InputParams("testdata/1/","alloc.json", "env.json", "txs.json", "Frontier+1346"), 
            new OutputParams(alloc: "stdout", result: "stdout"), 3);
    }

    private void Execute(InputParams inputParams, OutputParams outputParams, int expectedExitCode, string? expectedOutputFile = null)
    {
        var output = _t8NTool.Execute(
            inputParams.Alloc,
            inputParams.Env,
            inputParams.Txs,
            null,
            outputParams.Alloc,
            outputParams.Body,
            outputParams.Result,
            1,
            inputParams.StateFork,
            inputParams.StateReward,
            false,
            false,
            false,
            false,
            false
        );

        Assert.That(output.ExitCode, Is.EqualTo(expectedExitCode));

        if (expectedOutputFile != null)
        {
            var outputString = JsonSerializer.Serialize(output);
            var fileContent = File.ReadAllText(expectedOutputFile);
            Assert.Equals(outputString, fileContent);
        }
    }
}
