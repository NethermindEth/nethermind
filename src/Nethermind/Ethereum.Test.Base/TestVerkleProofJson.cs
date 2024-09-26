// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Ethereum.Test.Base;

public class TestVerkleProofJson
{
    public string[] OtherStems { get; set; }
    public string DepthExtensionPresent { get; set; }
    public string[] CommitmentsByPath { get; set; }
    public string D { get; set; }
    public IpaProofJson C { get; set; }
}

public class IpaProofJson
{
    public string[] Cl { get; set; }
    public string[] Cr { get; set; }
    public string FinalEvaluation { get; set; }
}
