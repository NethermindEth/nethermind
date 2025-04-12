// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Optimism.Rpc;

public class OptimismPayloadAttributes : PayloadAttributes
{
    private byte[][]? _encodedTransactions;

    public byte[][]? Transactions
    {
        get { return _encodedTransactions; }
        set
        {
            _encodedTransactions = value;
            _transactions = null;
        }
    }
    public bool NoTxPool { get; set; }
    public long GasLimit { get; set; }
    public override long? GetGasLimit() => GasLimit;

    /// <remarks>
    /// See <see href="https://specs.optimism.io/protocol/holocene/exec-engine.html#eip-1559-parameters-in-payloadattributesv3"/>
    /// </remarks>
    public byte[]? EIP1559Params { get; set; }

    private int TransactionsLength => Transactions?.Length ?? 0;

    private Transaction[]? _transactions;

    /// <summary>
    /// Decodes and returns an array of <see cref="Transaction"/> from <see cref="Transactions"/>.
    /// </summary>
    /// <returns>An RLP-decoded array of <see cref="Transaction"/>.</returns>
    public Transaction[]? GetTransactions() => _transactions ??= Transactions?
        .Select(static (t, i) =>
        {
            try
            {
                return Rlp.Decode<Transaction>(t, RlpBehaviors.SkipTypedWrapping);
            }
            catch (RlpException e)
            {
                throw new RlpException($"Transaction {i} is not valid", e);
            }
        }).ToArray();

    /// <summary>
    /// RLP-encodes and sets the transactions specified to <see cref="Transactions"/>.
    /// </summary>
    /// <param name="transactions">An array of transactions to encode.</param>
    public void SetTransactions(params Transaction[] transactions)
    {
        Transactions = transactions
            .Select(static t => Rlp.Encode(t, RlpBehaviors.SkipTypedWrapping).Bytes)
            .ToArray();
        _transactions = transactions;
    }

    protected override int ComputePayloadIdMembersSize() =>
        base.ComputePayloadIdMembersSize()
        + sizeof(bool) // noTxPool
        + (Keccak.Size * TransactionsLength) // Txs
        + sizeof(long) // gasLimit
        + ((EIP1559Params?.Length * sizeof(byte)) ?? 0); // eip1559Params

    protected override int WritePayloadIdMembers(BlockHeader parentHeader, Span<byte> inputSpan)
    {
        var offset = base.WritePayloadIdMembers(parentHeader, inputSpan);

        inputSpan[offset] = NoTxPool ? (byte)1 : (byte)0;
        offset += 1;

        Transaction[]? transactions = GetTransactions();
        if (transactions is not null)
        {
            foreach (Transaction tx in transactions)
            {
                tx.Hash!.Bytes.CopyTo(inputSpan.Slice(offset, Keccak.Size));
                offset += Keccak.Size;
            }
        }

        BinaryPrimitives.WriteInt64BigEndian(inputSpan.Slice(offset, sizeof(long)), GasLimit);
        offset += sizeof(long);

        if (EIP1559Params is not null)
        {
            EIP1559Params.CopyTo(inputSpan.Slice(offset));
            offset += EIP1559Params.Length;
        }

        return offset;
    }

    public override PayloadAttributesValidationResult Validate(ISpecProvider specProvider, int apiVersion,
        [NotNullWhen(false)] out string? error)
    {
        if (GasLimit == 0)
        {
            error = "Gas Limit should not be zero";
            return PayloadAttributesValidationResult.InvalidPayloadAttributes;
        }

        IReleaseSpec releaseSpec = specProvider.GetSpec(ForkActivation.TimestampOnly(Timestamp));
        if (!releaseSpec.IsOpHoloceneEnabled && EIP1559Params is not null)
        {
            error = $"{nameof(EIP1559Params)} should be null before Holocene";
            return PayloadAttributesValidationResult.InvalidPayloadAttributes;
        }
        if (releaseSpec.IsOpHoloceneEnabled && !this.TryDecodeEIP1559Parameters(out _, out var decodeError))
        {
            error = decodeError;
            return PayloadAttributesValidationResult.InvalidPayloadAttributes;
        }

        try
        {
            GetTransactions();
        }
        catch (RlpException e)
        {
            error = $"Error decoding transactions: {e}";
            return PayloadAttributesValidationResult.InvalidPayloadAttributes;
        }
        return base.Validate(specProvider, apiVersion, out error);
    }

    public override string ToString()
    {
        StringBuilder sb = new StringBuilder($"{nameof(PayloadAttributes)} {{")
            .Append($"{nameof(Timestamp)}: {Timestamp}, ")
            .Append($"{nameof(PrevRandao)}: {PrevRandao}, ")
            .Append($"{nameof(SuggestedFeeRecipient)}: {SuggestedFeeRecipient}, ")
            .Append($"{nameof(GasLimit)}: {GasLimit}, ")
            .Append($"{nameof(NoTxPool)}: {NoTxPool}, ")
            .Append($"{nameof(EIP1559Params)}: {EIP1559Params}")
            .Append($"{nameof(Transactions)}: {Transactions?.Length ?? 0}");

        if (Withdrawals is not null)
            sb.Append($", {nameof(Withdrawals)} count: {Withdrawals.Length}");

        sb.Append('}');
        return sb.ToString();
    }
}
