// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;

namespace Nethermind.Serialization.Rlp.Eip7928;

/// <remarks>
/// EIP-8298 extends <c>CodeChange</c> with an optional trailing <c>new_code_hash</c>: <c>[index, new_code]</c> for code
/// installed by creation or EIP-7702, <c>[index, b"", new_code_hash]</c> for code adopted by <c>SETCODEFROM</c>.
/// </remarks>
public class CodeChangeDecoder : IndexedChangeDecoder<CodeChange>
{
    public static readonly CodeChangeDecoder Instance = new();
    private static readonly RlpLimit _codeLimit = new(Eip7928Constants.MaxCodeSize, "", ReadOnlyMemory<char>.Empty);

    protected override CodeChange DecodeFields(ref RlpReader ctx)
        => new(ctx.DecodeUInt(), ctx.DecodeByteArray(_codeLimit));

    protected override CodeChange DecodeFields(ref RlpReader ctx, int end)
    {
        uint index = ctx.DecodeUInt();
        byte[] code = ctx.DecodeByteArray(_codeLimit);
        if (ctx.Position >= end) return new CodeChange(index, code);

        if (code.Length != 0) ThrowAdoptedCodeNotEmpty();
        return CodeChange.Adopted(index, ctx.DecodeValueKeccakNonNull());
    }

    protected override void EncodeValue<TWriter>(ref TWriter writer, CodeChange item)
    {
        if (item.IsAdopted)
        {
            writer.Encode(Array.Empty<byte>());
            writer.Encode(item.CodeHash);
            return;
        }

        writer.Encode(item.Code);
    }

    protected override int GetValueLength(CodeChange item)
        => item.IsAdopted ? Rlp.LengthOf(Array.Empty<byte>()) + Rlp.LengthOfKeccakRlp : Rlp.LengthOf(item.Code);

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowAdoptedCodeNotEmpty()
        => throw new RlpException("Code change with a code hash must have empty code.");
}
