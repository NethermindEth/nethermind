// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Ethereum.Test.Base;

namespace Ethereum.Legacy.Blockchain.Test;

public class MetaTests : DirectoryMetaTests<StPrefix>
{
    protected override IEnumerable<string> FilterDirectories(IEnumerable<string> dirs) =>
        dirs.Where(d => d != "stEWASMTests");
}

public class ArgsZeroOneBalance : LegacyStateTestFixture<ArgsZeroOneBalance>;

public class AttackTest : LegacyStateTestFixture<AttackTest>;

public class BadOpcode : LegacyStateTestFixture<BadOpcode>;

public class Bugs : LegacyStateTestFixture<Bugs>;

public class CallCodes : LegacyStateTestFixture<CallCodes>;

public class CallCreateCallCodeTest : LegacyStateTestFixture<CallCreateCallCodeTest>;

public class CallDelegateCodesCallCodeHomestead : LegacyStateTestFixture<CallDelegateCodesCallCodeHomestead>;

public class CallDelegateCodesHomestead : LegacyStateTestFixture<CallDelegateCodesHomestead>;

public class ChainId : LegacyStateTestFixture<ChainId>;

public class ChangedEIP150 : LegacyStateTestFixture<ChangedEIP150>;

public class CodeCopyTest : LegacyStateTestFixture<CodeCopyTest>;

public class CodeSizeLimit : LegacyStateTestFixture<CodeSizeLimit>;

public class Create2 : LegacyStateTestFixture<Create2>;

public class CreateTest : LegacyStateTestFixture<CreateTest>;

public class DelegatecallTestHomestead : LegacyStateTestFixture<DelegatecallTestHomestead>;

public class EIP1153 : LegacyStateTestFixture<EIP1153>;

public class EIP1153_transientStorage : LegacyStateTestFixture<EIP1153_transientStorage>;

public class EIP150Specific : LegacyStateTestFixture<EIP150Specific>;

public class EIP150singleCodeGasPrices : LegacyStateTestFixture<EIP150singleCodeGasPrices>;

public class EIP1559 : LegacyStateTestFixture<EIP1559>;

public class EIP158Specific : LegacyStateTestFixture<EIP158Specific>;

public class EIP2930 : LegacyStateTestFixture<EIP2930>;

public class EIP3607 : LegacyStateTestFixture<EIP3607>;

public class EIP3651 : LegacyStateTestFixture<EIP3651>;

public class EIP3651_warmcoinbase : LegacyStateTestFixture<EIP3651_warmcoinbase>;

public class EIP3855 : LegacyStateTestFixture<EIP3855>;

public class EIP3855_push0 : LegacyStateTestFixture<EIP3855_push0>;

public class EIP3860 : LegacyStateTestFixture<EIP3860>;

public class EIP3860_limitmeterinitcode : LegacyStateTestFixture<EIP3860_limitmeterinitcode>;

public class EIP4844 : LegacyStateTestFixture<EIP4844>;

public class EIP4844_blobtransactions : LegacyStateTestFixture<EIP4844_blobtransactions>;

public class EIP5656 : LegacyStateTestFixture<EIP5656>;

public class EIP5656_MCOPY : LegacyStateTestFixture<EIP5656_MCOPY>;

public class Example : LegacyStateTestFixture<Example>;

public class ExtCodeHash : LegacyStateTestFixture<ExtCodeHash>;

public class HomesteadSpecific : LegacyStateTestFixture<HomesteadSpecific>;

public class InitCodeTest : LegacyStateTestFixture<InitCodeTest>;

public class LogTests : LegacyStateTestFixture<LogTests>;

public class MemExpandingEIP150Calls : LegacyStateTestFixture<MemExpandingEIP150Calls>;

public class MemoryStressTest : LegacyStateTestFixture<MemoryStressTest>;

public class MemoryTest : LegacyStateTestFixture<MemoryTest>;

public class NonZeroCallsTest : LegacyStateTestFixture<NonZeroCallsTest>;

public class PreCompiledContracts : LegacyStateTestFixture<PreCompiledContracts>;

public class PreCompiledContracts2 : LegacyStateTestFixture<PreCompiledContracts2>;

public class QuadraticComplexityTest : LegacyStateTestFixture<QuadraticComplexityTest>;

public class Random : LegacyStateTestFixture<Random>;

public class Random2 : LegacyStateTestFixture<Random2>;

public class RecursiveCreate : LegacyStateTestFixture<RecursiveCreate>;

public class RefundTest : LegacyStateTestFixture<RefundTest>;

public class ReturnDataTest : LegacyStateTestFixture<ReturnDataTest>;

public class SelfBalance : LegacyStateTestFixture<SelfBalance>;

public class Shift : LegacyStateTestFixture<Shift>;

public class SLoadTest : LegacyStateTestFixture<SLoadTest>;

public class SolidityTest : LegacyStateTestFixture<SolidityTest>;

public class SStoreTest : LegacyStateTestFixture<SStoreTest>;

public class StackTests : LegacyStateTestFixture<StackTests>;

public class StaticCall : LegacyStateTestFixture<StaticCall>;

public class StaticFlagEnabled : LegacyStateTestFixture<StaticFlagEnabled>;

public class SystemOperationsTest : LegacyStateTestFixture<SystemOperationsTest>;

public class TimeConsuming : LegacyStateTestFixture<TimeConsuming>;

public class TransactionTest : LegacyStateTestFixture<TransactionTest>;

public class TransitionTest : LegacyStateTestFixture<TransitionTest>;

public class WalletTest : LegacyStateTestFixture<WalletTest>;

public class ZeroCallsRevert : LegacyStateTestFixture<ZeroCallsRevert>;

public class ZeroCallsTest : LegacyStateTestFixture<ZeroCallsTest>;

public class ZeroKnowledge : LegacyStateTestFixture<ZeroKnowledge>;

public class ZeroKnowledge2 : LegacyStateTestFixture<ZeroKnowledge2>;

public class SpecialTest : LegacyRetryStateTestFixture<SpecialTest>;
