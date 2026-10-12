using System.Net.Http.Json;
using ModularMonolith.Contracts;

namespace ModularMonolith.Host;

internal interface IRendererClient
{
    Task<string> RenderSvgAsync(GridSettings settings, CancellationToken cancellationToken);
    Task<string> RenderPageAsync(GridSettings settings, string svg, CancellationToken cancellationToken);
}

internal sealed class RendererClient(HttpClient httpClient) : IRendererClient
{
    public async Task<string> RenderSvgAsync(GridSettings settings, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync("/render/svg", settings, ContractJson.Options, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    public async Task<string> RenderPageAsync(GridSettings settings, string svg, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync(
            "/render/page",
            new RenderPageRequest(settings, svg),
            ContractJson.Options,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }
}

internal interface IImageCacheClient
{
    Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken);
    Task PutAsync(string key, byte[] svg, CancellationToken cancellationToken);
}

internal sealed class ImageCacheClient(HttpClient httpClient) : IImageCacheClient
{
    public async Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync($"/images/{Uri.EscapeDataString(key)}", cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    public async Task PutAsync(string key, byte[] svg, CancellationToken cancellationToken)
    {
        using var content = new ByteArrayContent(svg);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/svg+xml");
        using var response = await httpClient.PutAsync($"/images/{Uri.EscapeDataString(key)}", content, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
