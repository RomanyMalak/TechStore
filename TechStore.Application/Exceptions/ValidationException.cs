namespace TechStore.Application.Exceptions;

public sealed class ValidationException : Exception
{
    public ValidationException(IReadOnlyDictionary<string, string[]> errors)
        : base("One or more validation errors occurred.") => Errors = errors;

    public ValidationException(string property, string message)
        : this(new Dictionary<string, string[]> { [property] = new[] { message } }) { }

    public IReadOnlyDictionary<string, string[]> Errors { get; }
}
