using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Deskspan;

internal static class PairDirectory
{
    public const string DefaultServer = "https://deskspan.bdebtech.in";
    public const string ProPage = "https://deskspan.bdebtech.in/";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };

    public static async Task PublishPairAsync(string server, string code, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Api(server, "/api/v1/pair"))
            {
                Content = new StringContent(JsonSerializer.Serialize(new { code }), Encoding.UTF8, "application/json")
            };
            request.Headers.UserAgent.ParseAdd("Deskspan/0.0.2");
            using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    public static async Task WithdrawPairAsync(string server, string code)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, Api(server, "/api/v1/pair/" + Uri.EscapeDataString(code)));
            request.Headers.UserAgent.ParseAdd("Deskspan/0.0.2");
            using var response = await Http.SendAsync(request).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    public static async Task<string?> FindPairAsync(string server, string code, CancellationToken cancellationToken)
    {
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(TimeSpan.FromSeconds(4));
            using var request = new HttpRequestMessage(HttpMethod.Get, Api(server, "/api/v1/pair/" + Uri.EscapeDataString(code)));
            request.Headers.UserAgent.ParseAdd("Deskspan/0.0.2");
            using var response = await Http.SendAsync(request, limit.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(limit.Token).ConfigureAwait(false));
            return document.RootElement.TryGetProperty("address", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static Uri Api(string server, string path) => new(server.TrimEnd('/') + path);
}
