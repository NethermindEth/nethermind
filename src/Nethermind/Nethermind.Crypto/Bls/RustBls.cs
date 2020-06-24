using System.Runtime.InteropServices;

namespace Nethermind.Crypto.Bls
{
    public class RustBls
    {
        [DllImport("runtimes\\win-x64\\native\\eth_196.dll")]
        public static extern unsafe uint eip196_perform_operation(
            byte operation,
            byte* input,
            int inputLength,
            byte* output,
            ref int outputLength,
            byte* error,
            ref int errorLength);
    }
}