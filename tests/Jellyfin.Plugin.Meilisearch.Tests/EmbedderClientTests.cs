using System.Net;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.Meilisearch.Semantic;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Meilisearch.Tests;

public class EmbedderClientTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        public int Calls;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return await respond(request);
        }
    }

    private static HttpResponseMessage VectorResponse(int count)
    {
        var vec = Enumerable.Repeat(0.05f, EmbedderClient.Dimension).ToArray();
        var body = JsonSerializer.Serialize(new
        {
            vectors = Enumerable.Repeat(vec, count).ToArray(),
            model = "bge-small-en-v1.5-int8/1",
            dim = EmbedderClient.Dimension,
        });
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }

    private static EmbedderClient Client(FakeHandler handler) =>
        new(new HttpClient(handler), "http://sidecar:8000/", NullLogger<EmbedderClient>.Instance);

    [Fact]
    public async Task Query_embedding_roundtrip_and_cache_hit()
    {
        var handler = new FakeHandler(_ => Task.FromResult(VectorResponse(1)));
        var client = Client(handler);

        var v1 = await client.EmbedQueryAsync("feel good movies");
        var v2 = await client.EmbedQueryAsync("feel good movies");

        Assert.NotNull(v1);
        Assert.Equal(EmbedderClient.Dimension, v1!.Length);
        Assert.Same(v1, v2);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Http_error_returns_null()
    {
        var handler = new FakeHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        Assert.Null(await Client(handler).EmbedQueryAsync("q"));
    }

    [Fact]
    public async Task Timeout_returns_null_not_throws()
    {
        var handler = new FakeHandler(async _ =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            return VectorResponse(1);
        });
        Assert.Null(await Client(handler).EmbedQueryAsync("slow"));
    }

    [Fact]
    public async Task Wrong_dimension_returns_null()
    {
        var handler = new FakeHandler(_ =>
        {
            var body = JsonSerializer.Serialize(new
            {
                vectors = new[] { new float[] { 1, 2, 3 } },
                model = "m",
                dim = 3,
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        });
        Assert.Null(await Client(handler).EmbedQueryAsync("q"));
    }

    [Fact]
    public async Task Passages_batch_over_64_splits_requests()
    {
        var perRequestCounts = new List<int>();
        var handler = new FakeHandler(async req =>
        {
            var payload = JsonDocument.Parse(await req.Content!.ReadAsStringAsync());
            var n = payload.RootElement.GetProperty("texts").GetArrayLength();
            lock (perRequestCounts) perRequestCounts.Add(n);
            return VectorResponse(n);
        });

        var result = await Client(handler).EmbedPassagesAsync(
            Enumerable.Range(0, 150).Select(i => $"passage {i}").ToList());

        Assert.NotNull(result);
        Assert.Equal(150, result!.Count);
        Assert.Equal([64, 64, 22], perRequestCounts);
    }

    [Fact]
    public async Task Passages_failure_mid_batch_returns_null()
    {
        var call = 0;
        var handler = new FakeHandler(_ =>
        {
            call++;
            return Task.FromResult(call == 1
                ? VectorResponse(64)
                : new HttpResponseMessage(HttpStatusCode.BadGateway));
        });

        Assert.Null(await Client(handler).EmbedPassagesAsync(
            Enumerable.Range(0, 100).Select(i => i.ToString()).ToList()));
    }
}
