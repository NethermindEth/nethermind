// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Linq;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Serialization.Ssz;
using NUnit.Framework;
using Withdrawal = Nethermind.BeaconChain.Types.Withdrawal;

namespace Nethermind.BeaconChain.Test.Types;

/// <summary>
/// SSZ round trip and hash-tree-root coverage for the Gloas containers in <c>GloasContainers.cs</c>
/// and <c>BeaconStateGloas</c>.
/// </summary>
/// <remarks>
/// Expected roots for the seven plain (non-progressive) container types below, and for the all-default
/// <see cref="ExecutionPayloadBid"/> and <see cref="BeaconStateGloas"/> values, were computed
/// independently with ethereum/ssz-specs (`pip install git+https://github.com/ethereum/ssz-specs.git`,
/// installed 2026-09-19; package version 0.1.0 declared in its own pyproject.toml) - the same reference
/// SSZ implementation the consensus-specs test-vector generator itself now uses (see that repo's
/// tooling-swap note in this task's Gloas survey). Field types and order there were copied from the
/// spec class definitions cited on each type in GloasContainers.cs / BeaconState.cs, not from this
/// codebase's own encoder, so a mismatch here would be a genuine divergence, not a tautology. The
/// scripts used are not checked in; re-derive them from the field lists cited in the type docs if this
/// ever needs re-verifying.
/// <para/>
/// <see cref="ExecutionPayloadEnvelope"/>, <see cref="SignedExecutionPayloadEnvelope"/>,
/// <see cref="PayloadAttestation"/>, <see cref="IndexedPayloadAttestation"/> and
/// <see cref="SignedExecutionPayloadBid"/> are each checked against one consensus-spec-tests
/// v1.7.0-alpha.13 mainnet <c>ssz_static</c> vector (value.yaml and roots.yaml lifted verbatim, the
/// smallest of the five random cases). <see cref="BeaconBlockBodyGloas"/> gets round-trip coverage only
/// (decode(encode(x)) reproduces x's own root): its smallest vector is 27 KB of random operations, so
/// the fixture-driven <c>SszStaticTests</c> carries its oracle instead.
/// </remarks>
public class GloasContainerTests
{
    [Test]
    public void Builder_hash_tree_root_matches_an_independently_computed_value()
    {
        Builder builder = new()
        {
            Pubkey = Pubkey(0xA0),
            Version = 0,
            ExecutionAddress = new Address(Filled(Address.Size, 0xA1)),
            Balance = 32_000_000_000,
            DepositEpoch = 500_000,
            WithdrawableEpoch = ulong.MaxValue,
        };

        AssertRoundTripsAndMatchesRoot(builder, "0x85b4caa9007afd4605ad383f45294c3efb78ae4ce35348da60ead199ae166463");
    }

    [Test]
    public void BuilderPendingWithdrawal_hash_tree_root_matches_an_independently_computed_value()
    {
        BuilderPendingWithdrawal withdrawal = new()
        {
            FeeRecipient = new Address(Filled(Address.Size, 0xB0)),
            Amount = 1_000_000_000,
            BuilderIndex = 7,
        };

        AssertRoundTripsAndMatchesRoot(withdrawal, "0x807a2c0b918f9fdeaa0d8f84cc7b87aa441c8091c7b07abe50f81fc0a99bfbd6");
    }

    [Test]
    public void BuilderPendingPayment_hash_tree_root_matches_an_independently_computed_value()
    {
        BuilderPendingPayment payment = new()
        {
            Weight = 123_456,
            Withdrawal = new BuilderPendingWithdrawal
            {
                FeeRecipient = new Address(Filled(Address.Size, 0xB0)),
                Amount = 1_000_000_000,
                BuilderIndex = 7,
            },
            ProposerIndex = 42,
        };

        AssertRoundTripsAndMatchesRoot(payment, "0xfea1afeaba53f8ec3224a442a92ff2963fca55f27377fb0d3bfbc4d876c09469");
    }

    [Test]
    public void BuilderDepositRequest_hash_tree_root_matches_an_independently_computed_value()
    {
        BuilderDepositRequest request = new()
        {
            Pubkey = Pubkey(0xC0),
            WithdrawalCredentials = Hash(0xC1),
            Amount = 32_000_000_000,
            Signature = Signature(0xC2),
        };

        AssertRoundTripsAndMatchesRoot(request, "0x9583b5073b93f6c43168ee4d30fb805338b6616184f318d9b5acd291ccccb4e5");
    }

    [Test]
    public void BuilderExitRequest_hash_tree_root_matches_an_independently_computed_value()
    {
        BuilderExitRequest request = new()
        {
            SourceAddress = new Address(Filled(Address.Size, 0xD0)),
            Pubkey = Pubkey(0xD1),
        };

        AssertRoundTripsAndMatchesRoot(request, "0xff55245250859ea9c1cc7a15a196a9bc37b1ee348fb71b6ae1c51d40eaa1feb0");
    }

    [Test]
    public void PayloadAttestationData_hash_tree_root_matches_an_independently_computed_value()
    {
        PayloadAttestationData data = new()
        {
            BeaconBlockRoot = Hash(0xE0),
            Slot = 11_649_024,
            PayloadPresent = true,
            BlobDataAvailable = false,
        };

        AssertRoundTripsAndMatchesRoot(data, "0xfadac1fabeb7d8313dc7cd07feb19348bda5a2d662454db338c7043cdc4e5226");
    }

    [Test]
    public void PayloadAttestationMessage_hash_tree_root_matches_an_independently_computed_value()
    {
        PayloadAttestationMessage message = new()
        {
            ValidatorIndex = 17,
            Data = new PayloadAttestationData
            {
                BeaconBlockRoot = Hash(0xE0),
                Slot = 11_649_024,
                PayloadPresent = true,
                BlobDataAvailable = false,
            },
            Signature = Signature(0xE1),
        };

        AssertRoundTripsAndMatchesRoot(message, "0xdddbeec88a8d1479ff53e12e675553f0bedbef6a503f8d43e1ce51f810e74c96");
    }

