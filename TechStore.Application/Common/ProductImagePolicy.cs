namespace TechStore.Application.Common;

public static class ProductImagePolicy
{
    public const long MaximumUploadBytes = 5 * 1024 * 1024;
    public const int MaximumImages = 10;
    public const int MaximumDimension = 4096;
    public const long MaximumPixels = 16_000_000;
    public const string PublicPath = "/api/product-images/";

    public static bool TryGetManagedId(string? url, out Guid id)
    {
        id = default;
        return url is { Length: 57 } && url.StartsWith(PublicPath, StringComparison.Ordinal)
            && url.EndsWith(".webp", StringComparison.Ordinal)
            && Guid.TryParseExact(url.AsSpan(PublicPath.Length, 32), "N", out id)
            && url == PublicPath + id.ToString("N") + ".webp";
    }

    public static bool IsAllowedUrl(string? url)
    {
        if (TryGetManagedId(url, out _)) return true;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo)) return false;
        // Uploaded assets have one canonical relative URL. An absolute alias
        // must not bypass reference accounting and draft-deletion protection.
        return !Uri.UnescapeDataString(uri.AbsolutePath).StartsWith(PublicPath, StringComparison.OrdinalIgnoreCase);
    }
}
