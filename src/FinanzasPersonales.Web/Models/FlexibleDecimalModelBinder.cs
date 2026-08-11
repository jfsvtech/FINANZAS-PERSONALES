using System.Globalization;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace FinanzasPersonales.Web.Models;

public class FlexibleDecimalModelBinderProvider : IModelBinderProvider
{
    public IModelBinder? GetBinder(ModelBinderProviderContext context)
    {
        var type = Nullable.GetUnderlyingType(context.Metadata.ModelType) ?? context.Metadata.ModelType;
        return type == typeof(decimal) ? new FlexibleDecimalModelBinder() : null;
    }
}

public class FlexibleDecimalModelBinder : IModelBinder
{
    public Task BindModelAsync(ModelBindingContext bindingContext)
    {
        var valueProviderResult = bindingContext.ValueProvider.GetValue(bindingContext.ModelName);
        if (valueProviderResult == ValueProviderResult.None) return Task.CompletedTask;

        bindingContext.ModelState.SetModelValue(bindingContext.ModelName, valueProviderResult);
        var raw = valueProviderResult.FirstValue?.Trim();
        var nullable = Nullable.GetUnderlyingType(bindingContext.ModelType) != null;

        if (string.IsNullOrWhiteSpace(raw))
        {
            if (nullable)
            {
                bindingContext.Result = ModelBindingResult.Success(null);
                return Task.CompletedTask;
            }

            bindingContext.ModelState.TryAddModelError(bindingContext.ModelName, "El valor numerico es obligatorio.");
            return Task.CompletedTask;
        }

        if (TryParseFlexibleDecimal(raw, out var value))
        {
            bindingContext.Result = ModelBindingResult.Success(value);
            return Task.CompletedTask;
        }

        bindingContext.ModelState.TryAddModelError(bindingContext.ModelName, "El valor numerico no es valido.");
        return Task.CompletedTask;
    }

    private static bool TryParseFlexibleDecimal(string raw, out decimal value)
    {
        raw = raw.Replace(" ", "").Replace("$", "").Replace("%", "");

        var lastComma = raw.LastIndexOf(',');
        var lastDot = raw.LastIndexOf('.');

        if (lastComma >= 0 && lastDot >= 0)
        {
            raw = lastComma > lastDot
                ? raw.Replace(".", "").Replace(',', '.')
                : raw.Replace(",", "");
        }
        else if (lastComma >= 0)
        {
            raw = raw.Replace(".", "").Replace(',', '.');
        }
        else if (lastDot >= 0)
        {
            var decimals = raw.Length - lastDot - 1;
            var integerPart = raw[..lastDot].Replace(".", "");
            var decimalPart = raw[(lastDot + 1)..];
            var looksLikeThousands = decimals == 3 && integerPart.Length > 0;
            raw = looksLikeThousands ? integerPart + decimalPart : integerPart + "." + decimalPart;
        }

        return decimal.TryParse(raw, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
    }
}