    /// <summary>
    /// The all-default (every field zero/empty) bid: the simplest instance whose root the progressive
    /// merkleization (active_fields-bitvector mix-in) can be checked against, the same way
    /// <c>Zeroed_checkpoint_hash_tree_root_matches_spec_value</c> checks a plain container.
    /// </summary>
    [Test]
    public void ExecutionPayloadBid_all_default_hash_tree_root_matches_an_independently_computed_value() =>
        AssertRoundTripsAndMatchesRoot(new ExecutionPayloadBid(), "0x83b932ee5875c06aa35328e3c3e3c976c703f2f4b1bc98e32991ceabbb2e4b63");

    [Test]
    public void ExecutionPayloadBid_with_values_round_trips_with_a_stable_root()
    {
        ExecutionPayloadBid bid = new()
        {
            ParentBlockHash = Hash(0x01),
            ParentBlockRoot = Hash(0x02),
            BlockHash = Hash(0x03),
            PrevRandao = Hash(0x04),
            FeeRecipient = new Address(Filled(Address.Size, 0x05)),
            GasLimit = 36_000_000,
            BuilderIndex = 3,
            Slot = 11_649_024,
            Value = 32_000_000_000,
            ExecutionPayment = 1_000_000_000,
            BlobKzgCommitments = [SszKzgCommitmentOf(0x06)],
            ExecutionRequestsRoot = Hash(0x07),
        };

        byte[] encoded = ExecutionPayloadBid.Encode(bid);
        ExecutionPayloadBid.Decode(encoded, out ExecutionPayloadBid decoded);
        Assert.That(ExecutionPayloadBid.Encode(decoded), Is.EqualTo(encoded));
        ExecutionPayloadBid.Merkleize(bid, out UInt256 root);
        ExecutionPayloadBid.Merkleize(decoded, out UInt256 decodedRoot);
        Assert.Multiple(() =>
        {
            Assert.That(decodedRoot, Is.EqualTo(root));
            Assert.That(root, Is.Not.EqualTo(UInt256.Zero));
            Assert.That(decoded.BlobKzgCommitments, Has.Length.EqualTo(1));
        });
    }

    /// <summary>consensus-spec-tests v1.7.0-alpha.13 mainnet/gloas/ssz_static/SignedExecutionPayloadBid/ssz_random/case_3.</summary>
    [Test]
    public void SignedExecutionPayloadBid_hash_tree_root_matches_the_consensus_spec_fixture()
    {
        SignedExecutionPayloadBid value = new()
        {
            Message = new ExecutionPayloadBid
            {
                ParentBlockHash = new Hash256("0x35ccf7212fa6cae8427d05c7ba1388cfe5724b9b5026611559c369b0006aa10c"),
                ParentBlockRoot = new Hash256("0xab4714c9a922e335854eae6ed680a9ffe8cb50c92e1a5e8863598d933661c383"),
                BlockHash = new Hash256("0x1a8261fe848185ee432ff1a3088e4a14c8e3e4c5b090a00cbb9272d8b0bccb0f"),
                PrevRandao = new Hash256("0xf133bb6bb7871acbace6c1b90ec23f7e3a8cdec922e1838b7b8b64c3b5c5e0b3"),
                FeeRecipient = new Address("0x6ed6751efacfdf0816df58435e4e8647780102c1"),
                GasLimit = 14394590020888259302,
                BuilderIndex = 9487550301025249382,
                Slot = 17669061841876296338,
                Value = 7049766292789055864,
                ExecutionPayment = 12868397698568420971,
                BlobKzgCommitments = [Kzg("0xb9b2e67eb81671791896462589a5d5be81b3dff097267233bffa52b3c04e39d668d0a14e732fb819b1f8aef5481c59de")],
                ExecutionRequestsRoot = new Hash256("0x0b0d044ccb78c3030383dee8223afdd4d7a042a904448d5a0d47e40e45544eaa"),
            },
            Signature = new BlsSignature(Hex("0x3721519cc85dd3156d6d91c9e8acc7146866646ccb288c3bb738afe094c2e0ebbc4e3f30cb77ea2a62ed813b84bd0f44c15bd0fd45c049c135ae99c511b3e5e71ce7d7a8940b592a42e1890b2de1d78c958f73381b4f38b94ab58508b4951003")),
        };

        AssertRoundTripsAndMatchesRoot(value, "0x0e731d14cea238f3dca8c9b1da6a20a6fcef9b4c37a5a076197e4425d9b8c3e3");
    }

