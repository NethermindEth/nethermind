// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Core.JsonConverters;

namespace Nethermind.Evm;

public class BlockOverride
{
    [JsonConverter(typeof(NullableQuantityULongConverter))]
    public ulong? Number { get; set; }
    public Hash256? PrevRandao { get; set; }
    [JsonConverter(typeof(NullableQuantityULongConverter))]
    public ulong? Time { get; set; }
    [JsonConverter(typeof(NullableQuantityULongConverter))]
    public ulong? GasLimit { get; set; }
    public Address? FeeRecipient { get; set; }
    [JsonConverter(typeof(NullableQuantityUInt256Converter))]
    public UInt256? BaseFeePerGas { get; set; }
    [JsonConverter(typeof(NullableQuantityUInt256Converter))]
    public UInt256? BlobBaseFee { get; set; }

    public void ApplyOverrides(BlockHeader result)
    {
        if (Time is not null) result.Timestamp = Time.Value;
        if (GasLimit is not null)
        {
            if (GasLimit > long.MaxValue)
            {
                throw new OverflowException($"GasLimit value is too large, max value {long.MaxValue}");
            }
            result.GasLimit = GasLimit.Value;
        }

        if (Number is not null)
            result.Number = Number.Value;
        if (FeeRecipient is not null)
        {
            // Set Author as well because GasBeneficiary = Author ?? Beneficiary.
            // Mirrors geth: blockCtx.Coinbase = *o.FeeRecipient.
            result.Beneficiary = result.Author = FeeRecipient;
        }
        if (BaseFeePerGas is not null) result.BaseFeePerGas = BaseFeePerGas.Value;
        if (PrevRandao is not null && PrevRandao != Hash256.Zero) result.MixHash = PrevRandao;
        // BlobBaseFee is not a direct header field — it is derived from ExcessBlobGas via the
        // EIP-4844 formula. The override is applied via BlobBaseFeeOverrideCalculatorDecorator
        // (and for simulate via IBlobBaseFeeOverrideProvider) instead.
    }
}
