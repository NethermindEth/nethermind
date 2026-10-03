// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Core.Crypto;
using NUnit.Framework;

namespace Nethermind.Core.Test;

public class LeanProofStoreTests
{
    [Test]
    public void One_sender_cannot_exhaust_witness_record_capacity()
    {
        LeanProofStore store = new();
        Address sender = Sender(1);
        int admitted = 0;
        byte[] witness = new byte[6208];
        for (int batch = 0; batch < 64; batch++)
        {
            FrameDependency[] dependencies = Enumerable.Range(batch * 16, 16).Select(Dependency).ToArray();
            store.AddVerified(dependencies, dependencies.Select(_ => witness).ToArray(), null);
            if (store.PinPending(Transaction(dependencies, sender, batch))) admitted++;
        }
        Assert.That(admitted, Is.EqualTo(LeanProofStore.MaxSenderPinnedRecords / 16));
        FrameDependency honest = Dependency(2000);
        store.AddVerified([honest], [witness], null);
        Assert.That(store.PinPending(Transaction([honest], Sender(2), 0)), Is.True);
    }

    [Test]
    public void Record_slots_accommodate_the_default_pool_transaction_capacity()
    {
        LeanProofStore store = new();
        byte[] witness = new byte[6208];
        for (int index = 0; index < 2048; index++)
        {
            FrameDependency dependency = Dependency(index);
            store.AddVerified([dependency], [witness], null);
            Assert.That(store.PinPending(Transaction([dependency], Sender(index + 1), 0)), Is.True, $"transaction {index}");
        }
    }

    [Test]
    public void Shared_records_charge_once_and_release_only_after_the_last_transaction()
    {
        LeanProofStore store = new();
        Address sender = Sender(1);
        FrameDependency[] dependencies = Enumerable.Range(0, LeanProofStore.MaxSenderPinnedRecords).Select(Dependency).ToArray();
        store.AddVerified(dependencies, dependencies.Select(_ => new byte[] { 1 }).ToArray(), null);
        Transaction first = Transaction(dependencies, sender, 0), second = Transaction(dependencies, sender, 1);
        Assert.That(store.PinPending(first), Is.True);
        Assert.That(store.PinPending(second), Is.True);
        FrameDependency extra = Dependency(2000);
        store.AddVerified([extra], [[1]], null);
        Transaction next = Transaction([extra], sender, 2);
        Assert.That(store.PinPending(next), Is.False);
        store.UnpinPending(first.Hash!.ValueHash256);
        Assert.That(store.PinPending(next), Is.False);
        store.UnpinPending(second.Hash!.ValueHash256);
        Assert.That(store.PinPending(next), Is.True);
    }

    private static FrameDependency Dependency(int index) => new(Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute(index.ToString()), default);

    private static Address Sender(int index)
    {
        byte[] bytes = new byte[20];
        BitConverter.TryWriteBytes(bytes.AsSpan(), index);
        return new Address(bytes);
    }

    private static Transaction Transaction(FrameDependency[] dependencies, Address sender, int nonce) => new()
    {
        Type = TxType.FrameTx,
        SenderAddress = sender,
        Hash = new Hash256(ValueKeccak.Compute($"{sender}:{nonce}")),
        Frames = [new(FrameMode.DepVerify, FrameFlags.None, null, 0, 0, Eip8288Dependencies.Serialize(dependencies))]
    };
}
