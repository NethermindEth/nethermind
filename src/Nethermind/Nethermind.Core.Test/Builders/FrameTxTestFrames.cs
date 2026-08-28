// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Int256;

namespace Nethermind.Core.Test.Builders;

/// <summary>Builders for the EIP-8141 frame layouts the frame transaction fixtures are exercised against.</summary>
/// <remarks>Shared so a change to the frame grammar lands in one place rather than in every fixture.</remarks>
public static class FrameTxTestFrames
{
    /// <summary>A validation-prefix gas limit in the range a real prefix uses.</summary>
    public const ulong PrefixFrameGas = 100_000;

    public static Transaction FrameTx(params TxFrame[] frames) => FrameTx(TestItem.AddressA, [], frames);

    public static Transaction FrameTx(Address sender, TxFrameSignature[] signatures, params TxFrame[] frames) => new()
    {
        Type = TxType.FrameTx,
        SenderAddress = sender,
        Frames = frames,
        FrameSignatures = signatures,
    };

    /// <summary>A secp256k1 entry of the right shape, carrying placeholder signature bytes.</summary>
    public static TxFrameSignature Secp256k1Signature(Address signer) =>
        new(TxFrameSignature.SchemeSecp256k1, signer, default, new byte[TxFrameSignature.Secp256k1SignatureLength]);

    public static TxFrame SelfVerify(ulong gasLimit = 1_000) =>
        new(TxFrame.ModeVerify, TxFrame.ApproveExecutionAndPayment, target: null, gasLimit, UInt256.Zero, default);

    public static TxFrame OnlyVerify(ulong gasLimit = 1_000) =>
        new(TxFrame.ModeVerify, TxFrame.ApproveExecution, target: null, gasLimit, UInt256.Zero, default);

    public static TxFrame Pay(ulong gasLimit = 1_000) => Pay(TestItem.AddressC, gasLimit);

    /// <remarks>A null <paramref name="target"/> is the omitted-target encoding, not a missing argument.</remarks>
    public static TxFrame Pay(Address? target, ulong gasLimit = 1_000) =>
        new(TxFrame.ModeVerify, TxFrame.ApprovePayment, target, gasLimit, UInt256.Zero, default);

    public static TxFrame Deploy(ulong gasLimit = 1_000) =>
        new(TxFrame.ModeDefault, TxFrame.ApproveScopeNone, TestItem.AddressD, gasLimit, UInt256.Zero, default);

    public static TxFrame Execution(ulong gasLimit = 1_000) =>
        new(TxFrame.ModeSender, TxFrame.ApproveScopeNone, TestItem.AddressB, gasLimit, UInt256.Zero, default);

    public static TxFrame PostTx(ulong gasLimit = 1_000) =>
        new(TxFrame.ModePostTx, TxFrame.ApproveScopeNone, TestItem.AddressB, gasLimit, UInt256.Zero, default);

    /// <remarks>Approving flags on a DEFAULT frame make the layout unrecognized rather than ending the prefix.</remarks>
    public static TxFrame ApprovingDefault(ulong gasLimit = 1_000) =>
        new(TxFrame.ModeDefault, TxFrame.ApproveExecutionAndPayment, target: null, gasLimit, UInt256.Zero, default);

    public static TxFrame Expiry(ulong gasLimit = 30_000) => ExpiryAt(deadline: 0, gasLimit);

    public static TxFrame ExpiryAt(ulong deadline, ulong gasLimit = 30_000)
    {
        byte[] data = new byte[Eip8141Constants.ExpiryDataLength];
        BinaryPrimitives.WriteUInt64BigEndian(data, deadline);
        return new TxFrame(TxFrame.ModeVerify, TxFrame.ApproveScopeNone, Eip8141Constants.ExpiryVerifierAddress, gasLimit, UInt256.Zero, data);
    }
}
