// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Evm.JsonTypes;
using Nethermind.JsonRpc;
using Nethermind.Serialization.Json;
using Newtonsoft.Json.Linq;

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
    private readonly EthereumJsonSerializer _ethereumJsonSerializer = new();

    [SetUp]
    public void Setup()
    {
        _t8NTool = new T8NTool();
    }

    [Test]
    public void TestExitOnBadConfig()
    {
        Execute(
            new InputParams("testdata/1/", "alloc.json", "env.json", "txs.json", "Frontier+1346"),
            new OutputParams(alloc: "stdout", result: "stdout"),
            expectedExitCode: 3
        );
    }

    [Test]
    public void BaselineTest()
    {
        Execute(
            new InputParams("testdata/1/", "alloc.json", "env.json", "txs.json", "Byzantium"),
            new OutputParams(alloc: "stdout", result: "stdout"),
            expectedExitCode: 0,
             "testdata/1/exp.json"
        );
    }

[Test]
public void BlockhashTest()
{
    Execute(
        new InputParams("testdata/3/", "alloc.json", "env.json", "txs.json", "Berlin"),
        new OutputParams(alloc: "stdout", result: "stdout"),
        expectedExitCode: 0,
        expectedOutputFile: "testdata/3/exp.json"
    );
}

[Test]
public void MissingBlockhashTest()
{
    Execute(
        new InputParams("testdata/4/", "alloc.json", "env.json", "txs.json", "Berlin"),
        new OutputParams(alloc: "stdout", result: "stdout"),
        expectedExitCode: 4
    );
}

[Test]
public void UncleTest()
{
    Execute(
        new InputParams("testdata/5/", "alloc.json", "env.json", "txs.json", "Byzantium", "0x80"),
        new OutputParams(alloc: "stdout", result: "stdout"),
        expectedExitCode: 0,
        expectedOutputFile: "testdata/5/exp.json"
    );
}

[Test]
public void SignJsonTransactionsTest()
{
    Execute(
        new InputParams("testdata/13/", "alloc.json", "env.json", "txs.json", "London"),
        new OutputParams(body: "stdout"),
        expectedExitCode: 0,
        expectedOutputFile: "testdata/13/exp.json"
    );
}

[Test]
public void AlreadySignedTransactionsTest()
{
    Execute(
        new InputParams("testdata/13/", "alloc.json", "env.json", "signed_txs.rlp", "London"),
        new OutputParams(result: "stdout"),
        expectedExitCode: 0,
        expectedOutputFile: "testdata/13/exp2.json"
    );
}

[Test]
public void DifficultyCalculationNoUnclesTest()
{
    Execute(
        new InputParams("testdata/14/", "alloc.json", "env.json", "txs.json", "London"),
        new OutputParams(result: "stdout"),
        expectedExitCode: 0,
        expectedOutputFile: "testdata/14/exp.json"
    );
}

[Test]
public void DifficultyCalculationWithUnclesTest()
{
    Execute(
        new InputParams("testdata/14/", "alloc.json", "env.uncles.json", "txs.json", "London"),
        new OutputParams(result: "stdout"),
        expectedExitCode: 0,
        expectedOutputFile: "testdata/14/exp2.json"
    );
}

[Test]
public void DifficultyCalculationWithOmmersBerlinTest()
{
    Execute(
        new InputParams("testdata/14/", "alloc.json", "env.uncles.json", "txs.json", "Berlin"),
        new OutputParams(result: "stdout"),
        expectedExitCode: 0,
        expectedOutputFile: "testdata/14/exp_berlin.json"
    );
}

[Test]
public void DifficultyCalculationOnLondonTest()
{
    Execute(
        new InputParams("testdata/19/", "alloc.json", "env.json", "txs.json", "London"),
        new OutputParams(result: "stdout"),
        expectedExitCode: 0,
        expectedOutputFile: "testdata/19/exp_london.json"
    );
}

[Test]
public void DifficultyCalculationOnArrowGlacierTest()
{
    Execute(
        new InputParams("testdata/19/", "alloc.json", "env.json", "txs.json", "ArrowGlacier"),
        new OutputParams(result: "stdout"),
        expectedExitCode: 0,
        expectedOutputFile: "testdata/19/exp_arrowglacier.json"
    );
}

[Test]
public void DifficultyCalculationOnGrayGlacierTest()
{
    Execute(
        new InputParams("testdata/19/", "alloc.json", "env.json", "txs.json", "GrayGlacier"),
        new OutputParams(result: "stdout"),
        expectedExitCode: 0,
        expectedOutputFile: "testdata/19/exp_grayglacier.json"
    );
}

[Test]
public void SignUnprotectedPreEIP155TransactionTest()
{
    Execute(
        new InputParams("testdata/23/", "alloc.json", "env.json", "txs.json", "Berlin"),
        new OutputParams(result: "stdout"),
        expectedExitCode: 0,
        expectedOutputFile: "testdata/23/exp.json"
    );
}

[Test]
public void TestPostMergeTransition()
{
    Execute(
        new InputParams("testdata/24/", "alloc.json", "env.json", "txs.json", "Merge"),
        new OutputParams(alloc: "stdout", result: "stdout"),
        expectedExitCode: 0,
        expectedOutputFile: "testdata/24/exp.json"
    );
}

[Test]
public void TestPostMergeTransitionWithMissingRandom()
{
    Execute(
        new InputParams("testdata/24/", "alloc.json", "env-missingrandom.json", "txs.json", "Merge"),
        new OutputParams(alloc: "stdout", result: "stdout"),
        expectedExitCode: 3
    );
}

