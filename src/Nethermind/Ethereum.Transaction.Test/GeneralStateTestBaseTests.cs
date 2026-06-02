// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Ethereum.Test.Base;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Ethereum.Transaction.Test;

[TestFixture]
[Parallelizable(ParallelScope.Self)]
public class GeneralStateTestBaseTests : GeneralStateTestBase
{
    [Test]
    public void Amsterdam_state_test_without_env_slot_number_defaults_to_zero()
    {
        Address contract = new("0x0000000000000000000000707690000000008024");
        using PrivateKey senderKey = new("0x45a915e4d060149eb4365960e6a7a45f334393093061116b197e3240065ff2d8");
        Nethermind.Core.Transaction transaction = Build.A.Transaction
            .WithChainId(1)
            .WithGasPrice(0x10)
            .WithGasLimit(0x100000)
            .WithNonce(UInt256.Zero)
            .To(contract)
            .WithValue(0)
            .SignedAndResolved(senderKey)
            .TestObject;

        GeneralStateTest test = new()
        {
            Name = nameof(Amsterdam_state_test_without_env_slot_number_defaults_to_zero),
            Category = "state",
            Fork = Amsterdam.Instance,
            ForkName = Amsterdam.Instance.Name,
            CurrentCoinbase = new Address("0xb94f5374fce5edbc8e2a8697c15331677e6ebf0b"),
            CurrentDifficulty = new UInt256(0x200000),
            CurrentGasLimit = 0x26e1f476fe1e22,
            CurrentNumber = 1,
            CurrentTimestamp = 1000,
            CurrentBaseFee = 0x10,
            CurrentRandom = new Hash256("0x0000000000000000000000000000000000000000000000000000000000200000"),
            PreviousHash = new Hash256("0x044852b2a670ade5407e78fb2863c51de9fcb96542a07186fe3aeda6bb8a116d"),
            Pre = new()
            {
                [contract] = new()
                {
                    Code = Bytes.FromHexString("0x4b600055"),
                    Balance = 1_000_000_000,
                },
                [senderKey.Address] = new()
                {
                    Balance = UInt256.Parse("0xffffffffff"),
                }
            },
            PostHash = new Hash256("0x7b8e9fcbf409db592f7263787cb6440e5a0b534efd3dd92e9b287dda0a84c080"),
            Transaction = transaction,
        };

        EthereumTestResult result = RunTest(test);

        Assert.That(result.Pass, Is.True);
        Assert.That(result.StateRoot, Is.EqualTo(test.PostHash));
    }
}
