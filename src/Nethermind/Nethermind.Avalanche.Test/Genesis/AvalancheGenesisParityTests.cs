// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Avalanche.Blocks;
using Nethermind.Avalanche.Parity;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Trie;
using NUnit.Framework;

namespace Nethermind.Avalanche.Test.Genesis;

/// <summary>
/// Reconstructs the real Avalanche mainnet C-Chain <b>genesis</b> (block 0) and verifies that Nethermind
/// reproduces both its state root and its block hash byte-exactly.
/// </summary>
/// <remarks>
/// The mainnet C-Chain genesis (from <c>ava-labs/avalanchego</c> <c>genesis_mainnet.json</c> →
/// <c>cChainGenesis</c>) allocates a single account: <c>0x0100…0000</c>, the native-asset-call precompile stub,
/// with balance 0 and the embedded bytecode. The genesis is encoded with Coreth's 5-field
/// <c>[nonce, balance, root, codeHash, isMultiCoin]</c> account RLP, so the state root only matches if that
/// encoding is reproduced. The genesis header is the 16-field shape with a <b>zero</b> <c>ExtDataHash</c>
/// (a genesis special case — not <c>keccak(0x80)</c>), difficulty 0, and <c>extraData = 0x00</c>.
/// Targets: stateRoot <c>0xd65eb1b8…29cc</c>, block hash <c>0x31ced5b9…96b</c>.
/// </remarks>
public class AvalancheGenesisParityTests
{
    private const string GenesisStateRoot = "0xd65eb1b8604a7aa497d41cd6372663785a5f809a17bd192edb86658ef24e29cc";
    private const string GenesisBlockHash = "0x31ced5b9beb7f8782b014660da0cb18cc409f121f408186886e1ca3e8eeca96b";
    private static readonly Address GenesisAccount = new("0x0100000000000000000000000000000000000000");

    // alloc["0100...0000"].code from the mainnet cChainGenesis.
    private const string GenesisAccountCode =
        "0x7300000000000000000000000000000000000000003014608060405260043610603d5760003560e01c80631e010439146042578063b6510bb314606e575b600080fd5b605c60048036036020811015605657600080fd5b503560b1565b60408051918252519081900360200190f35b818015607957600080fd5b5060af60048036036080811015608e57600080fd5b506001600160a01b03813516906020810135906040810135906060013560b6565b005b30cd90565b836001600160a01b031681836108fc8690811502906040516000604051808303818888878c8acf9550505050505015801560f4573d6000803e3d6000fd5b505050505056fea26469706673582212201eebce970fe3f5cb96bf8ac6ba5f5c133fc2908ae3dcd51082cfee8f583429d064736f6c634300060a0033";

    [Test]
    public void Genesis_state_root_matches_mainnet()
    {
        byte[] code = Bytes.FromHexString(GenesisAccountCode);
        Hash256 codeHash = Keccak.Compute(code);

        AvalancheStateAccount account = new(
            nonce: 0,
            balance: UInt256.Zero,
            storageRoot: Keccak.EmptyTreeHash.BytesToArray(),
            codeHash: codeHash.BytesToArray(),
            isMultiCoin: false);
        byte[] accountRlp = AvalancheStateAccountDecoder.Instance.Encode(account);

        MemDb db = new();
        PatriciaTree tree = new(new RawTrieStore(db).GetTrieStore(null), LimboLogs.Instance);
        tree.Set(Keccak.Compute(GenesisAccount.Bytes).Bytes, accountRlp);
        tree.Commit();

        Assert.That(tree.RootHash, Is.EqualTo(new Hash256(GenesisStateRoot)));
    }

    [Test]
    public void Genesis_block_hash_matches_mainnet()
    {
        AvalancheBlockHeader header = new(
            parentHash: Keccak.Zero,
            unclesHash: Keccak.OfAnEmptySequenceRlp,
            beneficiary: Address.Zero,
            difficulty: UInt256.Zero,
            number: 0,
            gasLimit: 0x5f5e100,
            timestamp: 0,
            extraData: [0x00])
        {
            StateRoot = new Hash256(GenesisStateRoot),
            TxRoot = Keccak.EmptyTreeHash,
            ReceiptsRoot = Keccak.EmptyTreeHash,
            Bloom = Bloom.Empty,
            GasUsed = 0,
            MixHash = Keccak.Zero,
            Nonce = 0,
            // Genesis carries a zero ExtDataHash (not the empty-extData keccak).
            ExtDataHash = Keccak.Zero
        };

        Assert.That(AvalancheHeaderDecoder.Instance.ComputeHash(header), Is.EqualTo(new Hash256(GenesisBlockHash)));
    }
}
