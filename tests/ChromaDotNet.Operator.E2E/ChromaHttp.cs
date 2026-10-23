using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using NUnit.Framework;

namespace ChromaDotNet.Operator.E2E;

/// <summary>The few calls of the HTTP API v2 of Chroma the tests need: a collection, records, a count, a query.</summary>
internal sealed class ChromaHttp(Uri uri) : IDisposable
{
	private const string Base = "api/v2/tenants/default_tenant/databases/default_database/";
	private readonly HttpClient _http = new() { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(30) };

	public async Task<bool> AnswersAsync()
	{
		using var response = await _http.GetAsync("api/v2/heartbeat");
		return response.IsSuccessStatusCode;
	}

	public async Task<string> CollectionAsync(string name)
	{
		using var response = await _http.PostAsJsonAsync(Base + "collections", new { name, get_or_create = true });
		Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), await response.Content.ReadAsStringAsync());
		return (await response.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<string>();
	}

	public async Task<HttpStatusCode> AddAsync(string collection, int start, int count, int dimensions = 8)
	{
		var random = new Random(start);
		var body = new
		{
			ids = Enumerable.Range(start, count).Select(i => "id" + i).ToArray(),
			embeddings = Enumerable.Range(start, count).Select(_ => Enumerable.Range(0, dimensions).Select(_ => random.NextSingle()).ToArray()).ToArray(),
		};
		using var response = await _http.PostAsJsonAsync(Base + $"collections/{collection}/add", body);
		return response.StatusCode;
	}

	public async Task<int> CountAsync(string name)
	{
		var id = await CollectionAsync(name);
		return await _http.GetFromJsonAsync<int>(Base + $"collections/{id}/count");
	}

	public async Task<string[]> QueryAsync(string name, int n)
	{
		var id = await CollectionAsync(name);
		using var response = await _http.PostAsJsonAsync(Base + $"collections/{id}/query",
			new { query_embeddings = new[] { Enumerable.Repeat(0.5f, 8).ToArray() }, n_results = n, include = Array.Empty<string>() });
		var json = await response.Content.ReadFromJsonAsync<JsonElement>();
		return json.GetProperty("ids")[0].EnumerateArray().Select(e => e.GetString()!).ToArray();
	}

	public void Dispose() => _http.Dispose();
}
