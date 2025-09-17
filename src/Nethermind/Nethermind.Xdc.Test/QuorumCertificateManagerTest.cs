// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Types;
using NSubstitute;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Xdc.Test;
public class QuorumCertificateManagerTest
{
    public static IEnumerable<TestCaseData> QcCases()
    {
        XdcBlockHeaderBuilder headerBuilder = Build.A.XdcBlockHeader();

        //Base valid control case
        yield return new TestCaseData(Build.A.QuorumCertificate().TestObject, headerBuilder, true);
    }

    [TestCaseSource(nameof(QcCases))]
    public void VerifyCertificate_(QuorumCertificate quorumCert, XdcBlockHeaderBuilder xdcBlockHeaderBuilder, bool expected)
    {
        var quorumCertificateManager = new QuorumCertificateManager(new XdcContext(),
            Substitute.For<IBlockTree>(),
            Substitute.For<IXdcReleaseSpec>(),
            Substitute.For<IEpochSwitchManager>());

        quorumCertificateManager.VerifyCertificate(quorumCert, xdcBlockHeaderBuilder.TestObject, out _);
    }
}