    /// <summary>consensus-spec-tests v1.7.0-alpha.13 mainnet/gloas/ssz_static/ExecutionPayloadEnvelope/ssz_random/case_4.</summary>
    [Test]
    public void ExecutionPayloadEnvelope_hash_tree_root_matches_the_consensus_spec_fixture()
    {
        ExecutionPayloadEnvelope value = new()
        {
            Payload = new ExecutionPayloadGloas
            {
                ParentHash = new Hash256("0x31a932a4571b8e7964c35430256c209f4d936b8b9db5d85e4b2d1b90e49177e8"),
                FeeRecipient = new Address("0x1bdca786a6cfd47bfca7f653db91072ea05558aa"),
                StateRoot = new Hash256("0x7dbb37ea0df565aef7ec9f90a8d38799fb2de5fbf8981d8be947dfb183cb6aae"),
                ReceiptsRoot = new Hash256("0x20c3e894e35f96769d11784e6aef8cdb20382c1e268c0bf98312d095be54e458"),
                LogsBloom = new Bloom(Hex("0x2bbea83d8bddad0ed50255ea958e0b54074370215c3c94222bb6f0d90202ece891f7ad0bcd26e9c7c9680b59e2ee22e659cee5166cb40736c7e33eac2f3fe3d447a755db41de618c35f992972f95ad7aad748412053fba32d4cfd309e6c589bf5f35a5857351fb05749bb8f21c95b502eb5fa4631b03f439f06cb5a1188f299ad961c14396630a75f1999b96e8b2da9ab31cc8795bd739c145d4d0505ebc42c90ff88743bb771690f1702481d43cd6a903e4d0bb23b775f064e8537022b79f91b0cf673e4f09db3906fef82d9d77af4342aea7dff0d81a08c2b666e2bb069bf5876a2c1470a1babf4a59109a81a5a8e79d229a7c7ba28e197589b52430c0ef18")),
                PrevRandao = new Hash256("0x60faf2f0e5d42e6ccc13b8ece8002a495481ac372161235d447b7088dc63da63"),
                BlockNumber = 11844059669554978471,
                GasLimit = 11607111753853050417,
                GasUsed = 15223481975194046184,
                Timestamp = 8323845977182749485,
                ExtraData = Hex("0xd25b"),
                BaseFeePerGas = UInt256.Parse("5122546541140068066138236904250893503207046049189296489217938318351473163201"),
                BlockHash = new Hash256("0x32291d2729427a244dc146b70523431df4245502011ca425942daec38d01be0f"),
                Transactions = [new TransactionGloas { Bytes = Hex("0x") }, new TransactionGloas { Bytes = Hex("0xc0d49e") }],
                Withdrawals =
                [
                    new Withdrawal
                    {
                        Index = 2749991337931910552,
                        ValidatorIndex = 2967223866872365453,
                        Address = new Address("0x22e8f436b1ccaf6d19e6641a8237b978fd57cac7"),
                        Amount = 8034153501360007281,
                    },
                ],
                BlobGasUsed = 2514183265171073374,
                ExcessBlobGas = 10736203297808159658,
                BlockAccessList = Hex("0x45fa"),
                SlotNumber = 10041495827257853792,
            },
            ExecutionRequests = new ExecutionRequestsGloas
            {
                Deposits =
                [
                    new DepositRequest
                    {
                        Pubkey = new BlsPublicKey(Hex("0xa498a07d8b1dfadf773f9e4c470dad8fa58b90eb4b7d7c71b3c54cfed4fa577602564f19c0044bf9bbb81c10789c693e")),
                        WithdrawalCredentials = new Hash256("0x146f02da69567a72edc1c4a765cba69fbb23382b643a8ad99282da9f25bb8f97"),
                        Amount = 68903601517847518,
                        Signature = new BlsSignature(Hex("0x1318cea3b0a6f6ab9737975b350bfa201166de7debf13365d0a882b5ca9fb7e5b0cd1de0bc973d9c991192782d68dcff04d15fbafbae88a5de6794bf51ed604c8c31b26307803ab26e57b1ea430eb37f588371201914033bcd031ae9df03ff81")),
                        Index = 5961324226377045318,
                    },
                    new DepositRequest
                    {
                        Pubkey = new BlsPublicKey(Hex("0x8b64d5f7c85dd99aeda3e0091d26f989fe11df6fe284d95083fc059e9afa507594b4fc0515d1c5dacf102ad719454dd0")),
                        WithdrawalCredentials = new Hash256("0xeb079d5dd338175453c9878e2892d1bd3bfa7b9e4aeed7d93c8b3d3f1a47c815"),
                        Amount = 8269325962171674000,
                        Signature = new BlsSignature(Hex("0x440d3f5a11242d10b6613bb1a35e72537c44fe1d3773a78a67d66df073e00b79a1768ea0d1299d93a443052c717e271e63ad3129275a48e2b0e71e642517b233081e266b769895a9ad051710978a34ee9916a29af61f804de28692d39e982937")),
                        Index = 16273029228612312729,
                    },
                    new DepositRequest
                    {
                        Pubkey = new BlsPublicKey(Hex("0x0e7b37161478d5c7ba1ff15e08ce13d15695443ac2f355297b8fffa7ce938df19191557a31ca929d17c537feada865d5")),
                        WithdrawalCredentials = new Hash256("0x5a043a1a2ac2e14f4b877a6e0cfba070b2b4379850370dd00af5d487e6cbaea3"),
                        Amount = 4049541586333022516,
                        Signature = new BlsSignature(Hex("0x1196e36168a597b9da970c8d7c32d7a12cb815e5340817627dde5440c7b47952a4bdecd82f045193aeff0d9886e1686cd3ebd4c520fc44e98d1df535d1c27748e1a104ed43a88cfa3d184b814164f6561411294248847fa35b902332b8a88570")),
                        Index = 6857281540983469260,
                    },
                    new DepositRequest
                    {
                        Pubkey = new BlsPublicKey(Hex("0xf899bd838db55c18c8d8a788903a0b3f6787ce1b9cee04d04fab7e854bec468ef0761963267640a4fc56130287e895c8")),
                        WithdrawalCredentials = new Hash256("0x9f760011e96a52aa47ad141f0935f48c7cf10122b0575d2df62a1b0c226287d3"),
                        Amount = 9939444393519949915,
                        Signature = new BlsSignature(Hex("0xf79d5b69d4928a68315103b53e0a4c82a136cb0ec406b0c46b6bd9fb79871bfbdf03ef4e299385430ebfa6fdf1d2fe804ff782d713eb3fb93244e76650be1587751d6b85f432df2a5d0be259a3be993a2fd6ccba8a80e976ce8643dd50d56e52")),
                        Index = 12106330420358823252,
                    },
                ],
                Withdrawals =
                [
                    new WithdrawalRequest
                    {
                        SourceAddress = new Address("0xabf9aef2866f10e243b1f48487080e9cc5a55de4"),
                        ValidatorPubkey = new BlsPublicKey(Hex("0x52208c71fd7c1953e1ef7e8cb4acd6818e2276888fbca14ed47627a3dd41356a9212c68055df3cd9d466e68d20a58bbb")),
                        Amount = 454436198694798676,
                    },
                    new WithdrawalRequest
                    {
                        SourceAddress = new Address("0x71b6555608f9b866fd868c218b0f5cfcbcbc3367"),
                        ValidatorPubkey = new BlsPublicKey(Hex("0x1481e35a8b724384043df189e0e8e383cd1c252b5845fd76936ff9eb38c635dbb0cacf008ff63778cea5bf4329291b80")),
                        Amount = 16572679128381571468,
                    },
                ],
                Consolidations =
                [
                    new ConsolidationRequest
                    {
                        SourceAddress = new Address("0x425f392afc83bae99cdf5137a26573040047d6d1"),
                        SourcePubkey = new BlsPublicKey(Hex("0x0801e76120dee7522f3e41170e388c1c2cc19a4c9ce31f8a119ff8876a59768de5a36d4023d1cbccd266dcdfa408e3d0")),
                        TargetPubkey = new BlsPublicKey(Hex("0xa6195dc307d1853ba0c1ad1ff17d739229c0e80a0d5ce397a656cd62a7244e70795e19b7f79113c2766bd1d692792b57")),
                    },
                    new ConsolidationRequest
                    {
                        SourceAddress = new Address("0xab74dcbbea4f2e1ff5d3fa4ff8987c32caba4da4"),
                        SourcePubkey = new BlsPublicKey(Hex("0xbdbc1444eda4318d7ea4eb6f617a624a5866e094be7608338daeee9a9d22648ae2309a049b8e8701930cb6e41c2b8803")),
                        TargetPubkey = new BlsPublicKey(Hex("0x4c880c56f6743262ee458993a057dda45c2f2589d42c81adc91ba4b26348956dd68cd9d033246c44bb8060e46ad36100")),
                    },
                    new ConsolidationRequest
                    {
                        SourceAddress = new Address("0x87e6442d13c52720594cb88076b7f15ac6da8e11"),
                        SourcePubkey = new BlsPublicKey(Hex("0x018dd6228dfa79795cfe2bdbef4abd6df948df538b68ed79721d77eb6890e4b82be3a95129567b0cfc9f23d03f3b0b71")),
                        TargetPubkey = new BlsPublicKey(Hex("0x725694bd7827863201561208640f2e532b889c8af462f91b5b319ce0e083e0878172df6958da3fd45a99b064a6c2b3cd")),
                    },
                    new ConsolidationRequest
                    {
                        SourceAddress = new Address("0x918962b701b272401a92221ab3dc18c24ec67136"),
                        SourcePubkey = new BlsPublicKey(Hex("0x455b43525f6d910eb85200d63d0de2f99f3016ed76821b10d8145eda1fd9c93c2fc2ff940f3e62790ec9b5f84ed0bac5")),
                        TargetPubkey = new BlsPublicKey(Hex("0x12e339fc0f9f100850794cd42bac020b28f7403beea9f1455403f9077161fb892bf937a8c39740b48089a1d740b5e345")),
                    },
                    new ConsolidationRequest
                    {
                        SourceAddress = new Address("0x21a77f04554304cbb6e071e72729ad35c27adf2d"),
                        SourcePubkey = new BlsPublicKey(Hex("0x53d7818329bd1496461ae17a5ff1c1dba0598fb598b819ae1f6e22183a73068c871f9340a66f6236a255fb2ec2f999b4")),
                        TargetPubkey = new BlsPublicKey(Hex("0x7562f68e0bf38d660b75db0205709be77ee0199cda7820d2711217ba754e5c71c7dbea36ecff5318894267ab9c556cb0")),
                    },
                    new ConsolidationRequest
                    {
                        SourceAddress = new Address("0xb8e6b172119bda1a7b10e0f4c3e892cd77064988"),
                        SourcePubkey = new BlsPublicKey(Hex("0x55c1e2f50452ec3e1d1cdc71f42fad135df5cda0270de54b0b9769294528878738b9e794b7970703b0ccce3e4e20dce9")),
                        TargetPubkey = new BlsPublicKey(Hex("0x71f0506e58cee2e35de1bff91e1f075e343f9f4471466d271a5277c53e36936cfbc231392bd4746ba35def3f3774d020")),
                    },
                ],
                BuilderDeposits =
                [
                    new BuilderDepositRequest
                    {
                        Pubkey = new BlsPublicKey(Hex("0x70a6b03d750f2b420d4557a32cd39ad68bc4cf03a64f10ce3db310a04a1a5b12f9e54ee792aabf0c1c1b32ce22d52348")),
                        WithdrawalCredentials = new Hash256("0xb9eaf48e7280f0dd277df006fb904f8f04c36e2acb7a1e8bf2c41017157e92fe"),
                        Amount = 4440340826193967980,
                        Signature = new BlsSignature(Hex("0x8b52bede6cbda2c220dc2e89973ef39961f6370ae3ad065bb195f8112d96d25ee5470548cc897072e0cea050267377a4a87b8911d5e97e25abeeefd73eb1010a7508cf4edde0df4844e34b09879662d349dec34c06af709d82bd9e6cb369ac2f")),
                    },
                    new BuilderDepositRequest
                    {
                        Pubkey = new BlsPublicKey(Hex("0x867bad4b884b99731cbeb36c94a0aa805daa190fff417e1574496ea0f44f17417544c3eb645f1c1a091ca5ad0873a769")),
                        WithdrawalCredentials = new Hash256("0xaa45fbe760d99dae0cc47b5de89df07ce64975d87a520858b8a942b48fa5ec38"),
                        Amount = 16600549436633089148,
                        Signature = new BlsSignature(Hex("0x0ce407e2971dbcb4e4e9a0ba716a51c0403b6a7e767a8d7ff64a726360fdae258defc792574e7682725a18b1ba324bb8ca9965ae7b75044b677461e2b1bcb56181232f26fdc449190ffd5f626c7e126ccc12b5aee3295f169246fc1c388ecef6")),
                    },
                    new BuilderDepositRequest
                    {
                        Pubkey = new BlsPublicKey(Hex("0xb1e4518aef5b873aea9083e1776d0d37fdfb2fd9e962776690cad4d60bd6a07eda6aefdaa766543b9a546f9a32d099b0")),
                        WithdrawalCredentials = new Hash256("0x9f5391b2f839f92f3a6d40f8dc80f0d07bdf5f82707ae823ba402c3e7a4d1f2a"),
                        Amount = 12121050456403222950,
                        Signature = new BlsSignature(Hex("0xae14311e2cdcf5e0f5305bf99f1d4e731871ad3680fe5466003c02a6f57d2ae284001a5561c97dadb7f5c0a62252ff4f23cc1ff7d16cfbfbbfb135792b727a4732c1a94d1c5e3fc57336c31cc8582c123debfb8fef5ea50adebefdd73b1e7cd2")),
                    },
                    new BuilderDepositRequest
                    {
                        Pubkey = new BlsPublicKey(Hex("0xbd57b2537beb593508b1591a3905badacb37bfc05cbf5008a68e0c203b38cbae3731bd43552949e80e45c1e465b948fa")),
                        WithdrawalCredentials = new Hash256("0xcf64378fe6ecb60341d1cfe69d4bd207d8b5daaef696170fb29ad4602c58473c"),
                        Amount = 10087698460013816333,
                        Signature = new BlsSignature(Hex("0x8d090d0f79c42e7c1ee51014d8c86a4bfa85135e2e321ef38f856668dc62f2161db9ea828278d50a5118a3b7e4cb00c8ffd1ed3cbb2d42a62dfaa71a6e763acfaa0f5131c7259a23880424f946046c12b27273f526b5e8f57eb33f703fa4ad28")),
                    },
                ],
                BuilderExits =
                [
                    new BuilderExitRequest
                    {
                        SourceAddress = new Address("0xc9dc2aa47ff68d9a3eac67c1aaa55ce32f6eb85a"),
                        Pubkey = new BlsPublicKey(Hex("0xd67a875aa026e39a954b5b7bfc8bb22a330ef4c937326623bd46872b730d689a403962d21cb958a56fe1a077439f4244")),
                    },
                    new BuilderExitRequest
                    {
                        SourceAddress = new Address("0xd97ea7aa3e35dbfa079f3e45b3f9b34490f781c4"),
                        Pubkey = new BlsPublicKey(Hex("0xd3f61e764f2772b7ce90b30da098907837ce29b625f8d6b63c5cc8b056c2be4f0d437429ecb1e02cb2abb3bd4c4e1267")),
                    },
                    new BuilderExitRequest
                    {
                        SourceAddress = new Address("0xfb9d19978528098f039328d794fd5be6c2efbda4"),
                        Pubkey = new BlsPublicKey(Hex("0x075af56634d8a92c6593e86a12496964b23ed38bb4baf176564e9c7edadd923cc2f9985bc22c480da765350051935f80")),
                    },
                    new BuilderExitRequest
                    {
                        SourceAddress = new Address("0x646ade66692c4630c9394e497a602e1b442649be"),
                        Pubkey = new BlsPublicKey(Hex("0xa168528633f92949a9ffea3d8104ed6755c7cb0bb9eddb515cc07fad27b959a4abf30038c2711a0a4c06f85f08ae20b6")),
                    },
                    new BuilderExitRequest
                    {
                        SourceAddress = new Address("0x4a8566a3720e3c5a184b8da96930b402d07ef8e1"),
                        Pubkey = new BlsPublicKey(Hex("0x639b932f9463a7662c3d6806c49dc3395c5ede8361aed33a08cbae9551d8cd7a22c02e2307aa13cc25d7d63e6d875dca")),
                    },
                    new BuilderExitRequest
                    {
                        SourceAddress = new Address("0xc9cb0d73616a6cb0b1bbbc44caa8fc5466d8f186"),
                        Pubkey = new BlsPublicKey(Hex("0x5d14424b6427f03fca26067b9794450456ef2af5db860328a045eb191369a46d87d2815f3aa84f8649e8c1d1c89aefee")),
                    },
                    new BuilderExitRequest
                    {
                        SourceAddress = new Address("0xdd7e7a8fe6df98e1c3c84ed0e8b94dec8cccd9bc"),
                        Pubkey = new BlsPublicKey(Hex("0x399d3170f1315505e0bc01521bb219f27e579a8973406fe66d91d9ded97a70d9fe98d8b33b3e5ebbc2ac922710e865ea")),
                    },
                    new BuilderExitRequest
                    {
                        SourceAddress = new Address("0x7776e95d623a7386f5359b5860821858662cc040"),
                        Pubkey = new BlsPublicKey(Hex("0xafcc4861bb1c5b5ec15bca687aad38679d2a2c0ef19e89c479987125a7c95fc3f00ad01840739379dfd1fe052330657b")),
                    },
                    new BuilderExitRequest
                    {
                        SourceAddress = new Address("0x7dba863f450fea3ee37456e312ff2ad4ab767558"),
                        Pubkey = new BlsPublicKey(Hex("0x1e41e8e35774186af433ab8c8d29cb7d238922848db29eda3b2e363525b2412d062031716e4d98d71657254718191a67")),
                    },
                ],
            },
            BuilderIndex = 9310727944472014463,
            BeaconBlockRoot = new Hash256("0x03b86a2368fcfbd43f63fa5ac5cb71066803b185a12e736945db7f6dbc84ed08"),
            ParentBeaconBlockRoot = new Hash256("0xc678f53e073fed6ab5eedc8e231f59168164641203af8e3bcaab74df85da5690"),
        };

        AssertRoundTripsAndMatchesRoot(value, "0xf1e3a366656a523322d0e74a170ed13fe971925a59825c7dfadf70116087843e");
    }

