using FinanzasPersonales.Web.Services;

namespace FinanzasPersonales.Web.Middleware;

public class TraduccionHtmlMiddleware
{
    private const string CookieIdioma = "FinanzasPersonales.Idioma";
    private readonly RequestDelegate _next;

    public TraduccionHtmlMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, TraduccionService traduccion)
    {
        var originalBody = context.Response.Body;
        await using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        await _next(context);

        buffer.Position = 0;
        var contentType = context.Response.ContentType ?? "";
        if (!contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
        {
            await buffer.CopyToAsync(originalBody);
            context.Response.Body = originalBody;
            return;
        }

        using var reader = new StreamReader(buffer);
        var html = await reader.ReadToEndAsync();
        var idioma = ResolverIdioma(context, traduccion);
        html = html.Replace("<html lang=\"es\"", $"<html lang=\"{idioma}\"", StringComparison.OrdinalIgnoreCase);
        html = traduccion.TraducirHtmlVisible(html, idioma);

        context.Response.Body = originalBody;
        context.Response.ContentLength = null;
        await context.Response.WriteAsync(html);
    }

    private static string ResolverIdioma(HttpContext context, TraduccionService traduccion)
    {
        if (context.User.Identity?.IsAuthenticated == true)
            return traduccion.IdiomaActual(context.User);

        if (context.Request.Query.TryGetValue("lang", out var queryLang))
        {
            var normalizado = TraduccionService.NormalizarIdioma(queryLang.ToString());
            context.Response.Cookies.Append(CookieIdioma, normalizado, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                Secure = context.Request.IsHttps,
                Expires = DateTimeOffset.UtcNow.AddYears(1)
            });
            return normalizado;
        }

        if (context.Request.Cookies.TryGetValue(CookieIdioma, out var cookieLang))
            return TraduccionService.NormalizarIdioma(cookieLang);

        var accept = context.Request.Headers.AcceptLanguage.ToString();
        if (accept.StartsWith("en", StringComparison.OrdinalIgnoreCase)) return "en";
        if (accept.StartsWith("pt", StringComparison.OrdinalIgnoreCase)) return "pt";
        return "es";
    }
}
