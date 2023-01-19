// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using Nethermind.Abi;
using Nethermind.Blockchain.Contracts;
using Nethermind.Blockchain.Contracts.Json;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.DepositContract
{
    public sealed class DepositContract : Contract
    {
        public DepositContract(IAbiEncoder abiEncoder, Address contractAddress)
            : base(
                abiEncoder,
                contractAddress,
                new AbiDefinitionParser().Parse(File.ReadAllText("contracts//DepositContract.json")))
        {
        }

        public Transaction Deposit(
            Address sender,
            byte[] blsPublicKey,
            byte[] withdrawalCredentials,
            byte[] blsSignature,
            byte[] depositDataRoot)
            => GenerateTransaction<Transaction>(
                "deposit",
                sender,
                blsPublicKey,
                withdrawalCredentials,
                blsSignature,
                depositDataRoot);

        public Keccak DepositEventHash => GetEventHash("DepositEvent");

        public Transaction Deploy(Address senderAddress) =>
            new Transaction
            {
                Value = 0,
                Init = AbiDefinition.Bytecode,
                GasLimit = 2000000,
                GasPrice = 20.GWei(),
                SenderAddress = senderAddress
            };
    }
}
