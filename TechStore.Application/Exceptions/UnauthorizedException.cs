namespace TechStore.Application.Exceptions;

public sealed class UnauthorizedException : Exception
{
    public UnauthorizedException() : base("Invalid email or password.") { }
}
