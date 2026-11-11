namespace TechStore.Application.Exceptions;

public sealed class CommerceConflictException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
