// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.Consensus.Producers;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Taiko;

public class TaikoPayloadAttributes : PayloadAttributes
{
    public UInt256 BaseFeePerGas { get; set; }
    public BlockMetadata? BlockMetadata { get; set; }
    public L1Origin? L1Origin { get; set; }

    public override long? GetGasLimit()
    {
        return BlockMetadata!.GasLimit;
    }

    public override PayloadAttributesValidationResult Validate(ISpecProvider specProvider, int apiVersion,
        [NotNullWhen(false)] out string? error)
    {
        if (L1Origin is null)
        {
            error = "L1Origin is required";
            return PayloadAttributesValidationResult.InvalidPayloadAttributes;
        }

        if (BlockMetadata is null)
        {
            error = "BlockMetadata is required";
            return PayloadAttributesValidationResult.InvalidPayloadAttributes;
        }

        if (BlockMetadata.Beneficiary is null)
        {
            error = "BlockMetadata.Beneficiary is required";
            return PayloadAttributesValidationResult.InvalidPayloadAttributes;
        }
        if (BlockMetadata.MixHash is null)
        {
            error = "BlockMetadata.MixHash is required";
            return PayloadAttributesValidationResult.InvalidPayloadAttributes;
        }
        if (BlockMetadata.TxList is null)
        {
            error = "BlockMetadata.TxList is required";
            return PayloadAttributesValidationResult.InvalidPayloadAttributes;
        }
        if (BlockMetadata.ExtraData is null)
        {
            error = "BlockMetadata.ExtraData is required";
            return PayloadAttributesValidationResult.InvalidPayloadAttributes;
        }

        return base.Validate(specProvider, apiVersion, out error);
    }

}
