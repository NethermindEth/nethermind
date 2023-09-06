// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State;
using NUnit.Framework;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Blockchain;

namespace Nethermind.Evm.Test;

public class Eip4788Tests : TestBlockchain
{
    private ISpecProvider specProvider = new TestSpecProvider(Cancun.Instance);
    protected static IEnumerable<(IReleaseSpec Spec, bool ShouldFail)> BeaconBlockRootGetPayloadV3ForDifferentSpecTestSource()
    {
        yield return (Shanghai.Instance, true);
        yield return (Cancun.Instance, false);
    }

    [TestCaseSource(nameof(BeaconBlockRootGetPayloadV3ForDifferentSpecTestSource))]
    public void BeaconBlockRoot_Is_Stored_Correctly_and_Only_Valid_PostCancun((IReleaseSpec Spec, bool ShouldFail) testCase)
    {
        // empty placeholder for tests 
    }

    Block CreateBlock(IWorldState testState, IReleaseSpec spec)
    {
        Keccak parentBeaconBlockRoot = TestItem.KeccakG;

        byte[] bytecode = Prepare
            .EvmCode
            .TIMESTAMP()
            .MSTORE(0)
            .CALL(100.Ether(), Address.FromNumber(0x0B), 0, 0, 32, 32, 32)
            .MLOAD(32)
            .EQ(new UInt256(parentBeaconBlockRoot.Bytes, true))
            .JUMPI(0x57)
            .INVALID()
            .JUMPDEST()
            .STOP()
            .Done;

        testState.InsertCode(TestBlockchain.AccountA, bytecode, specProvider.GenesisSpec);
        Transaction tx = Core.Test.Builders.Build.A.Transaction
            .WithGasLimit(1_000_000)
            .WithGasPrice(1)
            .WithValue(1)
            .WithSenderAddress(TestBlockchain.AccountB)
            .WithNonce(testState.GetNonce(TestBlockchain.AccountB))
            .To(TestBlockchain.AccountA)
            .TestObject;

        testState.Commit(spec);
        testState.CommitTree(0);
        testState.RecalculateStateRoot();
        BlockBuilder blockBuilder = Core.Test.Builders.Build.A.Block.Genesis
                .WithDifficulty(1)
                .WithTotalDifficulty(1L)
                .WithTransactions(tx)
                .WithPostMergeFlag(true);

        if (spec.IsBeaconBlockRootAvailable)
        {
            blockBuilder.WithParentBeaconBlockRoot(parentBeaconBlockRoot);
        }

        return blockBuilder.TestObject;
    }
}
