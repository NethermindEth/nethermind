// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test;

/// <summary>
/// Tests for cross-block access list state leaking bug.
///
/// Bug: When processing multiple blocks, BlockAccessList.ResetBlockAccessIndex()
/// only resets Index and clears _changes, but does NOT clear _accountChanges.
/// This causes addresses accessed in earlier blocks to leak into later blocks' access lists.
///
/// Root cause:
/// - BlockProcessor.cs:137 calls ResetBlockAccessIndex()
/// - BlockAccessList.cs:59-63 ResetBlockAccessIndex() doesn't clear _accountChanges
/// </summary>
[TestFixture]
public class BlockAccessListCrossBlockLeakTests
{
    // RIPEMD-160 precompile address (the one from the bug report)
    private static readonly Address Ripemd160Precompile = new("0x0000000000000000000000000000000000000003");

    // Identity precompile address
    private static readonly Address IdentityPrecompile = new("0x0000000000000000000000000000000000000004");

    /// <summary>
    /// This test directly demonstrates the bug in BlockAccessList.ResetBlockAccessIndex().
    ///
    /// After calling ResetBlockAccessIndex(), the _accountChanges dictionary should be cleared,
    /// but it is not. This causes addresses from previous blocks to leak into subsequent blocks.
    /// </summary>
    [Test]
    public void ResetBlockAccessIndex_should_clear_account_changes()
    {
        BlockAccessList accessList = new();

        // Simulate Block 1: Access RIPEMD-160 precompile (0x03)
        // This adds the precompile address to _accountChanges
        accessList.AddAccountRead(Ripemd160Precompile);

        // Verify Block 1 has the precompile in access list
        var block1Changes = accessList.GetAccountChanges(Ripemd160Precompile);
        Assert.That(block1Changes, Is.Not.Null,
            "Block 1 should have RIPEMD-160 precompile in access list");

        // Simulate starting Block 2: Reset for new block
        // This is what BlockProcessor.cs:137 calls between blocks
        accessList.ResetBlockAccessIndex();

        // BUG: After reset, the precompile should NOT be in the access list
        // But ResetBlockAccessIndex() doesn't clear _accountChanges!
        var block2Changes = accessList.GetAccountChanges(Ripemd160Precompile);

        // This assertion will FAIL due to the bug - the precompile leaks from Block 1
        Assert.That(block2Changes, Is.Null,
            "After ResetBlockAccessIndex(), RIPEMD-160 precompile (0x03) should NOT be in access list. " +
            "BUG: _accountChanges is not cleared in ResetBlockAccessIndex()!");
    }

    /// <summary>
    /// Test that multiple addresses leak across blocks.
    /// </summary>
    [Test]
    public void ResetBlockAccessIndex_should_clear_all_account_changes()
    {
        BlockAccessList accessList = new();

        // Simulate Block 1: Access multiple addresses
        accessList.AddAccountRead(Ripemd160Precompile);
        accessList.AddAccountRead(IdentityPrecompile);
        accessList.AddAccountRead(TestItem.AddressA);

        // Verify all addresses are in Block 1's access list
        Assert.That(accessList.GetAccountChanges(Ripemd160Precompile), Is.Not.Null);
        Assert.That(accessList.GetAccountChanges(IdentityPrecompile), Is.Not.Null);
        Assert.That(accessList.GetAccountChanges(TestItem.AddressA), Is.Not.Null);

        // Reset for Block 2
        accessList.ResetBlockAccessIndex();

        // BUG: All these should be null after reset
        Assert.That(accessList.GetAccountChanges(Ripemd160Precompile), Is.Null,
            "RIPEMD-160 precompile leaked from previous block");
        Assert.That(accessList.GetAccountChanges(IdentityPrecompile), Is.Null,
            "Identity precompile leaked from previous block");
        Assert.That(accessList.GetAccountChanges(TestItem.AddressA), Is.Null,
            "TestItem.AddressA leaked from previous block");
    }

    /// <summary>
    /// Test that balance changes also leak across blocks.
    /// </summary>
    [Test]
    public void ResetBlockAccessIndex_should_clear_balance_changes()
    {
        BlockAccessList accessList = new();

        // Simulate Block 1: Record a balance change
        accessList.AddBalanceChange(TestItem.AddressA, 100, 200);

        // Verify balance change is recorded
        var block1Changes = accessList.GetAccountChanges(TestItem.AddressA);
        Assert.That(block1Changes, Is.Not.Null);

        // Reset for Block 2
        accessList.ResetBlockAccessIndex();

        // BUG: The account should not be in access list after reset
        var block2Changes = accessList.GetAccountChanges(TestItem.AddressA);
        Assert.That(block2Changes, Is.Null,
            "Account with balance change leaked from previous block");
    }

    /// <summary>
    /// Test that storage changes also leak across blocks.
    /// </summary>
    [Test]
    public void ResetBlockAccessIndex_should_clear_storage_changes()
    {
        BlockAccessList accessList = new();

        // Simulate Block 1: Record a storage change
        UInt256 storageIndex = 1;
        byte[] before = new byte[32];
        byte[] after = new byte[32];
        after[31] = 1; // Different value
        accessList.AddStorageChange(TestItem.AddressA, storageIndex, before, after);

        // Verify storage change is recorded
        var block1Changes = accessList.GetAccountChanges(TestItem.AddressA);
        Assert.That(block1Changes, Is.Not.Null);

        // Reset for Block 2
        accessList.ResetBlockAccessIndex();

        // BUG: The account should not be in access list after reset
        var block2Changes = accessList.GetAccountChanges(TestItem.AddressA);
        Assert.That(block2Changes, Is.Null,
            "Account with storage change leaked from previous block");
    }

    /// <summary>
    /// Test the leak persists across multiple resets.
    /// </summary>
    [Test]
    public void Leak_persists_across_multiple_resets()
    {
        BlockAccessList accessList = new();

        // Block 1: Access precompile
        accessList.AddAccountRead(Ripemd160Precompile);
        Assert.That(accessList.GetAccountChanges(Ripemd160Precompile), Is.Not.Null, "Block 1");

        // Reset for Block 2
        accessList.ResetBlockAccessIndex();
        // BUG: Still present after first reset
        Assert.That(accessList.GetAccountChanges(Ripemd160Precompile), Is.Null,
            "Precompile leaked to Block 2");

        // Reset for Block 3
        accessList.ResetBlockAccessIndex();
        // BUG: Still present after second reset
        Assert.That(accessList.GetAccountChanges(Ripemd160Precompile), Is.Null,
            "Precompile leaked to Block 3");

        // Reset for Block 4
        accessList.ResetBlockAccessIndex();
        // BUG: Still present after third reset
        Assert.That(accessList.GetAccountChanges(Ripemd160Precompile), Is.Null,
            "Precompile leaked to Block 4");
    }

    /// <summary>
    /// Verify that Index is correctly reset (this part works).
    /// </summary>
    [Test]
    public void ResetBlockAccessIndex_does_reset_index()
    {
        BlockAccessList accessList = new();

        // Increment index a few times
        accessList.IncrementBlockAccessIndex();
        accessList.IncrementBlockAccessIndex();
        accessList.IncrementBlockAccessIndex();
        Assert.That(accessList.Index, Is.EqualTo(3));

        // Reset should set Index back to 0
        accessList.ResetBlockAccessIndex();
        Assert.That(accessList.Index, Is.EqualTo(0), "Index should be reset to 0");
    }
}
