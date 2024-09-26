// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.State;

namespace Nethermind.TxPool.Filters;
public class IsValidTxSender(IWorldState worldState, ICodeInfoRepository codeInfoRepository, IChainHeadSpecProvider specProvider)
{
    public bool IsValid(Address sender)
    {
        return specProvider.GetCurrentHeadSpec().IsEip3607Enabled
            && worldState.HasCode(sender)
            && (!specProvider.GetCurrentHeadSpec().IsEip7702Enabled || !codeInfoRepository.TryGetDelegation(worldState, sender, out _));
    }
}
