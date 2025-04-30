// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;

namespace Nethermind.WurdumTestPlugin;

public static class L2MessageParser
{
    public static List<Transaction> ParseL2Transactions(L1IncomingMessage message, ulong chainId, ILogger logger)
    {
        if (message.L2Msg.Length > ArbitrumConstants.MaxL2MessageSize)
        {
            logger.Warn($"L2 message size {message.L2Msg.Length} exceeds maximum {ArbitrumConstants.MaxL2MessageSize}, ignoring.");
            return [];
        }

        ReadOnlySpan<byte> l2MsgSpan = Convert.FromBase64String(message.L2Msg);

        switch (message.Header.Kind)
        {
            case ArbitrumL1MessageKind.L2Message:
                return ParseL2MessageFormat(ref l2MsgSpan, message.Header.Sender, message.Header.Timestamp, message.Header.RequestId, chainId, 0, logger);

            case ArbitrumL1MessageKind.L2FundedByL1:
                return ParseL2FundedByL1(ref l2MsgSpan, message.Header, chainId);

            case ArbitrumL1MessageKind.SubmitRetryable:
                return ParseSubmitRetryable(ref l2MsgSpan, message.Header, chainId);

            case ArbitrumL1MessageKind.EthDeposit:
                return ParseEthDeposit(ref l2MsgSpan, message.Header, chainId);

            case ArbitrumL1MessageKind.BatchPostingReport:
                return ParseBatchPostingReport(ref l2MsgSpan, chainId, message.BatchGasCost);

            case ArbitrumL1MessageKind.EndOfBlock:
            case ArbitrumL1MessageKind.RollupEvent:
                return []; // No transactions for these types

            case ArbitrumL1MessageKind.Initialize:
                // Should be handled explicitly at genesis, not during normal operation
                throw new ArgumentException("Initialize message encountered outside of genesis.", nameof(message));

            case ArbitrumL1MessageKind.BatchForGasEstimation:
                throw new NotImplementedException("L1 message type BatchForGasEstimation is unimplemented.");

            case ArbitrumL1MessageKind.Invalid:
                throw new ArgumentException("Invalid L1 message type (0xFF).", nameof(message));

            default:
                // Ignore unknown/invalid message types as per Go implementation
                logger.Warn($"Ignoring L1 message with unknown kind: {message.Header.Kind}");
                return [];
        }
    }

    private static List<Transaction> ParseL2MessageFormat(
        ref ReadOnlySpan<byte> data,
        Address poster,
        ulong timestamp, // Note: timestamp is not directly used in tx creation here
        Hash256? l1RequestId,
        ulong chainId,
        int depth,
        ILogger logger)
    {
        const int maxDepth = 16;
        if (depth >= maxDepth)
        {
            throw new ArgumentException($"L2 message batch depth exceeds maximum of {maxDepth}");
        }

        var l2Kind = (ArbitrumL2MessageKind)ArbitrumBinaryReader.ReadByteOrFail(ref data);
        if (!Enum.IsDefined(l2Kind))
        {
            throw new ArgumentException($"L2 message kind {l2Kind} is not defined.");
        }

        switch (l2Kind)
        {
            case ArbitrumL2MessageKind.UnsignedUserTx:
            case ArbitrumL2MessageKind.ContractTx:
                var parsedTx = ParseUnsignedTx(ref data, poster, l1RequestId, chainId, l2Kind);
                return [ConvertParsedDataToTransaction(parsedTx)];

            case ArbitrumL2MessageKind.Batch:
                var transactions = new List<Transaction>();
                var index = UInt256.Zero;
                while (!data.IsEmpty) // Loop until the span is consumed
                {
                    ReadOnlyMemory<byte> nextMsgData = ArbitrumBinaryReader.ReadByteStringOrFail(ref data, ArbitrumConstants.MaxL2MessageSize);
                    ReadOnlySpan<byte> nextMsgSpan = nextMsgData.Span;

                    Hash256? nextRequestId = null;
                    if (l1RequestId != null)
                    {
                        // Calculate sub-request ID: keccak256(l1RequestId, index)
                        Span<byte> combined = new byte[64];
                        l1RequestId.Bytes.CopyTo(combined[..32]);
                        index.ToBigEndian(combined[32..]);
                        nextRequestId = Keccak.Compute(combined);
                    }

                    transactions.AddRange(ParseL2MessageFormat(ref nextMsgSpan, poster, timestamp, nextRequestId, chainId, depth + 1, logger));

                    if (!nextMsgSpan.IsEmpty)
                    {
                         logger.Warn($"Nested L2 message parsing did not consume all data. Kind: {l2Kind}, Depth: {depth}, Remaining: {nextMsgSpan.Length} bytes.");
                    }

                    index.Add(UInt256.One, out index);
                }
                return transactions;

            case ArbitrumL2MessageKind.SignedTx:
                var legacyTx = Rlp.Decode<Transaction>(data.ToArray(),
                    RlpBehaviors.AllowUnsigned | RlpBehaviors.SkipTypedWrapping | RlpBehaviors.InMempoolForm);

                if (legacyTx.Type >= (TxType)ArbitrumTxType.ArbitrumDeposit || legacyTx.Type == TxType.Blob)
                {
                     throw new ArgumentException($"Unsupported transaction type {legacyTx.Type} encountered in L2MessageKind_SignedTx.");
                }

                return [legacyTx];

            case ArbitrumL2MessageKind.Heartbeat:
                if (timestamp >= ArbitrumConstants.HeartbeatsDisabledAt)
                {
                    throw new ArgumentException("Heartbeat message received after disable time.");
                }

                return [];

            case ArbitrumL2MessageKind.NonmutatingCall:
                 throw new NotImplementedException("L2 message kind NonmutatingCall is unimplemented.");
            case ArbitrumL2MessageKind.SignedCompressedTx:
                 throw new NotImplementedException("L2 message kind SignedCompressedTx is unimplemented.");

            default:
                // Ignore invalid/unknown message kind as per Go implementation
                logger.Warn($"Ignoring L2 message with unknown kind: {l2Kind}");
                return [];
        }
    }

