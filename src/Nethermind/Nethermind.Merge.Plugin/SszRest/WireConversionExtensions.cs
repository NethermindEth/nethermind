// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Merge.Plugin.SszRest;

/// <summary>
/// Domain ↔ SSZ-wire conversion extensions used by <see cref="SszCodec"/> and
/// the per-version descriptors. Methods are scoped <c>internal</c> so they only
/// surface inside this assembly and don't pollute IntelliSense for unrelated
/// generic-collection callsites.
/// </summary>
internal static class WireConversionExtensions
{
    public static SszTransaction[] ToTxsWire(this byte[][] txs)
    {
        if (txs.Length == 0) return [];
        SszTransaction[] result = new SszTransaction[txs.Length];
        for (int i = 0; i < result.Length; i++) result[i] = new SszTransaction { Bytes = txs[i] };
        return result;
    }

    public static SszWithdrawal[] ToWire(this Withdrawal[]? ws)
    {
        if (ws is null || ws.Length == 0) return [];
        SszWithdrawal[] result = new SszWithdrawal[ws.Length];
        for (int i = 0; i < ws.Length; i++)
            result[i] = new SszWithdrawal
            {
                Index = ws[i].Index,
                ValidatorIndex = ws[i].ValidatorIndex,
                Address = ws[i].Address,
                Amount = ws[i].AmountInGwei
            };
        return result;
    }

    public static Withdrawal[] ToDomain(this SszWithdrawal[]? ws)
    {
        if (ws is null || ws.Length == 0) return [];
        Withdrawal[] result = new Withdrawal[ws.Length];
        for (int i = 0; i < ws.Length; i++)
            result[i] = new Withdrawal
            {
                Index = ws[i].Index,
                ValidatorIndex = ws[i].ValidatorIndex,
                Address = ws[i].Address,
                AmountInGwei = ws[i].Amount
            };
        return result;
    }

    public static byte[]?[] ToBytesArrays(this Hash256[]? hashes)
    {
        if (hashes is null || hashes.Length == 0) return [];
        byte[]?[] result = new byte[]?[hashes.Length];
        for (int i = 0; i < hashes.Length; i++)
        {
            byte[] bytes = new byte[32];
            hashes[i].Bytes.CopyTo(bytes);
            result[i] = bytes;
        }
        return result;
    }

    public static SszKzgCommitment[] ToKzgWire(this byte[][] proofs)
    {
        SszKzgCommitment[] result = new SszKzgCommitment[proofs.Length];
        for (int i = 0; i < proofs.Length; i++) result[i] = SszKzgCommitment.FromSpan(proofs[i]);
        return result;
    }

    public static SszTransaction[] ToExecutionRequestsWire(this byte[][]? reqs)
    {
        if (reqs is null) return [];
        SszTransaction[] result = new SszTransaction[reqs.Length];
        for (int i = 0; i < reqs.Length; i++) result[i] = new SszTransaction { Bytes = reqs[i] };
        return result;
    }

    public static byte[][]? ToExecutionRequests(this SszTransaction[]? reqs) => reqs switch
    {
        null => null,
        [] => [],
        _ => BuildExecutionRequests(reqs),
    };

    private static byte[][] BuildExecutionRequests(SszTransaction[] reqs)
    {
        byte[][] result = new byte[reqs.Length][];
        for (int i = 0; i < reqs.Length; i++) result[i] = reqs[i].Bytes ?? [];
        return result;
    }

    public static BlobsBundleV1Wire ToWire(this BlobsBundleV1? b)
    {
        if (b?.Commitments is null) return new BlobsBundleV1Wire();
        return new BlobsBundleV1Wire
        {
            Commitments = b.Commitments.ToKzgWire(),
            Proofs = b.Proofs!.ToKzgWire(),
            Blobs = ToBlobsWire(b.Blobs!)
        };
    }

    public static BlobsBundleV2Wire ToWire(this BlobsBundleV2? b)
    {
        if (b?.Commitments is null) return new BlobsBundleV2Wire();
        return new BlobsBundleV2Wire
        {
            Commitments = b.Commitments.ToKzgWire(),
            Proofs = b.Proofs!.ToKzgWire(),
            Blobs = ToBlobsWire(b.Blobs!)
        };
    }

    public static ExecutionPayloadBodyV1Wire ToBodyWire(this ExecutionPayloadBodyV1Result body) =>
        new()
        {
            Transactions = body.Transactions.ToTxsWire(),
            Withdrawals = body.Withdrawals.ToWire()
        };

    public static ExecutionPayloadBodyV2Wire ToBodyWire(this ExecutionPayloadBodyV2Result body) =>
        new()
        {
            Transactions = body.Transactions.ToTxsWire(),
            Withdrawals = body.Withdrawals.ToWire(),
            BlockAccessList = body.BlockAccessList ?? []
        };

    private static SszBlob[] ToBlobsWire(byte[][] blobs)
    {
        SszBlob[] result = new SszBlob[blobs.Length];
        for (int i = 0; i < blobs.Length; i++) result[i] = new SszBlob { Bytes = blobs[i] };
        return result;
    }
}
