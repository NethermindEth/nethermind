// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Stateless.Execution.IO;

namespace Nethermind.Stateless.Execution;

public static partial class StatelessExecutor
{
    private static StatelessValidationResult _defaultFailureResult => new()
    {
        NewPayloadRequestRoot = Hash256.Zero,
        IsSuccess = false,
        ChainId = 0,
        SchemaId = 0
    };

    static partial void TryRecoverSendersInBatch(
        Transaction[] transactions, byte[][] encodedTransactions, ISpecProvider specProvider, IReleaseSpec spec, ref bool? recovered)
    {
        if (AirbenderFlag.IsActive)
            recovered = EthereumEcdsa.TryRecoverSendersInBatch(
                transactions, encodedTransactions, new EthereumEcdsa(specProvider.ChainId), !spec.ValidateChainId);
    }

    static partial void TryVerifySendersInBatch(
        Transaction[] transactions, in TrustedPayload payload, ISpecProvider specProvider, IReleaseSpec spec, ref bool verified)
    {
        if (AirbenderFlag.IsActive)
            verified = EthereumEcdsa.TryVerifySendersInBatch(transactions, payload.EncodedTransactions,
                new EthereumEcdsa(specProvider.ChainId), !spec.ValidateChainId, TrustedInput.SenderKeys(payload.SenderHints),
                TrustedInput.SignatureYs(payload.SenderHints), TrustedInput.SenderHintWeights(payload.BlockHash, payload.SenderHints));
    }
}