    private static object ParseUnsignedTx(ref ReadOnlySpan<byte> data, Address poster, Hash256? l1RequestId, ulong chainId, ArbitrumL2MessageKind kind)
    {
        var gasLimit = (ulong)ArbitrumBinaryReader.ReadUInt256OrFail(ref data);
        var maxFeePerGas = ArbitrumBinaryReader.ReadUInt256OrFail(ref data);
        var nonce = kind == ArbitrumL2MessageKind.UnsignedUserTx
            ? (ulong)ArbitrumBinaryReader.ReadBigInteger256OrFail(ref data)
            : 0;

        var destination = ArbitrumBinaryReader.ReadAddressFrom256OrFail(ref data);
        destination = destination == Address.Zero ? null : destination;

        var value = ArbitrumBinaryReader.ReadUInt256OrFail(ref data);

        // The rest of the data is the calldata
        ReadOnlyMemory<byte> calldata = data.ToArray();

        return kind switch
        {
            ArbitrumL2MessageKind.UnsignedUserTx =>
                new ArbitrumUnsignedTx(chainId, poster, nonce, maxFeePerGas, gasLimit, destination, value, calldata),
            ArbitrumL2MessageKind.ContractTx => l1RequestId != null
                ? new ArbitrumContractTx(chainId, l1RequestId, poster, maxFeePerGas, gasLimit, destination, value, calldata)
                : throw new ArgumentException("Cannot create ArbitrumContractTx without L1 request ID."),
            _ => throw new ArgumentException($"Invalid txKind '{kind}' passed to ParseUnsignedTx.")
        };
    }

    private static List<Transaction> ParseL2FundedByL1(ref ReadOnlySpan<byte> data, L1IncomingMessageHeader header, ulong chainId)
    {
        if (header.RequestId == null)
        {
            throw new ArgumentException("Cannot process L2FundedByL1 message without L1 request ID.");
        }

        var kind = (ArbitrumL2MessageKind)ArbitrumBinaryReader.ReadByteOrFail(ref data);
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentException($"Invalid L2FundedByL1 message kind: {kind}");
        }

        // Calculate request IDs
        // depositRequestId = keccak256(requestId, 0)
        // unsignedRequestId = keccak256(requestId, 1)
        Span<byte> depositRequestBytes = stackalloc byte[64];
        header.RequestId.Bytes.CopyTo(depositRequestBytes[..32]);
        var depositRequestId = Keccak.Compute(depositRequestBytes);

        Span<byte> unsignedRequestBytes = stackalloc byte[64];
        header.RequestId.Bytes.CopyTo(unsignedRequestBytes[..32]);
        unsignedRequestBytes[63] = 1;
        var unsignedRequestId = Keccak.Compute(unsignedRequestBytes);

        // Parse the unsigned transaction part using the remaining data
        var parsedUnsignedTx = ParseUnsignedTx(ref data, header.Sender, unsignedRequestId, chainId, kind);
        var unsignedTx = ConvertParsedDataToTransaction(parsedUnsignedTx);

