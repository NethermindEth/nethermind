// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Serialization.FluentRlp;

namespace Nethermind.Serialization.Rlp;

/// <summary>Decodes the EIP-8141 claim tuple <c>[verifier, key, msg, gas_limit, witness_len]</c>.</summary>
/// <remarks>A pure structural read: the key and msg shapes and the gas and witness-length rules are checked by
/// <c>FrameTxValidation.IsWellFormed</c>.</remarks>
public sealed class TxFrameClaimDecoder : RlpDecoder<TxFrameClaim>
{
    public static readonly TxFrameClaimDecoder Instance = new();

    private static readonly RlpLimit _keyRlpLimit = RlpLimit.For<TxFrameClaim>(32, nameof(TxFrameClaim.Key));
    private static readonly RlpLimit _msgRlpLimit = RlpLimit.For<TxFrameClaim>(32, nameof(TxFrameClaim.Msg));

    protected override TxFrameClaim DecodeInternal(ref RlpReader reader, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        => rlpBehaviors.IsTrustedInput()
            ? Decode<OffFlag>(ref reader, Memory<byte>.Empty, rlpBehaviors)
            : Decode<OnFlag>(ref reader, Memory<byte>.Empty, rlpBehaviors);

    /// <param name="source">The memory that holds the bytes of <paramref name="reader"/>, so that the values refer to it; empty to copy them.</param>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static TxFrameClaim Decode<TCanonicalChecks>(ref RlpReader reader, Memory<byte> source, RlpBehaviors rlpBehaviors)
        where TCanonicalChecks : struct, IFlag
    {
        RlpReader fields = reader.EnterSequence<TCanonicalChecks>(out int itemLength);

        Address verifier = fields.DecodeAddress();
        ReadOnlyMemory<byte> key = fields.DecodeByteArrayMemory<TCanonicalChecks>(source, _keyRlpLimit);
        ReadOnlyMemory<byte> msg = fields.DecodeByteArrayMemory<TCanonicalChecks>(source, _msgRlpLimit);
        ulong gasLimit = fields.DecodeULong<TCanonicalChecks>();
        ulong witnessLength = fields.DecodeULong<TCanonicalChecks>();

        reader.ExitSequence(in fields, itemLength, rlpBehaviors);

        return new TxFrameClaim(verifier, key, msg, gasLimit, witnessLength);
    }

    // A root of its own, so that the writes of each claim keep their inline budget.
    [MethodImpl(MethodImplOptions.NoInlining)]
    public override void Encode(ref RlpWriter writer, TxFrameClaim item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        RlpSequence sequence = writer.StartSequence(GetContentLength(item));
        writer.Encode(item.Verifier);
        writer.Encode(item.Key);
        writer.Encode(item.Msg);
        writer.Encode(item.GasLimit);
        writer.Encode(item.WitnessLength);
        writer.EndSequence(sequence);
    }

    /// <remarks>A missing list encodes as the empty one: <c>claims</c> is mandatory in the payload.</remarks>
    public void EncodeArray(ref RlpWriter writer, TxFrameClaim[]? items)
    {
        RlpSequence sequence = writer.StartSequence(GetArrayContentLength(items));
        foreach (TxFrameClaim item in items ?? [])
        {
            Encode(ref writer, item);
        }

        writer.EndSequence(sequence);
    }

    public override int GetLength(TxFrameClaim item, RlpBehaviors rlpBehaviors) =>
        Rlp.LengthOfSequence(GetContentLength(item));

    public int GetArrayLength(TxFrameClaim[]? items) => Rlp.LengthOfSequence(GetArrayContentLength(items));

    private static int GetArrayContentLength(TxFrameClaim[]? items)
    {
        int length = 0;
        foreach (TxFrameClaim item in items ?? [])
        {
            length += Rlp.LengthOfSequence(GetContentLength(item));
        }

        return length;
    }

    private static int GetContentLength(TxFrameClaim item) =>
        Rlp.LengthOf(item.Verifier)
        + Rlp.LengthOf(item.Key)
        + Rlp.LengthOf(item.Msg)
        + Rlp.LengthOf(item.GasLimit)
        + Rlp.LengthOf(item.WitnessLength);
}
