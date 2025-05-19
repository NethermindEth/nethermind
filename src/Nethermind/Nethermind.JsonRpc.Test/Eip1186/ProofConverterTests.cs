// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.JsonRpc.Test.Data;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.State.Proofs;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Eip1186
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class ProofConverterTests : SerializationTestBase
    {
        [Test]
        public void Storage_proofs_have_values_set_complex_3_setup()
        {
            byte[] a = Bytes.FromHexString("0x000000000000000000000000000000000000000000aaaaaaaaaaaaaaaaaaaaaa");
            byte[] b = Bytes.FromHexString("0x0000000000000000000000000bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            byte[] c = Bytes.FromHexString("0x00000000001ccccccccccccccccccccccccccccccccccccccccccccccccccccc");
            byte[] d = Bytes.FromHexString("0x00000000001ddddddddddddddddddddddddddddddddddddddddddddddddddddd");
            byte[] e = Bytes.FromHexString("0x00000000001eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");

            IDb memDb = new MemDb();
            TrieStore trieStore = TestTrieStoreFactory.Build(memDb, LimboLogs.Instance);
            StateTree tree = new(trieStore, LimboLogs.Instance);
            StorageTree storageTree = new(trieStore.GetTrieStore(TestItem.AddressA), Keccak.EmptyTreeHash, LimboLogs.Instance);
            storageTree.Set(Keccak.Compute(a).Bytes, Rlp.Encode(Bytes.FromHexString("0xab12000000000000000000000000000000000000000000000000000000000000000000000000000000")));
            storageTree.Set(Keccak.Compute(b).Bytes, Rlp.Encode(Bytes.FromHexString("0xab34000000000000000000000000000000000000000000000000000000000000000000000000000000")));
            storageTree.Set(Keccak.Compute(c).Bytes, Rlp.Encode(Bytes.FromHexString("0xab56000000000000000000000000000000000000000000000000000000000000000000000000000000")));
            storageTree.Set(Keccak.Compute(d).Bytes, Rlp.Encode(Bytes.FromHexString("0xab78000000000000000000000000000000000000000000000000000000000000000000000000000000")));
            storageTree.Set(Keccak.Compute(e).Bytes, Rlp.Encode(Bytes.FromHexString("0xab9a000000000000000000000000000000000000000000000000000000000000000000000000000000")));
            storageTree.Commit();
            _ = new byte[] { 1, 2, 3 };
            Account account1 = Build.An.Account.WithBalance(1).WithStorageRoot(storageTree.RootHash).TestObject;
            Account account2 = Build.An.Account.WithBalance(2).TestObject;
            tree.Set(TestItem.AddressA, account1);
            tree.Set(TestItem.AddressB, account2);
            tree.Commit();

            AccountProofCollector accountProofCollector = new(TestItem.AddressA, new byte[][] { a, b, c, d, e });
            tree.Accept(accountProofCollector, tree.RootHash);
            AccountProof proof = accountProofCollector.BuildResult();

            TestToJson(proof, "{\"accountProof\":[\"0xe215a08c9a7cdf08d4425c138ef5ba3d1f6c2ae18786fe88d9a56230a00b3e83367b25\",\"0xf8518080808080a017934c1c90ce30ca48f32b4f449dab308cfba803fd6d142cab01eb1fad1b70038080808080808080a02352504a0cd6095829b18bae394d0c882d84eead7be5b6ad0a87daaff9d2fb4a8080\",\"0xf869a020227dead52ea912e013e7641ccd6b3b174498e55066b0c174a09c8c3cc4bf5eb846f8448001a0b2375a34ff2c8037d9ff04ebc16367b51d156d9a905ca54cef50bfad1a4c0711a0c5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470\"],\"address\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"balance\":\"0x1\",\"codeHash\":\"0xc5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470\",\"nonce\":\"0x0\",\"storageHash\":\"0xb2375a34ff2c8037d9ff04ebc16367b51d156d9a905ca54cef50bfad1a4c0711\",\"storageProof\":[{\"key\":\"0x000000000000000000000000000000000000000000aaaaaaaaaaaaaaaaaaaaaa\",\"proof\":[\"0xf8918080a02474007b0486d5e951fe3fbcdae3e63cadf9c85cb8f178d3c7ca972e2d77705a808080808080a059c4fa21a1e4c40dc3c87e925befbb78a5b6f729865a12f6f0490d9801bcbf22a06b3576bbd6c91ca7128f69f728f3e30bf8980c6381430a5e80186d0dfec89d4e8080a098cfc3bf071c19a2e230165f4152bb98a5d1ab0fee47c952de65da85fcbdfdb2808080\",\"0xf85180a0aec9a5fc3ba2ebedf137fbcf6987b303c9d8718f75253e6e2444b81a4049e5b980808080808080808080a0397f22cb0ad24543caffaad031e3a0a538e5d8ac106d8f6858a703d442c0e4d380808080\",\"0xf84ca02046d62176084b9d1eace1c8bcc2353228d10569a500ccfd1bdbd8c093f4b4e9aaa9ab12000000000000000000000000000000000000000000000000000000000000000000000000000000\"],\"value\":\"0xab12000000000000000000000000000000000000000000000000000000000000000000000000000000\"},{\"key\":\"0x0000000000000000000000000bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"proof\":[\"0xf8918080a02474007b0486d5e951fe3fbcdae3e63cadf9c85cb8f178d3c7ca972e2d77705a808080808080a059c4fa21a1e4c40dc3c87e925befbb78a5b6f729865a12f6f0490d9801bcbf22a06b3576bbd6c91ca7128f69f728f3e30bf8980c6381430a5e80186d0dfec89d4e8080a098cfc3bf071c19a2e230165f4152bb98a5d1ab0fee47c952de65da85fcbdfdb2808080\",\"0xf85180a0aec9a5fc3ba2ebedf137fbcf6987b303c9d8718f75253e6e2444b81a4049e5b980808080808080808080a0397f22cb0ad24543caffaad031e3a0a538e5d8ac106d8f6858a703d442c0e4d380808080\",\"0xf84ca020cc5921315fe8a051acdea77b782ecb469470d4e94248cb4ff49a1ff26fe982aaa9ab34000000000000000000000000000000000000000000000000000000000000000000000000000000\"],\"value\":\"0xab34000000000000000000000000000000000000000000000000000000000000000000000000000000\"},{\"key\":\"0x00000000001ccccccccccccccccccccccccccccccccccccccccccccccccccccc\",\"proof\":[\"0xf8918080a02474007b0486d5e951fe3fbcdae3e63cadf9c85cb8f178d3c7ca972e2d77705a808080808080a059c4fa21a1e4c40dc3c87e925befbb78a5b6f729865a12f6f0490d9801bcbf22a06b3576bbd6c91ca7128f69f728f3e30bf8980c6381430a5e80186d0dfec89d4e8080a098cfc3bf071c19a2e230165f4152bb98a5d1ab0fee47c952de65da85fcbdfdb2808080\",\"0xf84ca0383dacaf928fae678bb3ccca17c746816e9bcfc5628853ae37c67b2d3bbaad32aaa9ab56000000000000000000000000000000000000000000000000000000000000000000000000000000\"],\"value\":\"0xab56000000000000000000000000000000000000000000000000000000000000000000000000000000\"},{\"key\":\"0x00000000001ddddddddddddddddddddddddddddddddddddddddddddddddddddd\",\"proof\":[\"0xf8918080a02474007b0486d5e951fe3fbcdae3e63cadf9c85cb8f178d3c7ca972e2d77705a808080808080a059c4fa21a1e4c40dc3c87e925befbb78a5b6f729865a12f6f0490d9801bcbf22a06b3576bbd6c91ca7128f69f728f3e30bf8980c6381430a5e80186d0dfec89d4e8080a098cfc3bf071c19a2e230165f4152bb98a5d1ab0fee47c952de65da85fcbdfdb2808080\",\"0xf84ca031c208aebac39ba1b4503fa46a491619019fc1a910b3bfe3c78dc3da4abdb097aaa9ab78000000000000000000000000000000000000000000000000000000000000000000000000000000\"],\"value\":\"0xab78000000000000000000000000000000000000000000000000000000000000000000000000000000\"},{\"key\":\"0x00000000001eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee\",\"proof\":[\"0xf8918080a02474007b0486d5e951fe3fbcdae3e63cadf9c85cb8f178d3c7ca972e2d77705a808080808080a059c4fa21a1e4c40dc3c87e925befbb78a5b6f729865a12f6f0490d9801bcbf22a06b3576bbd6c91ca7128f69f728f3e30bf8980c6381430a5e80186d0dfec89d4e8080a098cfc3bf071c19a2e230165f4152bb98a5d1ab0fee47c952de65da85fcbdfdb2808080\",\"0xf84ca03ca9062cf1266ae3ad77045eb67b33d4c9a4f5e2be44c79cad975ace0bc6ed22aaa9ab9a000000000000000000000000000000000000000000000000000000000000000000000000000000\"],\"value\":\"0xab9a000000000000000000000000000000000000000000000000000000000000000000000000000000\"}]}");
        }

        [Test]
        public void Does_not_fail_when_proofs_are_longer_than_number_of_proofs_regression()
        {
            byte[] a = Bytes.FromHexString("0x000000000000000000000000000000000000000000aaaaaaaaaaaaaaaaaaaaaa");
            byte[] b = Bytes.FromHexString("0x0000000000000000000000000bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            byte[] c = Bytes.FromHexString("0x00000000001ccccccccccccccccccccccccccccccccccccccccccccccccccccc");
            byte[] d = Bytes.FromHexString("0x00000000001ddddddddddddddddddddddddddddddddddddddddddddddddddddd");
            byte[] e = Bytes.FromHexString("0x00000000001eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");

            IDb memDb = new MemDb();
            TrieStore trieStore = TestTrieStoreFactory.Build(memDb, LimboLogs.Instance);
            StateTree tree = new(trieStore, LimboLogs.Instance);
            StorageTree storageTree = new(trieStore.GetTrieStore(TestItem.AddressA), Keccak.EmptyTreeHash, LimboLogs.Instance);
            storageTree.Set(Keccak.Compute(a).Bytes, Rlp.Encode(Bytes.FromHexString("0xab12000000000000000000000000000000000000000000000000000000000000000000000000000000")));
            storageTree.Set(Keccak.Compute(b).Bytes, Rlp.Encode(Bytes.FromHexString("0xab34000000000000000000000000000000000000000000000000000000000000000000000000000000")));
            storageTree.Set(Keccak.Compute(c).Bytes, Rlp.Encode(Bytes.FromHexString("0xab56000000000000000000000000000000000000000000000000000000000000000000000000000000")));
            storageTree.Set(Keccak.Compute(d).Bytes, Rlp.Encode(Bytes.FromHexString("0xab78000000000000000000000000000000000000000000000000000000000000000000000000000000")));
            storageTree.Set(Keccak.Compute(e).Bytes, Rlp.Encode(Bytes.FromHexString("0xab9a000000000000000000000000000000000000000000000000000000000000000000000000000000")));
            storageTree.Commit();
            _ = new byte[] { 1, 2, 3 };
            Account account1 = Build.An.Account.WithBalance(1).WithStorageRoot(storageTree.RootHash).TestObject;
            Account account2 = Build.An.Account.WithBalance(2).TestObject;
            tree.Set(TestItem.AddressA, account1);
            tree.Set(TestItem.AddressB, account2);
            tree.Commit();

            AccountProofCollector accountProofCollector = new(TestItem.AddressA, new byte[][] { a });
            tree.Accept(accountProofCollector, tree.RootHash);
            AccountProof proof = accountProofCollector.BuildResult();

            TestToJson(proof, "{\"accountProof\":[\"0xe215a08c9a7cdf08d4425c138ef5ba3d1f6c2ae18786fe88d9a56230a00b3e83367b25\",\"0xf8518080808080a017934c1c90ce30ca48f32b4f449dab308cfba803fd6d142cab01eb1fad1b70038080808080808080a02352504a0cd6095829b18bae394d0c882d84eead7be5b6ad0a87daaff9d2fb4a8080\",\"0xf869a020227dead52ea912e013e7641ccd6b3b174498e55066b0c174a09c8c3cc4bf5eb846f8448001a0b2375a34ff2c8037d9ff04ebc16367b51d156d9a905ca54cef50bfad1a4c0711a0c5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470\"],\"address\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"balance\":\"0x1\",\"codeHash\":\"0xc5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470\",\"nonce\":\"0x0\",\"storageHash\":\"0xb2375a34ff2c8037d9ff04ebc16367b51d156d9a905ca54cef50bfad1a4c0711\",\"storageProof\":[{\"key\":\"0x000000000000000000000000000000000000000000aaaaaaaaaaaaaaaaaaaaaa\",\"proof\":[\"0xf8918080a02474007b0486d5e951fe3fbcdae3e63cadf9c85cb8f178d3c7ca972e2d77705a808080808080a059c4fa21a1e4c40dc3c87e925befbb78a5b6f729865a12f6f0490d9801bcbf22a06b3576bbd6c91ca7128f69f728f3e30bf8980c6381430a5e80186d0dfec89d4e8080a098cfc3bf071c19a2e230165f4152bb98a5d1ab0fee47c952de65da85fcbdfdb2808080\",\"0xf85180a0aec9a5fc3ba2ebedf137fbcf6987b303c9d8718f75253e6e2444b81a4049e5b980808080808080808080a0397f22cb0ad24543caffaad031e3a0a538e5d8ac106d8f6858a703d442c0e4d380808080\",\"0xf84ca02046d62176084b9d1eace1c8bcc2353228d10569a500ccfd1bdbd8c093f4b4e9aaa9ab12000000000000000000000000000000000000000000000000000000000000000000000000000000\"],\"value\":\"0xab12000000000000000000000000000000000000000000000000000000000000000000000000000000\"}]}");
        }
    }
}