    /// <summary>consensus-spec-tests v1.7.0-alpha.13 mainnet/gloas/ssz_static/SignedExecutionPayloadEnvelope/ssz_random/case_3.</summary>
    [Test]
    public void SignedExecutionPayloadEnvelope_hash_tree_root_matches_the_consensus_spec_fixture()
    {
        SignedExecutionPayloadEnvelope value = new()
        {
            Message = new ExecutionPayloadEnvelope
            {
                Payload = new ExecutionPayloadGloas
                {
                    ParentHash = new Hash256("0xa4837b57c9ecdf53d3b4c433c0d22c89d5633280cf08ef8276fdca5ff09e0679"),
                    FeeRecipient = new Address("0x308f124fbab2e777efc9e3a15e5ce8b5c476ea46"),
                    StateRoot = new Hash256("0x034e9ea9743a90cb75626f1b3566397d37e65db3533c7ee9557820553090c326"),
                    ReceiptsRoot = new Hash256("0x2d8d7d2ea76e8cbbf27a1c76be655f2c6b6bc978ab5d5e77046f3a69c45cc4cb"),
                    LogsBloom = new Bloom(Hex("0x67e434413f783a8a24b7b878d0445b37d436abdef12e17a6c41d038a51efe2e039e198ddf7f3181cb456e4fac83c4f62be5e24ead5c203d2b3e99f582c54b4d9570ca87c4b556f171bca36001aeda7db3fd4e9a7a24f0e7996da0e0e26527187806816279c90a29e51f3a855b49f691498869b3133cfb0eb7bd0b40651360199c160c2508736f3b77608f37875e1bb333ec95fae965c9fc356e12a674fd3e0542ac83743e37855521697d8966fe41877a0e3c16fd5f3b970d1ae6b637ea4275054638ba7cf8b28ff7598e51053356487427eccd7f9973e9aaa8d1c74c40ea2fc0b3bfda73d96adc41675008e4a01820345694732aa5205cab0f6ef0f104d3180")),
                    PrevRandao = new Hash256("0xb8c628a3a2804892b96c813f8630dc9e8fea8e9dfecbb1891c6e02431c95c07a"),
                    BlockNumber = 5745732352836372826,
                    GasLimit = 7810025201527898681,
                    GasUsed = 9370927720033782860,
                    Timestamp = 14805465561131419917,
                    ExtraData = Hex("0x3654db7e0523b6f7ad65ca4a5fc47e13b1856a23ebe098c846"),
                    BaseFeePerGas = UInt256.Parse("28521739503957978205664533597995245700787317236276235477074462157605432388607"),
                    BlockHash = new Hash256("0xcbcfa99d43aca55f75ad408fa29e39f42b841fa914b32b2dc10c27c8b3b4d5d1"),
                    Transactions = [new TransactionGloas { Bytes = Hex("0xe1253e") }, new TransactionGloas { Bytes = Hex("0x4b") }, new TransactionGloas { Bytes = Hex("0xca7d09f0") }, new TransactionGloas { Bytes = Hex("0xfd7c") }, new TransactionGloas { Bytes = Hex("0x") }, new TransactionGloas { Bytes = Hex("0xd666") }, new TransactionGloas { Bytes = Hex("0x74") }, new TransactionGloas { Bytes = Hex("0xef") }, new TransactionGloas { Bytes = Hex("0x") }],
                    Withdrawals =
                    [
                        new Withdrawal
                        {
                            Index = 16596734541847302361,
                            ValidatorIndex = 18187399603898432538,
                            Address = new Address("0xe93c179aac5f190fde64f830997a06e2ac2f4fd8"),
                            Amount = 831297959989642701,
                        },
                        new Withdrawal
                        {
                            Index = 17650188899713094075,
                            ValidatorIndex = 7648340068616585150,
                            Address = new Address("0x28052bf74ceb9da3f43bde79595d77d9218a37f4"),
                            Amount = 1044148948808314275,
                        },
                        new Withdrawal
                        {
                            Index = 4627888345002644175,
                            ValidatorIndex = 9615890871386370354,
                            Address = new Address("0x8cfe57039ca11fb7ceae8249c5884a661f993ed3"),
                            Amount = 460716876688515341,
                        },
                        new Withdrawal
                        {
                            Index = 3725168194741340149,
                            ValidatorIndex = 9196050520245943962,
                            Address = new Address("0x4d6b33a732cef73154770eb2ebe8bc9d3bc2a2ec"),
                            Amount = 9263338059477446586,
                        },
                        new Withdrawal
                        {
                            Index = 2714199825714719298,
                            ValidatorIndex = 184659594320681732,
                            Address = new Address("0x9c00e1e7fd6834097ac46625f100f20b467e3a64"),
                            Amount = 14432109890733221940,
                        },
                        new Withdrawal
                        {
                            Index = 16750456847630051888,
                            ValidatorIndex = 10869737283447025547,
                            Address = new Address("0xb51d2c636dbb069e7c07494c345e8efa2b8af2bf"),
                            Amount = 13173878150253712056,
                        },
                        new Withdrawal
                        {
                            Index = 27439287514755126,
                            ValidatorIndex = 12270853296035304605,
                            Address = new Address("0x7ae4a5ea24044e570f4673790fc919b3f7ad6cce"),
                            Amount = 10209743984291912989,
                        },
                        new Withdrawal
                        {
                            Index = 16143678751983981514,
                            ValidatorIndex = 10778554243496457821,
                            Address = new Address("0xa7933c1f9c9fa7b80eaf9f7a2693fc9355aa8a91"),
                            Amount = 9776234624848178339,
                        },
                        new Withdrawal
                        {
                            Index = 8176392928330299672,
                            ValidatorIndex = 8180358748788705370,
                            Address = new Address("0x58f970f35420b5f92ddbcccf8323b4f3dfb0119a"),
                            Amount = 12172091402218580216,
                        },
                    ],
                    BlobGasUsed = 12663424353192368202,
                    ExcessBlobGas = 18060641942178684021,
                    BlockAccessList = Hex("0x"),
                    SlotNumber = 18018224954505590314,
                },
                ExecutionRequests = new ExecutionRequestsGloas
                {
                    Deposits = [],
                    Withdrawals = [],
                    Consolidations =
                    [
                        new ConsolidationRequest
                        {
                            SourceAddress = new Address("0x022e9ba067f2c747968f189c82a9d58d958c948e"),
                            SourcePubkey = new BlsPublicKey(Hex("0x43755f87eb2181ecb665970baaff59e96a2be6724c56b55fc1fd60534903fc0da7a7512416160542f9689ac140315c07")),
                            TargetPubkey = new BlsPublicKey(Hex("0xaf2dcde0c3a2950c35e4811eac88431fe2112aaa289425aba92626e05755cc4d32a3caa229e81e1922d2ee56b09c308e")),
                        },
                        new ConsolidationRequest
                        {
                            SourceAddress = new Address("0xf444f0158f696122889b41effd7deb3457419459"),
                            SourcePubkey = new BlsPublicKey(Hex("0x0971f32c6b61a50628a70bc0208fbc9ce25bd0d9aeab3ab3a3a0553aad1c07484fcb5fa9c67706a8968baad5cb1fa495")),
                            TargetPubkey = new BlsPublicKey(Hex("0x96345353b57ad0a8fb338f814ce0810419a4d8d5b03f00a948db7a6c2c46a1c88d69973825cf007f96f43fa2d2e54101")),
                        },
                        new ConsolidationRequest
                        {
                            SourceAddress = new Address("0x10052e1ddc44cfd08f0be1ada77fa91c8da245c4"),
                            SourcePubkey = new BlsPublicKey(Hex("0xdd2ab6aded632b23d4801e4750df9c27cfed7da4e25e681f534629f0e126769aac7f934937ba6682ebe7e887c2a43492")),
                            TargetPubkey = new BlsPublicKey(Hex("0x8f8fa4308ed296e660eeebd2eb454f7ed952eaed9547070771523f512a677cc9f1d285499a87d35815ab471593481903")),
                        },
                        new ConsolidationRequest
                        {
                            SourceAddress = new Address("0x5a2e1cf0ace608fc2cf7d79bff95900359082f64"),
                            SourcePubkey = new BlsPublicKey(Hex("0xb925bdc41e5f6fee3db19ba533d212cc04ceb7dac5b0b2120ee8823b99e0aa3fc601094ca19c9fa9009137a512a66411")),
                            TargetPubkey = new BlsPublicKey(Hex("0xb3d720cb0ed6cfac13d70779c9ebb90303e90ee01ea1af436bc54e63f4b4e6b62077ebfb62e1ffaeb059d87794228151")),
                        },
                        new ConsolidationRequest
                        {
                            SourceAddress = new Address("0xa2d16cfcae2dd90b343711c249e86d2f05bdb7e9"),
                            SourcePubkey = new BlsPublicKey(Hex("0x7575be7a80c60f51eb430a0d880755a7a64a86bea0b91242af6ffe3027faa7cdf23a8894d2d38e6e69f30cf5b81d14f4")),
                            TargetPubkey = new BlsPublicKey(Hex("0x348b6884271c0c80d72f81e229d9a64eaf5b5557f2187e612da6cbb7f2a955d1dc14d4aaa9ef0e3d87e2ce238ceb97c4")),
                        },
                        new ConsolidationRequest
                        {
                            SourceAddress = new Address("0xe451f338828ab773098a1e33f48797e8f71df193"),
                            SourcePubkey = new BlsPublicKey(Hex("0x5f943e59c7eea16ae9507a5f7fcb224ee728f4e93f221af926ab8511b581573a618d801a654158b6023aee7e754949ab")),
                            TargetPubkey = new BlsPublicKey(Hex("0xc732ce66150b32fc07d9ba0efb6fb3d8375b7cb16bfbc9f085a2a4500e951b48d0b649711d3bd604dd6d18f6220e26b2")),
                        },
                    ],
                    BuilderDeposits = [],
                    BuilderExits =
                    [
                        new BuilderExitRequest
                        {
                            SourceAddress = new Address("0x8048de0501d4767ce6ee4cee607967cfecbb3565"),
                            Pubkey = new BlsPublicKey(Hex("0x13269a8a4e6dba6222d8d3e1c07499f7d7dae9dc71b97956f4f9923af75ccb678dd6b3af5494d452f26f5c3756135f6e")),
                        },
                        new BuilderExitRequest
                        {
                            SourceAddress = new Address("0xab078677986294609d366923f4471a67481b4420"),
                            Pubkey = new BlsPublicKey(Hex("0xe6f66363e3d356c2d12f3db0bc2ac5242e374ad789301549d51a47a70439e7e0e6b467f77e9a20b94ca0460cddf5b752")),
                        },
                        new BuilderExitRequest
                        {
                            SourceAddress = new Address("0x06ab7035b2272688bba2757987463611b84029a2"),
                            Pubkey = new BlsPublicKey(Hex("0xfa88ae0c57f14c270ba4486cff2e1f6a70ff96a5d82ee365db639db24c7def0e7b209257f94776df9fb52d733b281018")),
                        },
                        new BuilderExitRequest
                        {
                            SourceAddress = new Address("0x8e8b974bd465fe42f5b6aa8ec8216bad64d9a7d6"),
                            Pubkey = new BlsPublicKey(Hex("0xb2541fa4b903be9816c273bfdb35ff95a5887de86c82f540e5229b3193a590c1b2e8300634445e1b8c85bac6aed6237b")),
                        },
                    ],
                },
                BuilderIndex = 11366524139550385452,
                BeaconBlockRoot = new Hash256("0x105ea0e015bf92f744e67dc5374e393b273c7c8f59c000de1850a5a17eec2b35"),
                ParentBeaconBlockRoot = new Hash256("0x43ee3e2320ab3ef3606e1ddc50fa091ef4596a4628aaf4ffa4c26bbcc9693f74"),
            },
            Signature = new BlsSignature(Hex("0x03fa770d65406659054ec21e0e9137d3c7311c8988b5503c5207069df1bd55e2ed7d19cb844c3ad343401545d955f460ecd275203f0fab8990a069ea7253b070c89cb0d1af8f9e37f24ac3c27375b421ec912bae086527222f39eed102f22422")),
        };

        AssertRoundTripsAndMatchesRoot(value, "0x73440a71df59ff10cd1c3f9f26d89408471cf8813e01ca946406268cf53e9805");
    }

