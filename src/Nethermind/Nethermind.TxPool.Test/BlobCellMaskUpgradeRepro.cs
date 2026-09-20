// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.TxPool.Test;

// Reproduces the 1.x -> 2.0.0 upgrade failure seen in smoke run 35518915517 (lane UpgHLv1_35_8).
// Pre-#11094 nodes wrote the processed-blob-txs entry WITHOUT the trailing cellMask+cells fields.
// Encoding with InMempoolForm alone yields exactly that layout, because EncodeShardBlobNetworkWrapper
// only appends cellMask+cells when RlpBehaviors.Storage is set.
public class BlobCellMaskUpgradeRepro
{
    private static readonly TxDecoder Decoder = TxDecoder.Instance;

    private static Transaction[] BuildBlobTxs(int count)
    {
        EthereumEcdsa ecdsa = new(BlockchainIds.Mainnet);
        Transaction[] txs = new Transaction[count];
        for (int i = 0; i < count; i++)
        {
            txs[i] = Build.A.Transaction
                .WithShardBlobTxTypeAndFields()
                .WithMaxFeePerGas(1.GWei)
                .WithMaxPriorityFeePerGas(1.GWei)
                .WithNonce((ulong)i)
                .SignedAndResolved(ecdsa, TestItem.PrivateKeys[i]).TestObject;
        }
        return txs;
    }

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    public void Old_format_block_entry_decodes_under_2_0_0(int txCount)
    {
        Transaction[] txs = BuildBlobTxs(txCount);

        // What 1.35.8 persisted: no cellMask, no cells.
        byte[] bytes = Decoder.Encode(txs, RlpBehaviors.InMempoolForm).Bytes;

        // What 2.0.0's TryGetBlobTransactionsFromBlock does.
        RlpReader ctx = new(bytes);
        Transaction[] decoded = Decoder.DecodeArray(ref ctx, RlpBehaviors.InMempoolForm | RlpBehaviors.Storage);

        Assert.That(decoded, Has.Length.EqualTo(txCount));
    }

    [TestCase(1)]
    [TestCase(2)]
    public void New_format_block_entry_roundtrips(int txCount)
    {
        Transaction[] txs = BuildBlobTxs(txCount);

        byte[] bytes = Decoder.Encode(txs, RlpBehaviors.InMempoolForm | RlpBehaviors.Storage).Bytes;
        RlpReader ctx = new(bytes);
        Transaction[] decoded = Decoder.DecodeArray(ref ctx, RlpBehaviors.InMempoolForm | RlpBehaviors.Storage);

        Assert.That(decoded, Has.Length.EqualTo(txCount));
    }

    // Rollback direction: 1.x never reads cellMask/cells, so it stops short of the wrapper end and
    // trips Check(networkWrapperCheck). Decoding without RlpBehaviors.Storage reproduces that.
    [TestCase(1)]
    [TestCase(2)]
    public void New_format_block_entry_read_by_old_node(int txCount)
    {
        Transaction[] txs = BuildBlobTxs(txCount);

        byte[] bytes = Decoder.Encode(txs, RlpBehaviors.InMempoolForm | RlpBehaviors.Storage).Bytes;

        // Documents a known, unfixable-from-here limitation: the 1.x binary is already released.
        // Bounded the same way as the upgrade direction -- the finalized-block cleaner drops these
        // entries, so the exposure is the unfinalized tip only.
        Assert.Throws<RlpException>(() =>
        {
            RlpReader ctx = new(bytes);
            Decoder.DecodeArray(ref ctx, RlpBehaviors.InMempoolForm);
        });
    }
}
