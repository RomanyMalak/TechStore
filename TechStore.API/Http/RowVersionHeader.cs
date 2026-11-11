using TechStore.Application.Exceptions;

namespace TechStore.API.Http;

public static class RowVersionHeader
{
    public static byte[] ParseRequired(string? value)
    {
        const string message = "If-Match must contain the product's eight-byte RowVersion as Base64, optionally enclosed in double quotes.";
        if (string.IsNullOrWhiteSpace(value)) throw new ValidationException("If-Match", message);
        value = value.Trim();
        if (value.StartsWith("W/", StringComparison.OrdinalIgnoreCase) || value.Contains(',') || value == "*")
            throw new ValidationException("If-Match", message);
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') value = value[1..^1];
        try
        {
            var version = Convert.FromBase64String(value);
            return version.Length == 8 ? version : throw new ValidationException("If-Match", message);
        }
        catch (FormatException) { throw new ValidationException("If-Match", message); }
    }

    public static void SetETag(HttpResponse response, byte[] rowVersion)
    {
        if (rowVersion.Length == 8) response.Headers.ETag = '"' + Convert.ToBase64String(rowVersion) + '"';
    }
}
