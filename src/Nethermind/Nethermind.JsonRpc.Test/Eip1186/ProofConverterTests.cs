/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.IO;
using System.Text;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Dirichlet.Numerics;
using Nethermind.JsonRpc.Eip1186;
using Nethermind.JsonRpc.Test.Data;
using Nethermind.Store;
using Newtonsoft.Json;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Eip1186
{
    public class ProofConverterTests : SerializationTestBase
    {
        [Test]
        public void Storage_proofs_have_values_set_complex_3_setup()
        {
            Keccak a = new Keccak("0x000000000000000000000000000000000000000000aaaaaaaaaaaaaaaaaaaaaa");
            Keccak b = new Keccak("0x0000000000000000000000000bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            Keccak c = new Keccak("0x00000000001ccccccccccccccccccccccccccccccccccccccccccccccccccccc");
            Keccak d = new Keccak("0x00000000001ddddddddddddddddddddddddddddddddddddddddddddddddddddd");
            Keccak e = new Keccak("0x00000000001eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");

            IDb memDb = new MemDb();
            StateTree tree = new StateTree(memDb);
            StorageTree storageTree = new StorageTree(memDb);
            storageTree.Set(a.Bytes, Rlp.Encode(Bytes.FromHexString("0xab12000000000000000000000000000000000000000000000000000000000000000000000000000000")));
            storageTree.Set(b.Bytes, Rlp.Encode(Bytes.FromHexString("0xab34000000000000000000000000000000000000000000000000000000000000000000000000000000")));
            storageTree.Set(c.Bytes, Rlp.Encode(Bytes.FromHexString("0xab56000000000000000000000000000000000000000000000000000000000000000000000000000000")));
            storageTree.Set(d.Bytes, Rlp.Encode(Bytes.FromHexString("0xab78000000000000000000000000000000000000000000000000000000000000000000000000000000")));
            storageTree.Set(e.Bytes, Rlp.Encode(Bytes.FromHexString("0xab9a000000000000000000000000000000000000000000000000000000000000000000000000000000")));
            storageTree.Commit();

            byte[] code = new byte[] {1, 2, 3};
            Account account1 = Build.An.Account.WithBalance(1).WithStorageRoot(storageTree.RootHash).TestObject;
            Account account2 = Build.An.Account.WithBalance(2).TestObject;
            tree.Set(TestItem.AddressA, account1);
            tree.Set(TestItem.AddressB, account2);
            tree.Commit();

            ProofCollector proofCollector = new ProofCollector(TestItem.AddressA, new Keccak[] {a, b, c, d, e});
            tree.Accept(proofCollector, memDb, tree.RootHash);
            AccountProof proof = proofCollector.BuildResult();

            TestOneWaySerialization(proof, "{\"accountProof\":[\"0xe215a0e2a0cd25c7c043b502d300690d497d07c90503cf48575d7c4d9df48c3239c3f4\",\"0xf8518080808080a064c7ecf7af3f0cd537929398725a611310f6d5190a097aca9c03a3d21ce061128080808080808080a02352504a0cd6095829b18bae394d0c882d84eead7be5b6ad0a87daaff9d2fb4a8080\",\"0xf869a020227dead52ea912e013e7641ccd6b3b174498e55066b0c174a09c8c3cc4bf5eb846f8448001a0afc6e7c1c13f18c0ee2a94c64c0855a03d1dd26afe5f6b2c2a99eb065223ca39a0c5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470\"],\"balance\":\"0x1\",\"codeHash\":\"0xc5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470\",\"nonce\":\"0x0\",\"storageHash\":\"0xafc6e7c1c13f18c0ee2a94c64c0855a03d1dd26afe5f6b2c2a99eb065223ca39\",\"storageProof\":[{\"key\":\"0x000000000000000000000000000000000000000000aaaaaaaaaaaaaaaaaaaaaa\",\"proof\":[\"0xe886000000000000a0830818307108fb62adbcdd1d9b869a4cf8955dc211a286503febdf17ac599b2e\",\"0xf851a020be9ddd30723181a87b18a6d2bfa2b3323f30f1d0646aa9c7eea06af9e31c57a06da0dc6a9169f5f35fd7d057065203ba2c9fb225d8d6b4bb35f2dc1d2ba693b6808080808080808080808080808080\",\"0xea880000000000000000a0c0f54c2d3456184a7bc418ec5b534a9a136ed3b5627caced8bed3732264b3fed\",\"0xf851a034c4b62df64d959a98788985227a6c0ab009d7db955547d0be790c1ce553ed4980808080808080808080a0b43c9841efec2ea2b85472f8883b77a2669cc8ed0c86a422d4114b9e17f115918080808080\"],\"value\":\"0xab12000000000000000000000000000000000000000000000000000000000000000000000000000000\"},{\"key\":\"0x0000000000000000000000000bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"proof\":[\"0xe886000000000000a0830818307108fb62adbcdd1d9b869a4cf8955dc211a286503febdf17ac599b2e\",\"0xf851a020be9ddd30723181a87b18a6d2bfa2b3323f30f1d0646aa9c7eea06af9e31c57a06da0dc6a9169f5f35fd7d057065203ba2c9fb225d8d6b4bb35f2dc1d2ba693b6808080808080808080808080808080\",\"0xea880000000000000000a0c0f54c2d3456184a7bc418ec5b534a9a136ed3b5627caced8bed3732264b3fed\",\"0xf851a034c4b62df64d959a98788985227a6c0ab009d7db955547d0be790c1ce553ed4980808080808080808080a0b43c9841efec2ea2b85472f8883b77a2669cc8ed0c86a422d4114b9e17f115918080808080\"],\"value\":\"0xab34000000000000000000000000000000000000000000000000000000000000000000000000000000\"},{\"key\":\"0x00000000001ccccccccccccccccccccccccccccccccccccccccccccccccccccc\",\"proof\":[\"0xe886000000000000a0830818307108fb62adbcdd1d9b869a4cf8955dc211a286503febdf17ac599b2e\",\"0xf851a020be9ddd30723181a87b18a6d2bfa2b3323f30f1d0646aa9c7eea06af9e31c57a06da0dc6a9169f5f35fd7d057065203ba2c9fb225d8d6b4bb35f2dc1d2ba693b6808080808080808080808080808080\",\"0xf871808080808080808080808080a0d05f5bbfc8b0cf084848efcdd079e280384c937c8f30e5fe86dea0b4c3e23ebaa0ed9e169336203e22c92423e552c29f717f1024954239f90ee6940d9546149ad3a06841d65c5f2d895812aa6b18874d0aeae1cca5d73d3ee9277dc00bc84bee6ca78080\",\"0xf8479b20ccccccccccccccccccccccccccccccccccccccccccccccccccccaaa9ab56000000000000000000000000000000000000000000000000000000000000000000000000000000\"],\"value\":\"0xab56000000000000000000000000000000000000000000000000000000000000000000000000000000\"},{\"key\":\"0x00000000001ddddddddddddddddddddddddddddddddddddddddddddddddddddd\",\"proof\":[\"0xe886000000000000a0830818307108fb62adbcdd1d9b869a4cf8955dc211a286503febdf17ac599b2e\",\"0xf851a020be9ddd30723181a87b18a6d2bfa2b3323f30f1d0646aa9c7eea06af9e31c57a06da0dc6a9169f5f35fd7d057065203ba2c9fb225d8d6b4bb35f2dc1d2ba693b6808080808080808080808080808080\",\"0xf871808080808080808080808080a0d05f5bbfc8b0cf084848efcdd079e280384c937c8f30e5fe86dea0b4c3e23ebaa0ed9e169336203e22c92423e552c29f717f1024954239f90ee6940d9546149ad3a06841d65c5f2d895812aa6b18874d0aeae1cca5d73d3ee9277dc00bc84bee6ca78080\",\"0xf8479b20ddddddddddddddddddddddddddddddddddddddddddddddddddddaaa9ab78000000000000000000000000000000000000000000000000000000000000000000000000000000\"],\"value\":\"0xab78000000000000000000000000000000000000000000000000000000000000000000000000000000\"},{\"key\":\"0x00000000001eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee\",\"proof\":[\"0xe886000000000000a0830818307108fb62adbcdd1d9b869a4cf8955dc211a286503febdf17ac599b2e\",\"0xf851a020be9ddd30723181a87b18a6d2bfa2b3323f30f1d0646aa9c7eea06af9e31c57a06da0dc6a9169f5f35fd7d057065203ba2c9fb225d8d6b4bb35f2dc1d2ba693b6808080808080808080808080808080\",\"0xf871808080808080808080808080a0d05f5bbfc8b0cf084848efcdd079e280384c937c8f30e5fe86dea0b4c3e23ebaa0ed9e169336203e22c92423e552c29f717f1024954239f90ee6940d9546149ad3a06841d65c5f2d895812aa6b18874d0aeae1cca5d73d3ee9277dc00bc84bee6ca78080\",\"0xf8479b20eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeaaa9ab9a000000000000000000000000000000000000000000000000000000000000000000000000000000\"],\"value\":\"0xab9a000000000000000000000000000000000000000000000000000000000000000000000000000000\"}]}");
        }
    }
}