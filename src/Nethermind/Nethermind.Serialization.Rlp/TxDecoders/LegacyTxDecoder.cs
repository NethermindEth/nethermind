// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Serialization.Rlp.TxDecoders;

public class LegacyTxDecoder<T>(Func<T>? transactionFactory = null) : BaseTxDecoder<T>(TxType.Legacy, transactionFactory) where T : Transaction, new()
{
    private static bool IncludeSigChainIdHack(bool isEip155Enabled, ulong chainId) => isEip155Enabled && chainId != 0;

    protected override ulong GetSignatureFirstElement(Signature signature) => signature.V;

    protected override void EncodeSignature<TWriter>(Signature? signature, ref TWriter writer, bool forSigning, bool isEip155Enabled, ulong chainId)
    {
        if (forSigning)
        {
            if (IncludeSigChainIdHack(isEip155Enabled, chainId))
            {
                writer.Encode(chainId);
                writer.Encode(Rlp.OfEmptyByteArray);
                writer.Encode(Rlp.OfEmptyByteArray);
            }
        }
        else
        {
            base.EncodeSignature(signature, ref writer, false, isEip155Enabled, chainId);
        }
    }

    protected override Signature? DecodeSignature(ulong v, ReadOnlySpan<byte> rBytes, ReadOnlySpan<byte> sBytes, Signature? fallbackSignature = null, RlpBehaviors rlpBehaviors = RlpBehaviors.None) =>
        SignatureBuilder.FromBytes(v, rBytes, sBytes, rlpBehaviors) ?? fallbackSignature;

    protected override int GetSignatureLength(Signature? signature, bool forSigning, bool isEip155Enabled = false, ulong chainId = 0)
    {
        if (forSigning)
        {
            int contentLength = 0;
            if (IncludeSigChainIdHack(isEip155Enabled, chainId))
            {
                contentLength += Rlp.LengthOf(chainId);
                contentLength += 1;
                contentLength += 1;
            }

            return contentLength;
        }

        return base.GetSignatureLength(signature, false, isEip155Enabled, chainId);
    }
}
