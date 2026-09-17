using FluentValidation;

namespace AiControlCenter.Orchestrator.Api;

public sealed class ValidationFilter<T>(IValidator<T> validator) : IEndpointFilter where T : class
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var model = context.Arguments.OfType<T>().FirstOrDefault();
        if (model is null) return await next(context);
        var result = await validator.ValidateAsync(model, context.HttpContext.RequestAborted);
        return result.IsValid
            ? await next(context)
            : Results.ValidationProblem(result.Errors.GroupBy(error => error.PropertyName)
                .ToDictionary(group => group.Key, group => group.Select(error => error.ErrorMessage).Distinct().ToArray()));
    }
}
