// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc.Data;

namespace Nethermind.JsonRpc.Modules.Personal
{
    [RpcModule(ModuleType.Personal)]
    public interface IPersonalRpcModule : IRpcModule
    {
        [JsonRpcMethod(Description = "", ExampleResponse = "0x707fc13c0eb628c074f7ff514ae21acaee0ec072")]
        ResultWrapper<Address> personal_importRawKey([JsonRpcParameter(ExampleValue = "[\"a8fceb14d53045b1c8baedf7bc1f38b2540ce132ac28b1ec8b93b8113165abc0\", \"testPass\"]")] byte[] keyData, string passphrase);

        [JsonRpcMethod(Description = "", ExampleResponse = "[\"0x247b5f5f007fb5d50de13cfcbd4460db21c12bcb\",\"0x707fc13c0eb628c074f7ff514ae21acaee0ec072\"]")]
        ResultWrapper<Address[]> personal_listAccounts();

        [JsonRpcMethod(Description = "", ExampleResponse = "true")]
        ResultWrapper<bool> personal_lockAccount([JsonRpcParameter(ExampleValue = "707Fc13C0eB628c074f7ff514Ae21ACaeE0ec072")] Address address);

        [JsonRpcMethod(Description = "", ExampleResponse = "true")]
        ResultWrapper<bool> personal_unlockAccount([JsonRpcParameter(ExampleValue = "[\"707Fc13C0eB628c074f7ff514Ae21ACaeE0ec072\",\"testPass\"]")] Address address, string passphrase);

        [JsonRpcMethod(Description = "", ExampleResponse = "0xfb06d31473545b0e0d62a24e02b266e08523c6a9")]
        ResultWrapper<Address> personal_newAccount([JsonRpcParameter(ExampleValue = "testPass")] string passphrase);

        [JsonRpcMethod(Description = "", IsImplemented = false)]
        ResultWrapper<Keccak> personal_sendTransaction(TransactionForRpc transaction, string passphrase);

        [JsonRpcMethod(Description = "ecRecover returns the address associated with the private key that was used to calculate the signature in personal_sign",
            IsImplemented = false,
            ExampleResponse = "0x1ddea39c8b8a2202cd9f56bc9a6ecdbf1cf3d5f5")]
        ResultWrapper<Address> personal_ecRecover([JsonRpcParameter(ExampleValue = "[\"0xdeadbeaf\", \"0xa3f20717a250c2b0b729b7e5becbff67fdaef7e0699da4de7ca5895b02a170a12d887fd3b17bfdce3481f10bea41f45ba9f709d39ce8325427b57afcfc994cee1b\"]")] byte[] message, byte[] signature);

        [JsonRpcMethod(Description = "The sign method calculates an Ethereum specific signature with: sign(keccack256(\"\x19Ethereum Signed Message:\n\" + len(message) + message))).",
            IsImplemented = false,
            ExampleResponse = "0xa3f20717a250c2b0b729b7e5becbff67fdaef7e0699da4de7ca5895b02a170a12d887fd3b17bfdce3481f10bea41f45ba9f709d39ce8325427b57afcfc994cee1b")]
        ResultWrapper<byte[]> personal_sign([JsonRpcParameter(ExampleValue = "[\"0xdeadbeaf\", \"0x9b2055d370f73ec7d8a03e965129118dc8f5bf83\"]")] byte[] message, Address address, string passphrase = null);
    }
}
