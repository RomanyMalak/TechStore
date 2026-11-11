namespace TechStore.Application.Exceptions;

public sealed class NotFoundException : Exception
{
    public NotFoundException(string resource) : base($"{resource} was not found.") { }
    public NotFoundException(string resource, int id) : base($"{resource} with id '{id}' was not found.") { }
}
