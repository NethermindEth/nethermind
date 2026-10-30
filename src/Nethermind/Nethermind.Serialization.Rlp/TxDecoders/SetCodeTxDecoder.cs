// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Serialization.FluentRlp;

namespace Nethermind.Serialization.Rlp.TxDecoders;

public sealed class SetCodeTxDecoder(Func<Transaction>? transactionFactory = null)
    : BaseEIP1559TxDecoder(TxType.SetCode, transactionFactory)
{
    private static RlpLimit AuthorizationListLimit => RlpLimit.For<Transaction>(
        checked((int)(RlpLimit.MaxBlockGas / GasCostOf.PerAuthBaseCost + 1)),
        nameof(Transaction.AuthorizationList)
    );

    private static readonly AuthorizationTupleDecoder AuthTupleDecoder = AuthorizationTupleDecoder.Instance;

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowNullArrayElement(int index) =>
        throw new RlpReaderException($"Unexpected null array element at index {index}");

    public override void Decode(ref Transaction? transaction, ReadOnlySpan<byte> transactionSequence,
        ref RlpReader reader, Memory<byte> source, RlpBehaviors rlpBehaviors = RlpBehaviors.None) =>
        DecodeTransaction<Payload>(ref transaction, transactionSequence, ref reader, source, rlpBehaviors);

    public override void Encode(Transaction transaction, ref RlpWriter writer, RlpBehaviors rlpBehaviors = RlpBehaviors.None,
        bool forSigning = false, bool isEip155Enabled = false, ulong chainId = 0) =>
        EncodeTransaction(transaction, ref writer, rlpBehaviors, forSigning);

    public override int GetLength(Transaction transaction, RlpBehaviors rlpBehaviors, bool forSigning = false, bool isEip155Enabled = false, ulong chainId = 0) =>
        GetTransactionLength(transaction, rlpBehaviors, forSigning);

    internal static void EncodeTransaction(Transaction transaction, ref RlpWriter writer, RlpBehaviors rlpBehaviors, bool forSigning)
    {
        int contentLength = GetContentLength(transaction, forSigning);
        StartTypedTransaction(ref writer, TxType.SetCode, Rlp.LengthOfSequence(contentLength), rlpBehaviors);
        RlpSequence sequence = writer.StartSequence(contentLength);
        EncodeEip1559Fields(transaction, ref writer, rlpBehaviors);
        AuthTupleDecoder.EncodeArray(ref writer, transaction.AuthorizationList, rlpBehaviors);
        EncodeTypedSignature(transaction.Signature, forSigning, ref writer);
        writer.EndSequence(sequence);
    }

    internal static int GetTransactionLength(Transaction transaction, RlpBehaviors rlpBehaviors, bool forSigning) =>
        GetTypedTransactionLength(Rlp.LengthOfSequence(GetContentLength(transaction, forSigning)), rlpBehaviors);

    private static int GetContentLength(Transaction transaction, bool forSigning) =>
        GetEip1559FieldsLength(transaction)
        + (transaction.AuthorizationList is null ? 1 : Rlp.LengthOfSequence(AuthTupleDecoder.GetContentLength(transaction.AuthorizationList, RlpBehaviors.None)))
        + GetTypedSignatureLength(transaction.Signature, forSigning);

    private readonly struct Payload : ITxPayloadDecoder
    {
        public static void DecodePayload<TCanonicalChecks>(Transaction transaction, ref RlpReader reader, Memory<byte> source, RlpBehaviors rlpBehaviors)
            where TCanonicalChecks : struct, IFlag
        {
            DecodeEip1559Fields<TCanonicalChecks>(transaction, ref reader, source, rlpBehaviors);
            transaction.AuthorizationList = DecodeAuthorizationList<TCanonicalChecks>(ref reader);
        }

        /// <remarks>The tuples are read in the mode of the transaction without a virtual call each; they never took the other behaviors.</remarks>
        private static AuthorizationTuple[] DecodeAuthorizationList<TCanonicalChecks>(ref RlpReader reader)
            where TCanonicalChecks : struct, IFlag
        {
            RlpReader items = reader.EnterSequence<TCanonicalChecks>(out int itemLength);
            AuthorizationTuple[] tuples = new AuthorizationTuple[items.CountItems<TCanonicalChecks>(AuthorizationListLimit)];
            for (int i = 0; i < tuples.Length; i++)
            {
                if (items.IsNextItemEmptyList()) ThrowNullArrayElement(i);
                tuples[i] = AuthorizationTupleDecoder.Decode<TCanonicalChecks>(ref items, RlpBehaviors.None);
            }

            reader.ExitSequence(in items, itemLength);
            return tuples;
        }

        public static void DecodeTrailing<TCanonicalChecks>(Transaction transaction, ref RlpReader reader, RlpBehaviors rlpBehaviors)
            where TCanonicalChecks : struct, IFlag =>
            DecodeTypedSignature<TCanonicalChecks>(transaction, ref reader, rlpBehaviors);
    }
}
