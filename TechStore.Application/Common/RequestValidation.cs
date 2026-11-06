using FluentValidation;
using TechStore.Domain.Exceptions;
using ValidationException = TechStore.Application.Exceptions.ValidationException;

namespace TechStore.Application.Common;

internal static class RequestValidation
{
    public static async Task ValidateAsync<T>(IValidator<T> validator, T dto, CancellationToken cancellationToken)
        where T : class
    {
        if (dto is null) throw new ValidationException("Request", "Request is required.");
        var result = await validator.ValidateAsync(dto, cancellationToken);
        if (!result.IsValid)
            throw new ValidationException(result.Errors.GroupBy(error => error.PropertyName)
                .ToDictionary(group => group.Key, group => group.Select(error => error.ErrorMessage).Distinct().ToArray()));
    }

    public static T ApplyDomainRules<T>(Func<T> action)
    {
        try { return action(); }
        catch (DomainRuleException exception)
        {
            throw new ValidationException(exception.PropertyName, exception.Message);
        }
    }
}
