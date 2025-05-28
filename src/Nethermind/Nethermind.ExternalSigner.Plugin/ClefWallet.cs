// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.JsonRpc.Client;
using Nethermind.Serialization.Rlp;
using Nethermind.Wallet;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Security;
using System.Text;
using System.Text.Unicode;

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
            var accounts = rpcClient.Post<string[]>("account_list").GetAwaiter().GetResult() ?? throw new InvalidOperationException("Remote signer 'account_list' response is invalid.");
            if (accounts.Length == 0) throw new InvalidOperationException("Remote signer has not been configured with any signers.");
            return accounts.Select(x => new Address(x)).ToArray();
        }

        public void Import(byte[] keyData, SecureString passphrase)
        {
            ThrowNotSupportedException();
        }

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
            return null;
        }

        public Signature Sign(Hash256 message, Address address, SecureString? passphrase = null)
        {
            string? signed = rpcClient.Post<string>(
                "account_signData",
                "text/plain",
                address.ToString(),
                message)
                .GetAwaiter().GetResult();
            if (signed is null) ThrowInvalidOperationSignFailed();
            byte[] bytes = Bytes.FromHexString(signed);
            return new Signature(bytes);
        }

        public Signature Sign(Hash256 message, Address address)
        {
            return Sign(message, address, null);
        }

        public Signature Sign(BlockHeader header, Address address)
        {
            ArgumentNullException.ThrowIfNull(header);
            int contentLength = _headerDecoder.GetLength(header, RlpBehaviors.None);
            IByteBuffer buffer = PooledByteBufferAllocator.Default.Buffer(contentLength);
            try
            {
                RlpStream rlpStream = new NettyRlpStream(buffer);
                rlpStream.Encode(header);
                string? signed = rpcClient.Post<string>(
                    "account_signData",
                    "application/x-clique-header",
                    address.ToString(),
                    buffer.AsSpan().ToHexString(true))
                    .GetAwaiter().GetResult();
                if (signed is null) ThrowInvalidOperationSignFailed();
                byte[] bytes = Bytes.FromHexString(signed);

                //Clef will set recid to 0/1, without the VOffset
                return bytes.Length == 65 && (bytes[64] == 0 || bytes[64] == 1)
                    ? new Signature(bytes.AsSpan(0, 64), bytes[64])
                    : new Signature(bytes);
            }
            finally
            {
                buffer.Release();
            }
        }

        /// <summary>
        /// Sends a <see cref="TransactionForRpc"/> for signing to clef. <paramref name="chainId"/> has to match what clef is configured with.
        /// </summary>
        /// <param name="transaction"></param>
        /// <param name="chainId"></param>
        public void Sign(Transaction transaction, ulong chainId)
        {
            ArgumentNullException.ThrowIfNull(transaction);

            TransactionForRpc transactionForRpc = TransactionForRpc.FromTransaction(transaction);
            //Clef will complain about certain fields if they are serialized
            if (transactionForRpc is EIP1559TransactionForRpc eip1559ForRpc)
                eip1559ForRpc.GasPrice = null;
            SignTransactionResponse? signed = rpcClient.Post<SignTransactionResponse>(
                "account_signTransaction",
                transactionForRpc).GetAwaiter().GetResult();
            if (signed is null || signed.Tx is null) ThrowInvalidOperationSignFailed();

            transaction.Signature = new Signature(signed.Tx.R!.Value, signed.Tx.S!.Value, transaction.Type == TxType.Legacy ? (ulong)(signed.Tx.V!) : (ulong)(signed.Tx.V!) + Signature.VOffset);
        }

        public Signature SignMessage(byte[] message, Address address)
        {
            ArgumentNullException.ThrowIfNull(message);

            string? signed = rpcClient.Post<string>(
                "account_signData",
                "text/plain",
                address,
                message).GetAwaiter().GetResult();
            if (signed is null) ThrowInvalidOperationSignFailed();
            return new Signature(Bytes.FromHexString(signed));
        }

        public bool UnlockAccount(Address address, SecureString passphrase, TimeSpan? timeSpan = null)
        {
            ThrowNotSupportedException();
            return false;
        }

        [DoesNotReturn]
        [StackTraceHidden]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ThrowInvalidOperationSignFailed() =>
            throw new InvalidOperationException("Remote signer failed to sign the request.");

        [DoesNotReturn]
        [StackTraceHidden]
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