[Test]
public void TestStateRewardNegativeOne()
{
    Execute(
        new InputParams("testdata/3/", "alloc.json", "env.json", "txs.json", "Berlin", "-1"),
        new OutputParams(alloc: "stdout", result: "stdout"),
        expectedExitCode: 0,
        expectedOutputFile: "testdata/3/exp.json"
    );
}

[Test]
public void ZeroTouchRewardPreEIP150NetworksNegativeOneTxsRlp()
{
    Execute(
        new InputParams("testdata/00-501/", "alloc.json", "env.json", "txs.rlp", "EIP150", "-1"),
        new OutputParams(alloc: "stdout", result: "stdout"),
        expectedExitCode: 0,
        expectedOutputFile: "testdata/00-501/exp.json"
    );
}

[Test]
public void ZeroTouchRewardPreEIP150NetworksTxsRlp()
{
    Execute(
        new InputParams("testdata/00-502/", "alloc.json", "env.json", "txs.rlp", "EIP150"),
        new OutputParams(alloc: "stdout", result: "stdout"),
        expectedExitCode: 0,
        expectedOutputFile: "testdata/00-502/exp.json"
    );
}

[Test]
public void ZeroTouchRewardPreEIP150NetworksTxsJson()
{
    Execute(
        new InputParams("testdata/00-502/", "alloc.json", "env.json", "txs.json", "EIP150"),
        new OutputParams(alloc: "stdout", result: "stdout"),
        expectedExitCode: 0,
        expectedOutputFile: "testdata/00-502/exp.json"
    );
}

[Test]
public void CalculateBaseFeeFromParentBaseFeeNegativeOne()
{
    Execute(
        new InputParams("testdata/00-503/", "alloc.json", "env.json", "txs.json", "London", "-1"),
        new OutputParams(alloc: "stdout", result: "stdout"),
        expectedExitCode: 3
    );
}

[Test]
public void CalculateBaseFeeFromParentBaseFee()
{
    Execute(
        new InputParams("testdata/00-504/", "alloc.json", "env.json", "txs.json", "London"),
        new OutputParams(alloc: "stdout", result: "stdout"),
        expectedExitCode: 0,
        expectedOutputFile: "testdata/00-504/exp.json"
    );
}

[Test]
public void BlockhashOpcodeNegativeOne()
{
    Execute(
        new InputParams("testdata/00-505/", "alloc.json", "env.json", "txs.json", "London", "-1"),
        new OutputParams(alloc: "stdout", result: "stdout"),
        expectedExitCode: 3
    );
}

[Test]
public void BlockhashOpcode()
{
    Execute(
        new InputParams("testdata/00-506/", "alloc.json", "env.json", "txs.json", "London"),
        new OutputParams(alloc: "stdout", result: "stdout"),
        expectedExitCode: 0,
        expectedOutputFile: "testdata/00-506/exp.json"
    );
}

[Test]
public void TestOpcode40Berlin()
{
    Execute(
        new InputParams("testdata/00-507/", "alloc.json", "env.json", "txs.json", "Berlin", "2000000000000000000"),
        new OutputParams(alloc: "stdout", result: "stdout"),
        expectedExitCode: 0,
        expectedOutputFile: "testdata/00-507/exp.json"
    );
}

[Test]
public void SuicideCoinbaseStateBerlin()
{
    Execute( // TODO: needs to be fixed
        new InputParams("testdata/00-508/", "alloc.json", "env.json", "txs.json", "Berlin", "2000000000000000000"),
        new OutputParams(alloc: "stdout", result: "stdout"),
        expectedExitCode: 0,
        expectedOutputFile: "testdata/00-508/exp.json"
    );
}

[Test]
public void BlockhashBounds()
{
    Execute(
        new InputParams("testdata/00-509/", "alloc.json", "env.json", "txs.json", "Berlin", "2000000000000000000"),
        new OutputParams(alloc: "stdout", result: "stdout"),
        expectedExitCode: 0,
        expectedOutputFile: "testdata/00-509/exp.json"
    );
}

[Test]
public void SuicidesMixingCoinbase()
{
    Execute(
        new InputParams("testdata/00-510/", "alloc.json", "env.json", "txs.json", "Berlin", "2000000000000000000"),
        new OutputParams(alloc: "stdout", result: "stdout"),
        expectedExitCode: 0,
        expectedOutputFile: "testdata/00-510/exp.json"
    );
}

[Test]
public void WithdrawalsTransition()
{
    Execute(
        new InputParams("testdata/00-511/", "alloc.json", "env.json", "txs.rlp", "Shanghai"),
        new OutputParams(alloc: "stdout", result: "stdout"),
        expectedExitCode: 3
    );
}

[Test]
public void TestWithdrawalsTransition()
{
    Execute(
        new InputParams("testdata/26/", "alloc.json", "env.json", "txs.json", "Shanghai"),
        new OutputParams(alloc: "stdout", result: "stdout"),
        expectedExitCode: 0,
        expectedOutputFile: "testdata/26/exp.json"
    );
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
            TraceOptions.Default
        );

        Assert.That(output.ExitCode, Is.EqualTo(expectedExitCode));

        if (expectedOutputFile == null) return;

        var outputString = _ethereumJsonSerializer.Serialize(output, true);
        var fileContent = File.ReadAllText(expectedOutputFile);
        Assert.That(AreEqual(fileContent, outputString));
    }

    private static bool AreEqual(string json1, string json2)
    {
        JToken expected = JToken.Parse(json1);
        JToken actual = JToken.Parse(json2);
        return JToken.DeepEquals(actual, expected);
    }
}
