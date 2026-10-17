// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;

namespace Nethermind.ExternalSigner.Plugin;

public class ClefSigner : ISigner, ISignerStore
{
    private readonly ClefWallet _clefWallet;

    private ClefSigner(ClefWallet clefWallet, Address author)
    {
        Address = author;
        _clefWallet = clefWallet;
    }

    public static ClefSigner Create(ClefWallet clefWallet, Address? blockAuthorAccount = null) =>
        new(clefWallet, GetSignerAddress(clefWallet, blockAuthorAccount));

    public Address Address { get; }

    public bool CanSign => true;

    public PrivateKey Key => throw new InvalidOperationException("Cannot get private keys from remote signer.");

    /// <summary>
    /// Clef will not sign data directly, but will parse and sign data in the format:
    /// keccak256("\x19Ethereum Signed Message:\n${message length}${message}")
    /// </summary>
    public bool TrySign(in ValueHash256 message, [NotNullWhen(true)] out Signature signature)
        => _clefWallet.TrySign(in message, Address, out signature);

    public bool TrySign(Transaction tx) =>
        throw new NotImplementedException("Remote signing of transactions is not supported.");

    private static Address GetSignerAddress(ClefWallet clefWallet, Address? blockAuthorAccount)
    {
        Address[] accounts = clefWallet.GetAccounts();
        if (accounts.Length == 0) throw new InvalidOperationException("Remote signer has not been configured with any signers.");
        return blockAuthorAccount is not null
            ? accounts.Any(a => a == blockAuthorAccount)
                ? blockAuthorAccount
                : throw new InvalidOperationException($"Remote signer cannot sign for {blockAuthorAccount}.")
            : accounts[0];
    }

    public void SetSigner(PrivateKey key) => ThrowInvalidOperationSetSigner();

    public void SetSigner(IProtectedPrivateKey key) => ThrowInvalidOperationSetSigner();

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ThrowInvalidOperationSetSigner() =>
        throw new InvalidOperationException("Cannot set a signer when using a remote signer.");
}
