namespace TechStore.Application.Common;

// GuestKey is a hash of a server-generated secret, never a client-supplied cart ID.
public sealed record CartOwner(string? UserId, string? GuestKey)
{
    public static CartOwner User(string id) => new(id, null);
    public static CartOwner Guest(string key) => new(null, key);
}
