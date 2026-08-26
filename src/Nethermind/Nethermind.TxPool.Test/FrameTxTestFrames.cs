// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;

namespace Nethermind.TxPool.Test;

/// <summary>Builders for the EIP-8141 frame layouts the pool filters are exercised against.</summary>
/// <remarks>Shared so a change to the frame grammar lands in one place rather than in every filter fixture.</remarks>
internal static class FrameTxTestFrames
{
    public static Transaction FrameTx(params TxFrame[] frames) => new()
    {
        Type = TxType.FrameTx,
        SenderAddress = TestItem.AddressA,
        Frames = frames,
        FrameSignatures = [],
    };

    public static TxFrame SelfVerify(ulong gasLimit = 1_000) =>
        new(TxFrame.ModeVerify, TxFrame.ApproveExecutionAndPayment, target: null, gasLimit, UInt256.Zero, default);

    public static TxFrame OnlyVerify(ulong gasLimit = 1_000) =>
        new(TxFrame.ModeVerify, TxFrame.ApproveExecution, target: null, gasLimit, UInt256.Zero, default);

    public static TxFrame Pay(ulong gasLimit = 1_000) =>
        new(TxFrame.ModeVerify, TxFrame.ApprovePayment, TestItem.AddressC, gasLimit, UInt256.Zero, default);

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
