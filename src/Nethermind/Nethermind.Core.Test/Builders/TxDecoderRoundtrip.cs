// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.Rlp;

namespace Nethermind.Core.Test.Builders;

/// <summary>Encodes a transaction and decodes it back, so a test can exercise the decoder-built form.</summary>
/// <remarks>Fields a decoder derives rather than carries — the frame calldata statistics among them — are set
/// only on the returned transaction, never on a caller-built one.</remarks>
public static class TxDecoderRoundtrip
{
    public static Transaction Roundtrip(Transaction transaction)
    {
        TxDecoder decoder = TxDecoder.Instance;
        byte[] bytes = new byte[decoder.GetLength(transaction, RlpBehaviors.None)];
        RlpWriter writer = new(bytes);
        decoder.Encode(ref writer, transaction);
        RlpReader reader = new(bytes);
        return decoder.Decode(ref reader)!;
    }
}
