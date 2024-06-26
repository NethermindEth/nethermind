// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;
using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Nethermind.Serialization.Rlp.Eip7702;
public class AuthorizationListDecoder : IRlpStreamDecoder<AuthorizationTuple[]?>, IRlpValueDecoder<AuthorizationTuple[]?>
{
    private readonly AuthorizationTupleDecoder _tupleDecoder = new();
    public AuthorizationTuple[]? Decode(
        RlpStream rlpStream,
        RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (rlpStream.IsNextItemNull())
            ThrowNullAuthorizationListRlpException();

        return rlpStream.DecodeArray<AuthorizationTuple>(
            (rlp) => _tupleDecoder.Decode(rlp, rlpBehaviors),
            rlpBehaviors.HasFlag(RlpBehaviors.AllowExtraBytes));
    }

    public AuthorizationTuple[]? Decode(
        ref Rlp.ValueDecoderContext decoderContext,
        RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (decoderContext.IsNextItemNull())
            ThrowNullAuthorizationListRlpException();

        AuthorizationTuple[] result = decoderContext.DecodeArray<AuthorizationTuple>(
            _tupleDecoder,
            rlpBehaviors.HasFlag(RlpBehaviors.AllowExtraBytes));
        return result;
    }

    public void Encode(
        RlpStream stream,
        AuthorizationTuple[] items,
        RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (items is null)
            throw new ArgumentNullException(nameof(items));

        int contentLength = GetLength(items, rlpBehaviors);
        stream.StartSequence(contentLength);
        foreach (AuthorizationTuple tuple in items)
        {
            _tupleDecoder.Encode(stream, tuple, rlpBehaviors);
        }
    }

    public int GetLength(AuthorizationTuple[] authorizationTuples, RlpBehaviors rlpBehaviors)
    {
        if (authorizationTuples is null) throw new ArgumentNullException(nameof(authorizationTuples));
        int total = 0;
        foreach (AuthorizationTuple tuple in authorizationTuples)
        {
            total += _tupleDecoder.GetLength(tuple, rlpBehaviors);
        }
        return Rlp.LengthOfSequence(total);
    }


    [StackTraceHidden]
    [DoesNotReturn]
    private void ThrowNullAuthorizationListRlpException()
    {
        throw new RlpException("Authorization list cannot be null.");
    }
}
