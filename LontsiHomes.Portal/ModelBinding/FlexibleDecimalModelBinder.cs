using Common.Helpers;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace LontsiHomes.Portal.ModelBinding
{
    public sealed class FlexibleDecimalModelBinderProvider : IModelBinderProvider
    {
        private static readonly IModelBinder Binder = new FlexibleDecimalModelBinder();

        public IModelBinder? GetBinder(ModelBinderProviderContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            var modelType = context.Metadata.ModelType;
            return modelType == typeof(decimal) || modelType == typeof(decimal?)
                ? Binder
                : null;
        }
    }

    internal sealed class FlexibleDecimalModelBinder : IModelBinder
    {
        public Task BindModelAsync(ModelBindingContext bindingContext)
        {
            ArgumentNullException.ThrowIfNull(bindingContext);
            var valueResult = bindingContext.ValueProvider.GetValue(bindingContext.ModelName);
            if (valueResult == ValueProviderResult.None)
            {
                return Task.CompletedTask;
            }

            bindingContext.ModelState.SetModelValue(bindingContext.ModelName, valueResult);
            var rawValue = valueResult.FirstValue;
            if (string.IsNullOrWhiteSpace(rawValue) && bindingContext.ModelType == typeof(decimal?))
            {
                bindingContext.Result = ModelBindingResult.Success(null);
                return Task.CompletedTask;
            }

            if (FlexibleDecimalParser.TryParse(rawValue, out var parsedValue))
            {
                bindingContext.Result = ModelBindingResult.Success(parsedValue);
                return Task.CompletedTask;
            }

            bindingContext.ModelState.TryAddModelError(
                bindingContext.ModelName,
                "Enter a valid amount using a comma or a period as the decimal separator.");
            return Task.CompletedTask;
        }
    }
}