        // Create the deposit transaction
        var depositData = new ArbitrumDepositTx(
            chainId,
            depositRequestId,
            Address.Zero,
            header.Sender,
            unsignedTx.Value
        );
        var depositTx = ConvertParsedDataToTransaction(depositData);

        return [depositTx, unsignedTx];
    }

    private static List<Transaction> ParseEthDeposit(ref ReadOnlySpan<byte> data, L1IncomingMessageHeader header, ulong chainId)
    {
        if (header.RequestId == null)
        {
            throw new ArgumentException("Cannot process EthDeposit message without L1 request ID.");
        }

        var toAddr = ArbitrumBinaryReader.ReadAddressOrFail(ref data); // Reads 20 bytes directly
        var value = ArbitrumBinaryReader.ReadUInt256OrFail(ref data);

        var depositData = new ArbitrumDepositTx(
            chainId,
            header.RequestId,
            header.Sender,
            toAddr,
            value
        );

        return [ConvertParsedDataToTransaction(depositData)];
    }

    private static List<Transaction> ParseSubmitRetryable(ref ReadOnlySpan<byte> data, L1IncomingMessageHeader header, ulong chainId)
    {
        ArgumentNullException.ThrowIfNull(header.RequestId, "Cannot process SubmitRetryable message without L1 request ID.");

        var retryTo = ArbitrumBinaryReader.ReadAddressFrom256OrFail(ref data);
        retryTo = retryTo == Address.Zero ? null : retryTo;

        var retryValue = ArbitrumBinaryReader.ReadUInt256OrFail(ref data);
        var depositValue = ArbitrumBinaryReader.ReadUInt256OrFail(ref data);
        var maxSubmissionFee = ArbitrumBinaryReader.ReadUInt256OrFail(ref data);
        var feeRefundAddress = ArbitrumBinaryReader.ReadAddressFrom256OrFail(ref data);
        var callvalueRefundAddress = ArbitrumBinaryReader.ReadAddressFrom256OrFail(ref data); // Beneficiary

        var gasLimit256 = ArbitrumBinaryReader.ReadUInt256OrFail(ref data);
        if (gasLimit256 > ulong.MaxValue)
        {
            throw new ArgumentException("Retryable gas limit overflows ulong.");
        }

        var gasLimit = (ulong)gasLimit256;
        var maxFeePerGas = ArbitrumBinaryReader.ReadUInt256OrFail(ref data);

        var dataLength256 = ArbitrumBinaryReader.ReadUInt256OrFail(ref data);
        if (dataLength256 > ArbitrumConstants.MaxL2MessageSize)
        {
            throw new ArgumentException("Retryable data too large.");
        }

        ReadOnlyMemory<byte> retryData = ArbitrumBinaryReader.ReadBytesOrFail(ref data, (int)dataLength256).ToArray();

        var retryableData = new ArbitrumSubmitRetryableTx(
            chainId,
            header.RequestId,
            header.Sender,
            header.BaseFeeL1,
            depositValue,
            maxFeePerGas,
            gasLimit,
            retryTo,
            retryValue,
            callvalueRefundAddress, // Beneficiary
            maxSubmissionFee,
            feeRefundAddress,
            retryData
        );

        return [ConvertParsedDataToTransaction(retryableData)];
    }

    private static List<Transaction> ParseBatchPostingReport(ref ReadOnlySpan<byte> data, ulong chainId, ulong? batchGasCostFromMsg)
    {
        ArgumentNullException.ThrowIfNull(batchGasCostFromMsg, "Cannot process BatchPostingReport message without Gas cost.");

        var batchTimestamp = ArbitrumBinaryReader.ReadUInt256OrFail(ref data);
        var batchPosterAddr = ArbitrumBinaryReader.ReadAddressOrFail(ref data);
        _ = ArbitrumBinaryReader.ReadHash256OrFail(ref data); // dataHash is not used directly in tx, but parsed
        var batchNum256 = ArbitrumBinaryReader.ReadUInt256OrFail(ref data);
        if (batchNum256 > ulong.MaxValue)
        {
            throw new ArgumentException("Batch number overflows ulong.");
        }

        var batchNum = (ulong)batchNum256;
        var l1BaseFee = ArbitrumBinaryReader.ReadUInt256OrFail(ref data);

        // Extra gas is optional in Go, try reading it
        ulong extraGas = 0;
        if (!data.IsEmpty && !ArbitrumBinaryReader.TryReadULongBigEndian(ref data, out extraGas) && !data.IsEmpty)
        {
            // If reading fails but data is not empty, it's an error
            // Otherwise, EOF is fine, extraGas remains 0
            throw new ArgumentException("Invalid data after L1 base fee in BatchPostingReport.");
        }

        // Calculate total gas cost (matches Go logic) following SaturatingAdd go implementation
        var batchDataGas = batchGasCostFromMsg > ulong.MaxValue - extraGas ? ulong.MaxValue : batchGasCostFromMsg.Value + extraGas;

        var internalTxParsed = new ArbitrumInternalTx(chainId, batchTimestamp, batchPosterAddr, batchNum, batchDataGas, l1BaseFee);

        return [ConvertParsedDataToTransaction(internalTxParsed)];
    }

    private static Transaction ConvertParsedDataToTransaction(object parsedData)
    {
        return parsedData switch
        {
            ArbitrumUnsignedTx d => new Transaction
            {
                Type = (TxType)ArbitrumTxType.ArbitrumUnsigned,
                ChainId = d.ChainId,
                SenderAddress = d.From,
                Nonce = d.Nonce,
                GasPrice = UInt256.Zero, // EIP-1559 fields used instead
                DecodedMaxFeePerGas = d.GasFeeCap,
                // MaxPriorityFeePerGas is implicitly 0 for this type
                GasLimit = (long)d.Gas,
                To = d.To,
                Value = d.Value,
                Data = d.Data.ToArray()
            },
            ArbitrumContractTx d => new Transaction
            {
                Type = (TxType)ArbitrumTxType.ArbitrumContract,
                ChainId = d.ChainId,
                SenderAddress = d.From,
                SourceHash = d.RequestId, // Use SourceHash for RequestId
                Nonce = UInt256.Zero,
                GasPrice = UInt256.Zero,
                DecodedMaxFeePerGas = d.GasFeeCap,
                GasLimit = (long)d.Gas,
                To = d.To,
                Value = d.Value,
                Data = d.Data.ToArray(),
                IsOPSystemTransaction = true, // Contract transactions are system transactions
            },
            ArbitrumDepositTx d => new ArbitrumTransaction<ArbitrumDepositTx>(d)
            {
                Type = (TxType)ArbitrumTxType.ArbitrumDeposit,
                ChainId = d.ChainId,
                SenderAddress = d.From, // L1 sender
                SourceHash = d.L1RequestId, // Use SourceHash for RequestId
                Nonce = UInt256.Zero, // Nonce is 0
                GasPrice = UInt256.Zero, // No gas price
                DecodedMaxFeePerGas = UInt256.Zero,
                GasLimit = 0, // No gas limit
                To = d.To, // L2 recipient
                Value = d.Value,
                IsOPSystemTransaction = true, // Deposits are system transactions
                Mint = d.Value, // Mint the deposited value on L2
            },
            ArbitrumSubmitRetryableTx d => new ArbitrumTransaction<ArbitrumSubmitRetryableTx>(d)
            {
                Type = (TxType)ArbitrumTxType.ArbitrumSubmitRetryable,
                ChainId = d.ChainId,
                SenderAddress = d.From, // L1 sender
                SourceHash = d.RequestId, // Use SourceHash for RequestId
                Nonce = UInt256.Zero, // Nonce is 0
                GasPrice = UInt256.Zero,
                DecodedMaxFeePerGas = d.GasFeeCap, // Gas fee cap for the L2 execution
                GasLimit = (long)d.Gas, // Gas limit for the L2 execution
                To = ArbitrumConstants.ArbRetryableTxAddress, // Target is the precompile
                Value = UInt256.Zero, // Tx value is 0, L2 execution value is in data
                Data = Array.Empty<byte>(), // TODO: ABI encode parameters
                IsOPSystemTransaction = true, // Retryable submissions are system transactions
                // Mint represents the ETH deposited with the retryable (DepositValue)
                Mint = d.DepositValue,
            },
            ArbitrumInternalTx d => new ArbitrumTransaction<ArbitrumInternalTx>(d)
            {
                Type = (TxType)ArbitrumTxType.ArbitrumInternal,
                ChainId = d.ChainId,
                SenderAddress = null, // No specific sender for internal tx
                Nonce = UInt256.Zero,
                GasPrice = UInt256.Zero,
                DecodedMaxFeePerGas = UInt256.Zero,
                GasLimit = 0,
                To = ArbitrumConstants.ArbosAddress, // Target is Arbos precompile
                Value = UInt256.Zero,
                Data = Array.Empty<byte>(),
                IsOPSystemTransaction = true, // Internal transactions are system transactions
            },
            _ => throw new ArgumentException($"Unsupported parsed data type: {parsedData.GetType().Name}")
        };
    }
}
