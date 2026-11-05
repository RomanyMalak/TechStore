namespace TechStore.Application.Exceptions;

public sealed class ConflictException : Exception
{
    public ConflictException(string message, Exception? innerException = null) : base(message, innerException) { }
}