    /// <summary>consensus-spec-tests v1.7.0-alpha.13 mainnet/gloas/ssz_static/PayloadAttestation/ssz_random/case_2.</summary>
    [Test]
    public void PayloadAttestation_hash_tree_root_matches_the_consensus_spec_fixture()
    {
        PayloadAttestation value = new()
        {
            AggregationBits = new BitArray(Hex("0x5fa348b14aadae87be47aaf123673c7a0db6beeca91e76bbac7b148ceffb0376588c9e8ed02b927c957ef3f224784039ad947965aa74153328ce4594dfc56f59")),
            Data = new PayloadAttestationData
            {
                BeaconBlockRoot = new Hash256("0x9296554c18415977a4c9104aa8f3ef52879a08c5920387350529753e3aa9c14d"),
                Slot = 161061023892723935,
                PayloadPresent = true,
                BlobDataAvailable = true,
            },
            Signature = new BlsSignature(Hex("0xbb63a5749b09506b2d4a694b5ab60daf5c99ff27b39179b0f8f0cc2eeb99867acba2519fdc550e2725ebc450a3bc38939e9875df1f898d854345299f3ca6cc98b3b29fe434384d2e7e9fcf835ca18086f437bdc6a3d4c5dfadbf1dc2cf142cf8")),
        };

        AssertRoundTripsAndMatchesRoot(value, "0xe77c4e19bf83c95dabd72815a4e56b150f177fac2349caa7356416c3989e26ce");
    }

