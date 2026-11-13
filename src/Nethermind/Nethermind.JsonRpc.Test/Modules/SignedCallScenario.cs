// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Int256;

namespace Nethermind.JsonRpc.Test.Modules;

/// <summary>Call requests that carry the fields and signature of a signed transaction, shared by the RPC tests of signed calls.</summary>
internal static class SignedCallScenario
{
    /// <summary>A call to <see cref="TestItem.AddressB"/> with the fields and signature of an EIP-1559 transaction signed by <see cref="TestItem.PrivateKeyA"/>.</summary>
    /// <param name="from">The <c>from</c> of the request, omitted when <see langword="null"/>.</param>
    /// <param name="unpriced">Zero fees, so a sender with no account can run it.</param>
    public static EIP1559TransactionForRpc BuildCall(IEthereumEcdsa ecdsa, ulong chainId, Address? from, bool unpriced = false)
    {
        Transaction tx = Build.A.Transaction
            .WithType(TxType.EIP1559)
            .WithChainId(chainId)
            .To(TestItem.AddressB)
            .WithMaxFeePerGas(unpriced ? 0 : 100_000_000_000UL)
            .WithMaxPriorityFeePerGas(unpriced ? 0 : 1_000_000_000UL)
            .WithGasLimit(100_000)
            .WithValue(0)
            .SignedAndResolved(ecdsa, TestItem.PrivateKeyA)
            .TestObject;

        return new()
        {
            Nonce = tx.Nonce,
            To = tx.To,
            Gas = tx.GasLimit,
            MaxFeePerGas = tx.MaxFeePerGas,
            MaxPriorityFeePerGas = tx.MaxPriorityFeePerGas,
            Value = tx.Value,
            ChainId = chainId,
            From = from,
            R = new UInt256(tx.Signature!.RAsSpan, isBigEndian: true),
            S = new UInt256(tx.Signature!.SAsSpan, isBigEndian: true),
            V = tx.Signature!.RecoveryId == 0 ? UInt256.Zero : UInt256.One,
        };
    }
}
