// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Flashbots.Modules.Flashbots;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using Nethermind.State;

namespace Nethermind.Flasbots.Test;

public partial class FlashbotsModuleTests
{

    public async Task TestValidateBuilderSubmissionV3()
    {
        using MergeTestBlockChain chain = await CreateBlockChain(Cancun.Instance);
        ReadOnlyTxProcessingEnv readOnlyTxProcessingEnv = chain.CreateReadOnlyTxProcessingEnv();
        IFlashbotsRpcModule rpc = CreateFlashbotsModule(chain, readOnlyTxProcessingEnv);
        BlockHeader currentHeader = chain.BlockTree.Head.Header;
        IWorldState State = chain.State;

        UInt256 nonce = State.GetNonce(TestKeysAndAddress.TestAddr);

        Transaction tx1 = Build.A.Transaction.WithNonce(nonce).WithTo(new Address("0x16")).WithValue(10).WithGasLimit(21000).WithGasPrice(TestKeysAndAddress.BaseInitialFee).Signed(TestKeysAndAddress.PrivateKey).TestObject;
        chain.TxPool.SubmitTx(tx1, TxPool.TxHandlingOptions.None);

        Transaction tx2 = Build.A.Transaction.WithNonce(nonce + 1).WithValue(0).WithGasLimit(1000000).WithGasPrice(2*TestKeysAndAddress.BaseInitialFee).Signed(TestKeysAndAddress.PrivateKey).TestObject;
        chain.TxPool.SubmitTx(tx2, TxPool.TxHandlingOptions.None);

        UInt256 baseFee = BaseFeeCalculator.Calculate(currentHeader, chain.SpecProvider.GetFinalSpec());

        Transaction tx3 = Build.A.Transaction.WithNonce(nonce + 2).WithValue(10).WithGasLimit(21000).WithValue(baseFee).Signed(TestKeysAndAddress.PrivateKey).TestObject;
        chain.TxPool.SubmitTx(tx3, TxPool.TxHandlingOptions.None);

        Withdrawal[] withdrawals = [
            Build.A.Withdrawal.WithIndex(0).WithValidatorIndex(1).WithAmount(100).WithRecipient(TestKeysAndAddress.TestAddr).TestObject,
            Build.A.Withdrawal.WithIndex(1).WithValidatorIndex(1).WithAmount(100).WithRecipient(TestKeysAndAddress.TestAddr).TestObject
        ];

        
    }
}
