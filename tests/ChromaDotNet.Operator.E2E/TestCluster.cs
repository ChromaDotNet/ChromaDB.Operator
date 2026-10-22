using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using ChromaDotNet.Operator.Entities;
using k8s;
using k8s.Autorest;
using k8s.Models;
using KubeOps.KubernetesClient;
using NUnit.Framework;

namespace ChromaDotNet.Operator.E2E;

/// <summary>
/// The test cluster, with the operator installed (build/e2e.sh). Its kubeconfig and its context are always given:
/// CHROMA_OPERATOR_KUBECONFIG and CHROMA_OPERATOR_CONTEXT, never the current context of the machine, which can be a real cluster.
/// Each test works in a namespace of its own, deleted at the end.
/// </summary>
internal sealed class TestCluster : IDisposable
{
	public static readonly TimeSpan ReadyTimeout = TimeSpan.FromMinutes(3);

	private readonly string _kubeconfig;
	private readonly string _context;

	public IKubernetesClient Client { get; }

	public IKubernetes Api { get; }

	public string Namespace { get; }

	private TestCluster(string kubeconfig, string context)
	{
		_kubeconfig = kubeconfig;
		_context = context;
		var config = KubernetesClientConfiguration.BuildConfigFromConfigFile(kubeconfig, currentContext: context);
		Api = new Kubernetes(config);
		Client = new KubernetesClient(config, Api);
		Namespace = "op-e2e-" + Guid.NewGuid().ToString("N")[..8];
	}

	public static async Task<TestCluster> CreateAsync()
	{
		var kubeconfig = Environment.GetEnvironmentVariable("CHROMA_OPERATOR_KUBECONFIG");
		var context = Environment.GetEnvironmentVariable("CHROMA_OPERATOR_CONTEXT");
		if (string.IsNullOrEmpty(kubeconfig) || string.IsNullOrEmpty(context))
			Assert.Ignore("CHROMA_OPERATOR_KUBECONFIG and CHROMA_OPERATOR_CONTEXT choose the test cluster: build/e2e.sh sets them.");
		var cluster = new TestCluster(kubeconfig!, context!);
		await cluster.Api.CoreV1.CreateNamespaceAsync(new V1Namespace { Metadata = new V1ObjectMeta { Name = cluster.Namespace } });
		return cluster;
	}

	public void Dispose()
	{
		try
		{
			Api.CoreV1.DeleteNamespace(Namespace);
		}
		catch (HttpOperationException)
		{
		}
		Api.Dispose();
		Client.Dispose();
	}

	public static V1Alpha1ChromaServer NewServer(string name, string image) => new()
	{
		Metadata = new V1ObjectMeta { Name = name },
		Spec = new V1Alpha1ChromaServer.ServerSpec
		{
			Image = image,
			Storage = new V1Alpha1ChromaServer.StorageSpec { Size = "1Gi" },
			Resources = new V1ResourceRequirements
			{
				Requests = new Dictionary<string, ResourceQuantity> { ["cpu"] = new("100m"), ["memory"] = new("256Mi") },
				Limits = new Dictionary<string, ResourceQuantity> { ["memory"] = new("1Gi") },
			},
		},
	};

	public async Task<V1Alpha1ChromaServer> CreateAsync(V1Alpha1ChromaServer server)
	{
		server.Metadata.NamespaceProperty = Namespace;
		return await Client.CreateAsync(server);
	}

	public Task<V1Alpha1ChromaServer?> GetAsync(string name) => Client.GetAsync<V1Alpha1ChromaServer>(name, Namespace);

	public Task<V1Pod?> PodAsync(string name) => Client.GetAsync<V1Pod>(name, Namespace);

	/// <summary>Changes the ChromaServer, read again at each attempt, until the API accepts the update.</summary>
	public async Task<V1Alpha1ChromaServer> UpdateAsync(string name, Action<V1Alpha1ChromaServer> change)
	{
		for (var attempt = 1; ; attempt++)
		{
			var server = (await GetAsync(name))!;
			change(server);
			try
			{
				return await Client.UpdateAsync(server);
			}
			catch (HttpOperationException e) when (e.Response.StatusCode == HttpStatusCode.Conflict && attempt < 10)
			{
			}
		}
	}

	/// <summary>Waits until the ChromaServer satisfies the condition, and returns it; fails with its status after the timeout.</summary>
	public async Task<V1Alpha1ChromaServer> WaitAsync(string name, Func<V1Alpha1ChromaServer, bool> condition, TimeSpan? timeout = null, string? what = null)
	{
		var watch = Stopwatch.StartNew();
		V1Alpha1ChromaServer? server = null;
		while (watch.Elapsed < (timeout ?? ReadyTimeout))
		{
			server = await GetAsync(name);
			if (server is not null && condition(server))
			{
				TestContext.Out.WriteLine($"{name}: {what ?? "condition"} after {watch.Elapsed.TotalSeconds:0.0} s");
				return server;
			}
			await Task.Delay(500);
		}
		Assert.Fail($"{name}: {what ?? "condition"} not met in {watch.Elapsed.TotalSeconds:0} s; status {KubernetesJson.Serialize(server?.Status)}");
		return null!;
	}

	public Task<V1Alpha1ChromaServer> WaitReadyAsync(string name, string image) =>
		WaitAsync(name, s => s.Status?.Phase == "Ready" && s.Status.Image == image && s.Status.ObservedGeneration == s.Metadata.Generation,
			what: $"Ready on {image}");

	/// <summary>
	/// A port-forward of kubectl to the Service of the ChromaServer, with the kubeconfig and the context of the test cluster. A
	/// port-forward follows one Pod: open a new one after the Pod changes.
	/// </summary>
	public async Task<PortForward> ForwardAsync(string server)
	{
		var port = FreePort();
		var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("CHROMA_OPERATOR_KUBECTL") ?? "kubectl")
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
		};
		foreach (var argument in new[] { "--kubeconfig", _kubeconfig, "--context", _context, "-n", Namespace, "port-forward", $"svc/{server}-rw", $"{port}:8000" })
			start.ArgumentList.Add(argument);
		var process = Process.Start(start)!;
		try
		{
			var line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
			if (line is null || !line.StartsWith("Forwarding from", StringComparison.Ordinal))
				Assert.Fail($"port-forward: {line}");
			return new PortForward(process, new Uri($"http://127.0.0.1:{port}"));
		}
		catch
		{
			// The PortForward that would own it does not exist: the process ends here.
			new PortForward(process, new Uri($"http://127.0.0.1:{port}")).Dispose();
			throw;
		}
	}

	private static int FreePort()
	{
		var listener = new TcpListener(IPAddress.Loopback, 0);
		listener.Start();
		var port = ((IPEndPoint)listener.LocalEndpoint).Port;
		listener.Stop();
		return port;
	}
}

internal sealed class PortForward(Process process, Uri uri) : IDisposable
{
	public Uri Uri { get; } = uri;

	public void Dispose()
	{
		try
		{
			process.Kill();
		}
		catch (InvalidOperationException)
		{
		}
		process.Dispose();
	}
}
