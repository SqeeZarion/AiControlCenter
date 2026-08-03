using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace AiControlCenter.Observability;

//Присвоює кожному HTTP-запиту унікальний ідентифікатор.
//Цей ID допомагає знайти всі логи, які належать одному запиту.

public sealed class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public const string HeaderName = "X-Correlation-ID";

    public async Task InvokeAsync(HttpContext context)
    {
        // шукає X-Correlation-ID у вхідному запиті;
        // передає його в Normalize;
        // якщо ID відсутній або неправильний — використовує стандартний TraceIdentifier.
        var correlationId = Normalize(context.Request.Headers[HeaderName].FirstOrDefault())
            ?? context.TraceIdentifier;

        //Встановлює ID для поточного запиту ASP.NET Core.
        context.TraceIdentifier = correlationId;
        //Додає той самий ID до HTTP-відповіді.
        context.Response.Headers[HeaderName] = correlationId;

        //Додавання ID до логів
        using (logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = correlationId,
        }))
        {
            await next(context);
        }
    }

//Перевіряє ID, отриманий від користувача.
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= 128 && trimmed.All(character => !char.IsControl(character))
            ? trimmed
            : null;
    }
}