    /// <summary>consensus-spec-tests v1.7.0-alpha.13 mainnet/gloas/ssz_static/IndexedPayloadAttestation/ssz_random/case_4.</summary>
    [Test]
    public void IndexedPayloadAttestation_hash_tree_root_matches_the_consensus_spec_fixture()
    {
        IndexedPayloadAttestation value = new()
        {
            AttestingIndices = [9804698202478740215, 5730887916120935097, 16600878836211186805, 8432851339943480060],
            Data = new PayloadAttestationData
            {
                BeaconBlockRoot = new Hash256("0x0c493a166f2122957decaf535325c1e1cfe2aabb00c2e4327a887eb5d47293e4"),
                Slot = 10829470397981321748,
                PayloadPresent = false,
                BlobDataAvailable = false,
            },
            Signature = new BlsSignature(Hex("0xcc22a49d4815bfe1d7e468c7cb7c883c2544febe049d55f229cfd13f4e03ff13abc8e5370cc28e35391e1dd65b0230b1f33e0ce91ad8a3c6fe787080826d9b17b149278bdcc4cc49f667959aa784ce5871f6d1aac0e9f70aaeb5aac3c382c922")),
        };

        AssertRoundTripsAndMatchesRoot(value, "0x12cc133cdb17b32ac8a9c0a0faaa06b2a49d685ba98c8b929350c3bc7c657f9e");
    }

