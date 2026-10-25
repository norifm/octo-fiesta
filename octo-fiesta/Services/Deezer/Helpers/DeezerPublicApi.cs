using System.Text.Json;

namespace octo_fiesta.Services.Deezer;

/// <summary>
/// Calls to the public Deezer API (api.deezer.com), shared by the metadata and download services.
/// </summary>
public static class DeezerPublicApi
{
    /// <summary>
    /// GETs a Deezer API url with retry + backoff for transient failures. Deezer throttles
    /// bursts either with HTTP 429/403 or - counter-intuitively - an HTTP 200 body carrying
    /// error code 4 ("Quota limit exceeded"). Without a retry a single throttled call silently
    /// drops a track (surfaces upstream as "Song not found"), which is why hearting a large
    /// playlist loses a handful of tracks on the first pass. Returns the response body, or null
    /// if permanently unavailable (e.g. 404 / invalid id) or still throttled after all retries.
    /// </summary>
    public static async Task<string?> GetJsonWithRetryAsync(HttpClient httpClient, string url, CancellationToken cancellationToken = default)
    {
        // The quota is counted over 5 seconds, so the retries must span longer than that
        int[] backoffMs = { 500, 1000, 2000, 4000 };
        for (int attempt = 0; ; attempt++)
        {
            bool transient = false;
            try
            {
                var response = await httpClient.GetAsync(url, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync(cancellationToken);
                    if (!IsQuotaError(json))
                    {
                        return json;
                    }
                    transient = true; // HTTP 200 + Deezer quota error -> retry
                }
                else
                {
                    var status = (int)response.StatusCode;
                    if (status != 429 && status != 403 && status < 500)
                    {
                        return null; // permanent (e.g. 404) -> don't retry
                    }
                    transient = true;
                }
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                transient = true; // network error / timeout -> retry
            }

            if (transient && attempt < backoffMs.Length)
            {
                await Task.Delay(backoffMs[attempt], cancellationToken);
                continue;
            }
            return null;
        }
    }

    /// <summary>
    /// True if the body is a Deezer "Quota limit exceeded" throttling response (error code 4),
    /// which Deezer returns with an HTTP 200 status. Other errors (e.g. "no data") are permanent.
    /// </summary>
    private static bool IsQuotaError(string json)
    {
        try
        {
            var root = JsonDocument.Parse(json).RootElement;
            return root.TryGetProperty("error", out var error)
                && error.TryGetProperty("code", out var code)
                && code.ValueKind == JsonValueKind.Number
                && code.GetInt32() == 4;
        }
        catch
        {
            return false;
        }
    }
}
