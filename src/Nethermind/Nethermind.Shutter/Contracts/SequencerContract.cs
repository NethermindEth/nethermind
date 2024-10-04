// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Abi;
using Nethermind.Blockchain.Contracts;
using Nethermind.Core;

namespace Nethermind.Shutter.Contracts;

public class SequencerContract : Contract, ISequencerContract
{
    public AbiEncodingInfo TransactionSubmittedAbi { get => _transactionSubmittedAbi; }
    private readonly AbiEncodingInfo _transactionSubmittedAbi;

    public SequencerContract(Address address)
        : base(null, address)
    {
        _transactionSubmittedAbi = AbiDefinition.GetEvent(nameof(ISequencerContract.TransactionSubmitted)).GetCallInfo(AbiEncodingStyle.None);
    }
}