    [Test]
    public void BeaconBlockBodyGloas_round_trips_with_a_stable_root()
    {
        BeaconBlockBodyGloas body = new()
        {
            RandaoReveal = Signature(0x40),
            Eth1Data = new Eth1Data { DepositRoot = Hash(0x41), DepositCount = 1, BlockHash = Hash(0x42) },
            Graffiti = Hash(0x43),
            ProposerSlashings = [],
            AttesterSlashings = [],
            Attestations = [],
            Deposits = [],
            VoluntaryExits = [],
            SyncAggregate = new SyncAggregate { SyncCommitteeBits = new BitArray(512), SyncCommitteeSignature = Signature(0x44) },
            BlsToExecutionChanges = [],
            SignedExecutionPayloadBid = new SignedExecutionPayloadBid
            {
                Message = new ExecutionPayloadBid
                {
                    ParentBlockHash = Hash(0x45),
                    ParentBlockRoot = Hash(0x46),
                    BlockHash = Hash(0x47),
                    PrevRandao = Hash(0x48),
                    FeeRecipient = new Address(Filled(Address.Size, 0x49)),
                    GasLimit = 36_000_000,
                    BuilderIndex = ulong.MaxValue,
                    Slot = 123,
                    Value = 0,
                    ExecutionPayment = 0,
                    BlobKzgCommitments = [],
                    ExecutionRequestsRoot = Hash(0x4A),
                },
                Signature = Signature(0x4B),
            },
            PayloadAttestations = [],
            ParentExecutionRequests = new ExecutionRequestsGloas(),
        };

        AssertRoundTrips(body, BeaconBlockBodyGloas.Encode, BeaconBlockBodyGloas.Decode, BeaconBlockBodyGloas.Merkleize);
    }

