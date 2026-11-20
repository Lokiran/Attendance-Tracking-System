using FluentValidation;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Ats.Api.Middleware;

/// <summary>
/// Runs the FluentValidation validator of each action argument (request bodies) and answers 422 with every problem,
/// in Pydantic's shape. Runs after model binding and after authorisation, as FastAPI validates the body after the
/// auth dependency.
/// </summary>
internal sealed class ValidationFilter : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var details = new List<ValidationErrors.Detail>();
        foreach (var argument in context.ActionArguments.Values)
        {
            if (argument is null)
            {
                continue;
            }

            var validatorType = typeof(IValidator<>).MakeGenericType(argument.GetType());
            if (context.HttpContext.RequestServices.GetService(validatorType) is not IValidator validator)
            {
                continue;
            }

            var result = await validator.ValidateAsync(new ValidationContext<object>(argument), context.HttpContext.RequestAborted)
                .ConfigureAwait(false);
            details.AddRange(result.Errors.Select(e => new ValidationErrors.Detail(e.PropertyName, e.ErrorMessage)));
        }

        if (details.Count > 0)
        {
            context.Result = ValidationErrors.Envelope(context.HttpContext, details);
            return;
        }

        await next().ConfigureAwait(false);
    }
}
