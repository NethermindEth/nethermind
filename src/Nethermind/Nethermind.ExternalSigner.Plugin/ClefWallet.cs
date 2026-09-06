// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.JsonRpc.Client;
using Nethermind.Serialization.Rlp;
using Nethermind.Wallet;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Security;

namespace Nethermind.ExternalSigner.Plugin
{
    public class ClefWallet(IJsonRpcClient rpcClient) : IWallet
    {
        private readonly HeaderDecoder _headerDecoder = new();

        public event EventHandler<AccountLockedEventArgs> AccountLocked
        {
            add { }
            remove { }
        }

        public event EventHandler<AccountUnlockedEventArgs> AccountUnlocked
        {
            add { }
            remove { }
        }

        public Address[] GetAccounts()
        {
            string[] accounts = rpcClient.Post<string[]>("account_list").GetAwaiter().GetResult() ?? throw new InvalidOperationException("Remote signer 'account_list' response is invalid.");
            if (accounts.Length == 0) throw new InvalidOperationException("Remote signer has not been configured with any signers.");
            return accounts.Select(x => new Address(x)).ToArray();
        }

        public void Import(byte[] keyData, SecureString passphrase) => ThrowNotSupportedException();

        public bool IsUnlocked(Address address)
        {
            ThrowNotSupportedException();
            return false;
        }

        public bool LockAccount(Address address)
        {
            ThrowNotSupportedException();
            return false;
        }

        public Address NewAccount(SecureString passphrase)
        {
            ThrowNotSupportedException();
            return null!;
        }

        public bool UnlockAccount(Address address, SecureString passphrase, TimeSpan? timeSpan = null)
        {
            ThrowNotSupportedException();
            return false;
        }

        public bool TrySign(in ValueHash256 message, Address address, [NotNullWhen(true)] out Signature signature)
        {
            string? signed = rpcClient.Post<string>(
                "account_signData",
                "text/plain",
                address.ToString(),
                (Hash256)message).GetAwaiter().GetResult();
            if (signed is null)
            {
                signature = null!;
                return false;
            }
            signature = new Signature(Bytes.FromHexString(signed));
            return true;
        }

        public bool TrySignMessage(byte[] message, Address address, [NotNullWhen(true)] out Signature signature)
        {
            ArgumentNullException.ThrowIfNull(message);
            string? signed = rpcClient.Post<string>(
                "account_signData",
                "text/plain",
                address,
                message).GetAwaiter().GetResult();
            if (signed is null)
            {
                signature = null!;
                return false;
            }
            signature = new Signature(Bytes.FromHexString(signed));
            return true;
        }

        public bool TrySign(BlockHeader header, Address address, [NotNullWhen(true)] out Signature signature)
        {
            ArgumentNullException.ThrowIfNull(header);

            using ArrayPoolSpan<byte> rlp = _headerDecoder.EncodeToArrayPoolSpan(header, RlpBehaviors.None);
            string? signed = rpcClient.Post<string>(
                "account_signData",
                "application/x-clique-header",
                address.ToString(),
                ((ReadOnlySpan<byte>)rlp).ToHexString(true))
                .GetAwaiter().GetResult();
            if (signed is null)
            {
                signature = null!;
                return false;
            }

            byte[] bytes = Bytes.FromHexString(signed);
            // Clef sets recid to 0/1 without the v-offset.
            signature = bytes.Length == 65 && (bytes[64] == 0 || bytes[64] == 1)
                ? new Signature(bytes.AsSpan(0, 64), bytes[64])
                : new Signature(bytes);
            return true;
        }

        public bool TrySignTransaction(Transaction tx, ulong chainId)
        {
            ArgumentNullException.ThrowIfNull(tx);

            TransactionForRpc transactionForRpc = TransactionForRpc.FromTransaction(tx);
            // Clef rejects certain fields if they are serialized.
            if (transactionForRpc is EIP1559TransactionForRpc eip1559ForRpc)
                eip1559ForRpc.GasPrice = null;

            SignTransactionResponse? signed = rpcClient.Post<SignTransactionResponse>(
                "account_signTransaction",
                transactionForRpc).GetAwaiter().GetResult();
            if (signed is null || signed.Tx is null) return false;

            tx.Signature = new Signature(
                signed.Tx.R!.Value,
                signed.Tx.S!.Value,
                tx.Type == TxType.Legacy ? (ulong)signed.Tx.V! : (ulong)signed.Tx.V! + Signature.VOffset);
            return true;
        }

        [DoesNotReturn, StackTraceHidden]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ThrowNotSupportedException([CallerMemberName] string member = "") =>
            throw new NotSupportedException($"Clef remote signer does not support '{member}'");


        private class SignTransactionResponse
        {
            public string? Raw { get; set; }
            public LegacyTransactionForRpc? Tx { get; set; }
        }
    }
}
