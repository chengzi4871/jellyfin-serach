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

    [Fact]
    public async Task SearchOptionalTreatsMissingCoverCollectionAsEmpty()
    {
        using var http = new HttpClient(new MissingCollectionHandler());
        var configuration = new PluginConfiguration { QdrantUrl = "http://qdrant.test" };
        var client = new VisualSearchClient(http, () => configuration);

        var hits = await client.SearchOptionalAsync(new[] { 0.1f, 0.2f }, "jellyfin_video_covers", 10, CancellationToken.None);

        Assert.Empty(hits);
    }

    [Fact]
    public async Task IndexedSignatureReadsStableHashesFromQdrantPayload()
    {
        const string response = """
        {
          "result": [{
            "id": "11111111-1111-1111-1111-111111111111",
            "payload": {"itemId":"11111111-1111-1111-1111-111111111111","textHash":"text-v2","visualHash":"visual-v2","hashVersion":2}
          }]
        }
        """;
        using var http = new HttpClient(new StubHandler(response));
        var configuration = new PluginConfiguration { QdrantUrl = "http://qdrant.test" };
        var client = new VisualSearchClient(http, () => configuration);

        var signature = await client.GetIndexedSignatureAsync("11111111-1111-1111-1111-111111111111", CancellationToken.None);

        Assert.NotNull(signature);
        Assert.Equal("text-v2", signature!.TextHash);
        Assert.Equal("visual-v2", signature.VisualHash);
    }

    [Fact]
    public void GroupedSearchPayloadIsParsedIntoVideoGroups()
    {
        using var document = JsonDocument.Parse("""
        {
          "result": {
            "groups": [
              {
                "id": "11111111-1111-1111-1111-111111111111",
                "hits": [
                  {"id":"frame-2","score":0.93,"payload":{"itemId":"11111111-1111-1111-1111-111111111111","frameIndex":2}},
                  {"id":"frame-1","score":0.88,"payload":{"itemId":"11111111-1111-1111-1111-111111111111","frameIndex":1}}
                ]
              }
            ]
          }
        }
        """);

        var groups = VisualSearchClient.ParseGroupedSearchHits(document.RootElement, 4);

        var group = Assert.Single(groups);
        Assert.Equal("11111111-1111-1111-1111-111111111111", group.ItemId);
        Assert.Equal(2, group.Hits.Count);
        Assert.Equal(2, group.Hits[0].Payload.GetProperty("frameIndex").GetInt32());
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

    private sealed class MissingCollectionHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("collection not found", Encoding.UTF8, "text/plain")
            });
    }
}
