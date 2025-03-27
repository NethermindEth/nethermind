// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core.Specs;

public static class BlobProofExtensions
{
    public static ProofVersion GetBlobProofVersion(this IReleaseSpec spec) => spec.IsEip7594Enabled ? ProofVersion.V2 : ProofVersion.V1;
}
