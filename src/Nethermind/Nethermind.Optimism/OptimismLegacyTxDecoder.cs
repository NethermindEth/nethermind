// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Serialization.Rlp;
using Nethermind.Serialization.Rlp.TxDecoders;
using Nethermind.TxPool;

namespace Nethermind.Optimism;

public class OptimismLegacyTxDecoder : LegacyTxDecoder<Transaction>
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

public class OptimismLegacyTxValidator : ITxValidator
{
    public ValidationResult IsWellFormed(Transaction transaction, IReleaseSpec releaseSpec)
    {
        // In op bedrock eip1559 activated with bedrock
        if (releaseSpec.IsEip1559Enabled)
        {
            return transaction.Signature is null ? new ValidationResult("Empty signature") : ValidationResult.Success;
        }

        return ValidationResult.Success;
    }
}
