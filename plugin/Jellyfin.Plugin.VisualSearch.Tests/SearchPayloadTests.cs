using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.VisualSearch;
using Xunit;

namespace Jellyfin.Plugin.VisualSearch.Tests;

public sealed class SearchPayloadTests
{
    [Fact]
    public void SearchPayloadCanBeReadAfterResponseDocumentIsDisposed()
    {
        IReadOnlyList<RemoteSearchHit> hits;
        using (var document = JsonDocument.Parse("""
        {
          "result": [
            {
              "id": "point-1",
              "score": 0.91,
              "payload": {
                "itemId": "11111111-1111-1111-1111-111111111111",
                "frameIndex": 7,
                "timestampMs": 42000,
                "libraryId": "22222222-2222-2222-2222-222222222222"
              }
            }
          ]
        }
        """))
        {
            hits = VisualSearchClient.ParseSearchHits(document.RootElement);
        }

        Assert.Single(hits);
        Assert.Equal("11111111-1111-1111-1111-111111111111", hits[0].ItemId);
        Assert.Equal(0.91, hits[0].Score, precision: 10);
        Assert.Equal(7, hits[0].Payload.GetProperty("frameIndex").GetInt32());
        Assert.Equal(42000, hits[0].Payload.GetProperty("timestampMs").GetInt64());
    }

    [Fact]
    public async Task SearchAsyncKeepsPayloadReadableForNonEmptyQdrantResponse()
    {
        const string response = """
        {
          "result": [
            {
              "id": "point-1",
              "score": 0.91,
              "payload": {
                "itemId": "11111111-1111-1111-1111-111111111111",
                "frameIndex": 3,
                "timestampMs": 18000
              }
            }
          ]
        }
        """;
        using var http = new HttpClient(new StubHandler(response));
        var configuration = new PluginConfiguration { QdrantUrl = "http://qdrant.test" };
        var client = new VisualSearchClient(http, () => configuration);

        var hits = await client.SearchAsync(new[] { 0.1f, 0.2f }, "jellyfin_video_frames", 10, CancellationToken.None);

        Assert.Single(hits);
        Assert.Equal(3, hits[0].Payload.GetProperty("frameIndex").GetInt32());
        Assert.Equal(18000, hits[0].Payload.GetProperty("timestampMs").GetInt64());
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _response;

        public StubHandler(string response) => _response = response;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_response, Encoding.UTF8, "application/json")
            });
    }
}
