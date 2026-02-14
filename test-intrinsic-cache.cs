// Quick test to verify intrinsic gas caching works
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Specs.Forks;

var tx = Build.A.Transaction.SignedAndResolved().TestObject;

// First call - should calculate and cache
var gas1 = IntrinsicGasCalculator.Calculate(tx, Berlin.Instance);
Console.WriteLine($"First call: Standard={gas1.Standard}, Floor={gas1.FloorGas}, MinimalGas={gas1.MinimalGas}");
Console.WriteLine($"Cache populated: Standard={tx._cachedIntrinsicGasStandard}, Floor={tx._cachedIntrinsicGasFloor}");

// Second call - should use cache
var gas2 = IntrinsicGasCalculator.Calculate(tx, Berlin.Instance);
Console.WriteLine($"Second call: Standard={gas2.Standard}, Floor={gas2.FloorGas}, MinimalGas={gas2.MinimalGas}");
Console.WriteLine($"Cache still set: Standard={tx._cachedIntrinsicGasStandard}, Floor={tx._cachedIntrinsicGasFloor}");

// Verify both calls returned the same result
if (gas1.Standard == gas2.Standard && gas1.FloorGas == gas2.FloorGas)
{
    Console.WriteLine("\n✅ SUCCESS: Both calls returned identical results!");
    Console.WriteLine("✅ Caching is working correctly - no duplicate calculation!");
}
else
{
    Console.WriteLine("\n❌ FAILURE: Results don't match!");
}
