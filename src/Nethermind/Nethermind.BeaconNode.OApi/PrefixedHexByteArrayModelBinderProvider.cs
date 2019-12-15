using System;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Nethermind.BeaconNode.OApi
{
    public class PrefixedHexByteArrayModelBinderProvider : IModelBinderProvider
    {
        public IModelBinder? GetBinder(ModelBinderProviderContext context)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }
            Type modelType = context.Metadata.UnderlyingOrModelType;
            if (modelType == typeof(byte[]))
            {
                return new PrefixedHexByteArrayModelBinder();
            }
            return null;
        }
    }
}
