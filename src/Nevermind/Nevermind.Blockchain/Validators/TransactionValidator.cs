using Nevermind.Core;
using Nevermind.Core.Potocol;
using Nevermind.Evm;

namespace Nevermind.Blockchain.Validators
{
    public class TransactionValidator : ITransactionValidator
    {
        private readonly IEthereumRelease _spec;
        private readonly ISignatureValidator _signatureValidator;

        // TODO: this will be calculated twice, refactor
        private readonly IntrinsicGasCalculator _intrinsicGasCalculator = new IntrinsicGasCalculator();

        public TransactionValidator(IEthereumRelease spec, ISignatureValidator signatureValidator)
        {
            _spec = spec;
            _signatureValidator = signatureValidator;
        }

        public bool IsWellFormed(Transaction transaction, bool ignoreSignature = false)
        {
            return Validator.IsInP256(transaction.Nonce) &&
                   Validator.IsInP256(transaction.GasPrice) &&
                   Validator.IsInP256(transaction.GasLimit) &&
                   transaction.GasLimit >= _intrinsicGasCalculator.Calculate(_spec, transaction) &&
                   (transaction.To != null || transaction.Init != null) && // TODO: check tests where this is the case and still state changes (is the gas substracted?)
                   Validator.IsInP256(transaction.Value) &&
                   // both null: transfer; data not null: message call; init not null: account creation
                   !(transaction.Data != null && transaction.Init != null) &&
                   (ignoreSignature || _signatureValidator.Validate(transaction.Signature));
            // TODO: also check if nonce is equal to sending account nonce
        }
    }
}