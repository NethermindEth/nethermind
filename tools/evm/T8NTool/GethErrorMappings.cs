namespace Evm.T8NTool;

// mapping Nethermind core error messages to geth errors
public class GethErrorMappings
{
    private const string WrongTransactionNonceError = "wrong transaction nonce";
    private const string WrongTransactionNonceGethError = "nonce too low: address {0}, tx: {1} state: {2}";

    private const string MissingTxToError = "TxMissingTo: Must be set.";
    private const string MissingTxToGethError = "rlp: input string too short for common.Address, decoding into (types.Transaction)(types.BlobTx).To";

    private static Dictionary<string, string> _mapping = new()
    {
        { WrongTransactionNonceError, WrongTransactionNonceGethError },
        { MissingTxToError, MissingTxToGethError }
    };
 
    public static string GetErrorMapping(string error, params object[] arguments)
    {
        return _mapping.TryGetValue(error, out var newError) ? string.Format(newError, arguments) : error;
    }
}