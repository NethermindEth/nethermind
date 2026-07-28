// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Ethereum.Test.Base;
using Evm.T8n;
using Evm.T8n.JsonTypes;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using NUnit.Framework;

namespace Evm.Test;

[TestFixture]
public class T8nExecutorTests
{
    private const ulong AllocatedBalance = 1_000_000_000_000_000_000; // 1 ETH

    private string _inputDirectory = null!;

    [SetUp]
    public void SetUp()
    {
        _inputDirectory = Path.Combine(Path.GetTempPath(), $"t8n-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_inputDirectory);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_inputDirectory, recursive: true);

    [Test]
    public void Execute_applies_non_empty_alloc()
    {
        T8nCommandArguments arguments = WriteInputs();

        T8nExecutionResult result = T8nExecutor.Execute(arguments);

        Assert.That(result.PostState.StateRoot, Is.Not.EqualTo(Keccak.EmptyTreeHash));
        Assert.That(result.Accounts.TryGetValue(TestItem.AddressA, out AccountState? account), Is.True);
        Assert.That(account!.Balance, Is.EqualTo((UInt256)AllocatedBalance));
    }

    private T8nCommandArguments WriteInputs()
    {
        Write("alloc.json", $$"""
            {
                "{{TestItem.AddressA}}": { "balance": "0x{{AllocatedBalance:x}}" }
            }
            """);
        Write("env.json", $$"""
            {
                "currentCoinbase": "{{TestItem.AddressB}}",
                "currentGasLimit": "0x1c9c380",
                "currentNumber": "0x1",
                "currentTimestamp": "0x0c",
                "currentDifficulty": "0x0",
                "currentBaseFee": "0x0",
                "currentRandom": "0x0",
                "withdrawals": []
            }
            """);
        Write("txs.json", "[]");

        return new T8nCommandArguments
        {
            InputAlloc = Path.Combine(_inputDirectory, "alloc.json"),
            InputEnv = Path.Combine(_inputDirectory, "env.json"),
            InputTxs = Path.Combine(_inputDirectory, "txs.json"),
            StateFork = "Shanghai",
            StateChainId = 1,
        };
    }

    private void Write(string fileName, string content) =>
        File.WriteAllText(Path.Combine(_inputDirectory, fileName), content);
}
