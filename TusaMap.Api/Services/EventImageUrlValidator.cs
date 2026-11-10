namespace TusaMap.Api.Services;

public static class EventImageUrlValidator
{
    public const int MaxDataUrlLength = 600_000;
    public const int MaxBatchImageDataLength = 8_000_000;
    private const int MaxRemoteUrlLength = 1500;
    private const int MaxImageBytes = 400 * 1024;
    private const string DataUrlPrefix = "data:image/webp;base64,";

    public static bool IsSupported(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (value.StartsWith(DataUrlPrefix, StringComparison.Ordinal))
            return IsValidWebpDataUrl(value);
        if (value.Length > MaxRemoteUrlLength) return false;

        return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
               uri.Scheme == Uri.UriSchemeHttps &&
               uri.IsDefaultPort &&
               string.IsNullOrEmpty(uri.UserInfo) &&
               string.IsNullOrEmpty(uri.Fragment);
    }

    private static bool IsValidWebpDataUrl(string value)
    {
        if (value.Length > MaxDataUrlLength) return false;
        var payload = value[DataUrlPrefix.Length..];
        if (payload.Length == 0) return false;

        try
        {
            var bytes = Convert.FromBase64String(payload);
            return bytes.Length <= MaxImageBytes &&
                   bytes.Length >= 12 &&
                   bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) &&
                   bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