    /// <summary>
    /// The all-default (every field zero/empty/512-of-zero) state: the same "simplest checkable
    /// instance" approach as the bid test above, sized up to a 46-field progressive container with
    /// several large fixed vectors (block_roots, randao_mixes, ptc_window, ...).
    /// </summary>
    [Test]
    public void BeaconStateGloas_all_default_hash_tree_root_matches_an_independently_computed_value()
    {
        BeaconStateGloas state = new()
        {
            Fork = new Fork(),
            LatestBlockHeader = new BeaconBlockHeader(),
            Eth1Data = new Eth1Data(),
            PreviousJustifiedCheckpoint = new Checkpoint(),
            CurrentJustifiedCheckpoint = new Checkpoint(),
            FinalizedCheckpoint = new Checkpoint(),
            CurrentSyncCommittee = new SyncCommittee(),
            NextSyncCommittee = new SyncCommittee(),
            LatestBlockHash = Hash256.Zero,
            ExecutionPayloadAvailability = new BitArray(8192),
            BuilderPendingPayments = [.. Enumerable.Repeat(0, 64).Select(static _ => new BuilderPendingPayment { Withdrawal = new BuilderPendingWithdrawal() })],
            LatestExecutionPayloadBid = new ExecutionPayloadBid(),
            PtcWindow = [.. Enumerable.Repeat(0, 96).Select(static _ => new PayloadTimelinessCommittee())],
        };

        AssertRoundTripsAndMatchesRoot(state, "0x1971a1bc7e155511766c64b6a2121317d01fa040ffa6da5f93c3629f60fe3166");
    }

    private static void AssertRoundTripsAndMatchesRoot<T>(T value, string expectedRootHex) where T : class, ISszCodec<T>
    {
        AssertRoundTrips(value, T.Encode, T.Decode, T.Merkleize);
        Hash256 root = SszRoots.HashTreeRoot(value);
        Assert.That(root.ToString(), Is.EqualTo(expectedRootHex).IgnoreCase);
    }

    private static void AssertRoundTrips<T>(T value, System.Func<T, byte[]> encode, DecodeDelegate<T> decode, MerkleizeDelegate<T> merkleize)
    {
        byte[] encoded = encode(value);
        decode(encoded, out T decoded);
        byte[] reEncoded = encode(decoded);
        merkleize(value, out UInt256 originalRoot);
        merkleize(decoded, out UInt256 decodedRoot);

        Assert.Multiple(() =>
        {
            Assert.That(reEncoded, Is.EqualTo(encoded));
            Assert.That(decodedRoot, Is.EqualTo(originalRoot));
        });
    }

    private delegate void DecodeDelegate<T>(ReadOnlySpan<byte> data, out T value);
    private delegate void MerkleizeDelegate<T>(T value, out UInt256 root);

    private static byte[] Filled(int length, byte value)
    {
        byte[] bytes = new byte[length];
        bytes.AsSpan().Fill(value);
        return bytes;
    }

    private static Hash256 Hash(byte value) => new(Filled(Hash256.Size, value));

    private static byte[] Hex(string hex) => Bytes.FromHexString(hex);

    private static Nethermind.Merge.Plugin.SszRest.SszKzgCommitment Kzg(string hex) =>
        Nethermind.Merge.Plugin.SszRest.SszKzgCommitment.FromSpan(Hex(hex));

    private static BlsPublicKey Pubkey(byte value) => new(Filled(BlsPublicKey.Length, value));

    private static BlsSignature Signature(byte value) => new(Filled(BlsSignature.Length, value));

    private static Nethermind.Merge.Plugin.SszRest.SszKzgCommitment SszKzgCommitmentOf(byte value) =>
        Nethermind.Merge.Plugin.SszRest.SszKzgCommitment.FromSpan(Filled(Nethermind.Merge.Plugin.SszRest.SszKzgCommitment.KzgCommitmentLength, value));
}
