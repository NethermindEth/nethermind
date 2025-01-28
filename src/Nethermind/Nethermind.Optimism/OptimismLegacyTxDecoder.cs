// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Serialization.Rlp;
using Nethermind.Serialization.Rlp.TxDecoders;
using Nethermind.TxPool;

namespace Nethermind.Optimism;

public sealed class OptimismLegacyTxDecoder : LegacyTxDecoder<Transaction>
{
    protected override Signature? DecodeSignature(ulong v, ReadOnlySpan<byte> rBytes, ReadOnlySpan<byte> sBytes, Signature? fallbackSignature = null,
        RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (v == 0 && rBytes.IsEmpty && sBytes.IsEmpty)
        {
            return null;
        }
        return base.DecodeSignature(v, rBytes, sBytes, fallbackSignature, rlpBehaviors);
    }
}

public sealed class OptimismLegacyTxValidator(ulong chainId) : ITxValidator
{
    private readonly ITxValidator _postBedrockValidator = new CompositeTxValidator([
        IntrinsicGasTxValidator.Instance,
        new LegacySignatureTxValidator(chainId),
        ContractSizeTxValidator.Instance,
        NonBlobFieldsTxValidator.Instance,
        NonSetCodeFieldsTxValidator.Instance
    ]);

    public ValidationResult IsWellFormed(Transaction transaction, IReleaseSpec releaseSpec)
    {
        // In Optimism, EIP1559 is activated in Bedrock
        var isPreBedrock = !releaseSpec.IsEip1559Enabled;
        if (isPreBedrock)
        {
            // Pre-Bedrock we peform no validation at all
            return ValidationResult.Success;
        }

        return _postBedrockValidator.IsWellFormed(transaction, releaseSpec);
    }
}
