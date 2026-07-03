// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Blockchain;

/// <summary>
/// Accounts targeted by the EIP-8253 irregular state transition: mainnet accounts with
/// empty code, zero nonce, and non-empty storage whose nonce is bumped to 1 at fork activation.
/// </summary>
/// <remarks>
/// Source of truth: https://eips.ethereum.org/assets/eip-8253/targeted-accounts.json (28 entries).
/// </remarks>
public static class Eip8253Data
{
    public static readonly Address[] Accounts =
    [
        new("0xf468bcbc4a0bfdb06336e773382c5202e674db71"),
        new("0xd8253352f6044cfe55bcc0748c3fa37b7df81f98"),
        new("0x5983c6ac846dcf85fbbc4303f43eb91c379f79ae"),
        new("0xde425ad4b8d2d9e0e12f65cbcd6d55f447b44083"),
        new("0x50b1497068bae652df3562eb8ea7677ff84477fa"),
        new("0x8398ff6c618e9515468c1c4b198d53666cbe8462"),
        new("0x6f156dbf8ed30e53f7c9df73144e69f65cbb7e94"),
        new("0x2c081ed1949d7dd9447f9d96e509befe576d4461"),
        new("0xdb7c577b93baeb56dab50af4d6f86f99a06b96a2"),
        new("0x14725085d004f1b10ee07234a4ab28c5ad2a7b9e"),
        new("0xae3703584494ade958ad27ec2d289b7a67c19e90"),
        new("0x7d6ae067de8d44ae1a08750e7d626d61a623c44a"),
        new("0x4d149eb99bdeefc1f858f8fd22289c6beae99f2c"),
        new("0x361d7a60b43587c7f6bba4f9fd9642747f65210a"),
        new("0xb619f45637c39ca49a41ac64c11637a0a194455e"),
        new("0x5071cb62aa170b7f66b26cae8004d90e6078bb1e"),
        new("0xadd92e0650457c5db0c4c08cbf7ca580175d33d2"),
        new("0x3311c08066580cb906a7287b6786e504c2ebd09f"),
        new("0x02820e4bee488c40f7455fdca53125565148708f"),
        new("0xe62dc49c92fa799033644d2a9afd7e3babe5a80a"),
        new("0x5cc182fabfb81a056b6080d4200bc5150673d06f"),
        new("0xf4a835ec1364809003de3925685f24cd360bdffe"),
        new("0xfc4465f84b29a1f8794dc753f41bef1f4b025ed2"),
        new("0x40490c9c468622d5c89646d6f3097f8eaf80c411"),
        new("0xa21b22389bfc1cd6bc7ba19a4fc96adc3d0fe074"),
        new("0x59ec0410867828e3b8c23dd8a29d9796ef523b17"),
        new("0x19272418753b90d9a3e3efc8430b1612c55fcb3a"),
        new("0xfee7707fa4b8c0a923a0e40399db3e7ce26069c6"),
    ];
}
