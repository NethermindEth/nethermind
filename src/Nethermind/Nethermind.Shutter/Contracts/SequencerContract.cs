// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Abi;
using Nethermind.Blockchain.Contracts;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Shutter.Contracts;

public class SequencerContract : Contract, ISequencerContract
{
    public AbiEncodingInfo TransactionSubmittedAbi { get => _transactionSubmittedAbi; }
    private readonly AbiEncodingInfo _transactionSubmittedAbi;

    public SequencerContract(ISpecProvider specProvider, Address address)
        : base(specProvider, null, address)
    {
        _transactionSubmittedAbi = AbiDefinition.GetEvent(nameof(ISequencerContract.TransactionSubmitted)).GetCallInfo(AbiEncodingStyle.None);
    }
}
