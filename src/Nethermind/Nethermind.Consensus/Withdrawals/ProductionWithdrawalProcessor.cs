using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.State.Proofs;

namespace Nethermind.Consensus.Withdrawals;

public class ProductionWithdrawalProcessor : IWithdrawalProcessor
{
    private readonly IWithdrawalProcessor _processor;

    public ProductionWithdrawalProcessor(IWithdrawalProcessor processor) =>
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));

    public void ProcessWithdrawals(Block block, IReleaseSpec spec)
    {
        _processor.ProcessWithdrawals(block, spec);

        if (spec.IsEip4895Enabled)
        {
            block.Header.WithdrawalsRoot = block.Withdrawals.Length == 0
                ? Keccak.EmptyTreeHash
                : new WithdrawalTrie(block.Withdrawals!).RootHash;
        }
    }
}
