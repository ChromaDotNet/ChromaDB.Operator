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

	public const string OperatorNamespace = "chroma-operator-system";

	/// <summary>The addresses k3d gives the API server of a cluster on this machine.</summary>
	private static readonly HashSet<string> LocalHosts = ["0.0.0.0", "127.0.0.1", "localhost", "host.docker.internal", "[::1]"];

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
		// The tests create and delete namespaces: only on a local k3d cluster, whatever the variables say. A context can be renamed, and a
		// local address can be a tunnel to another cluster: the API server must be on this machine, on the port Docker publishes for the
		// load balancer of that k3d cluster.
		if (!context!.StartsWith("k3d-", StringComparison.Ordinal))
			Assert.Fail($"CHROMA_OPERATOR_CONTEXT is {context}: the tests run only on the context of a k3d cluster (k3d-*).");
		var cluster = new TestCluster(kubeconfig!, context!);
		var host = cluster.Api.BaseUri.Host;
		if (!LocalHosts.Contains(host))
		{
			cluster.Dispose();
			Assert.Fail($"The API server of {context} is on {host}: the tests run only on a cluster on this machine.");
		}
		var name = context["k3d-".Length..];
		if (!PublishedPorts(name).Contains(cluster.Api.BaseUri.Port))
		{
			cluster.Dispose();
			Assert.Fail($"{cluster.Api.BaseUri} is not the API of the k3d cluster {name} (docker port k3d-{name}-serverlb 6443/tcp).");
		}
		await cluster.Api.CoreV1.CreateNamespaceAsync(new V1Namespace { Metadata = new V1ObjectMeta { Name = cluster.Namespace } });
		return cluster;
	}

	/// <summary>The ports Docker publishes for the API of a k3d cluster, on its load balancer; none when there is no such cluster.</summary>
	private static List<int> PublishedPorts(string cluster)
	{
		var start = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
		foreach (var argument in new[] { "port", $"k3d-{cluster}-serverlb", "6443/tcp" })
			start.ArgumentList.Add(argument);
		using var process = Process.Start(start)!;
		var output = process.StandardOutput.ReadToEnd();
		process.WaitForExit();
		return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Select(line => int.TryParse(line[(line.LastIndexOf(':') + 1)..], out var port) ? port : -1)
			.ToList();
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

	/// <summary>
	/// Waits until the ChromaServer satisfies the condition, and returns it; fails after the timeout with its status, the status of its
	/// Pod, and the events of the namespace, so that a Pod that does not start can be told from an operator that does not act.
	/// </summary>
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
		var pod = await PodAsync(name + "-1");
		var events = (await Api.CoreV1.ListNamespacedEventAsync(Namespace)).Items
			.OrderBy(e => e.LastTimestamp ?? e.EventTime)
			.Select(e => $"{e.InvolvedObject.Kind}/{e.InvolvedObject.Name} {e.Reason}: {e.Message}")
			.TakeLast(15);
		Assert.Fail($"{name}: {what ?? "condition"} not met in {watch.Elapsed.TotalSeconds:0} s; status {KubernetesJson.Serialize(server?.Status)}; " +
			$"Pod {KubernetesJson.Serialize(pod?.Status)}; events:\n{string.Join('\n', events)}");
		return null!;
	}

	/// <summary>Waits until the object is gone; fails after two minutes.</summary>
	public async Task WaitGoneAsync<T>(string name) where T : class, IKubernetesObject<V1ObjectMeta>
	{
		var watch = Stopwatch.StartNew();
		while (await Client.GetAsync<T>(name, Namespace) is not null)
		{
			if (watch.Elapsed > TimeSpan.FromMinutes(2))
				Assert.Fail($"{typeof(T).Name} {name} still there after {watch.Elapsed.TotalSeconds:0} s");
			await Task.Delay(500);
		}
	}

	/// <summary>
	/// After the deletion of a ChromaServer: once the garbage collector has deleted the objects it owned, the claim is still there, not
	/// being deleted, and without owners. A claim the garbage collector deletes is marked for deletion before the Pod that uses it is gone.
	/// </summary>
	public async Task AssertClaimKeptAsync(string server)
	{
		await WaitGoneAsync<V1Pod>(server + "-1");
		await WaitGoneAsync<V1Service>(server + "-rw");
		await WaitGoneAsync<V1ConfigMap>(server + "-config");
		var claim = await Client.GetAsync<V1PersistentVolumeClaim>(server + "-1", Namespace);
		Assert.That(claim, Is.Not.Null, "the claim is kept");
		Assert.That(claim!.Metadata.DeletionTimestamp, Is.Null, "the claim is not being deleted");
		Assert.That(claim.Metadata.OwnerReferences ?? [], Is.Empty, "the claim has no owner");
	}

	/// <summary>
	/// The holder of the Lease of the operator, once it is one of its running Pods other than <paramref name="except"/>: the identity of a
	/// holder is the name of its Pod.
	/// </summary>
	public async Task<string> WaitLeaseHolderAsync(string? except)
	{
		var watch = Stopwatch.StartNew();
		string? holder = null;
		while (watch.Elapsed < TimeSpan.FromMinutes(2))
		{
			holder = (await Api.CoordinationV1.ReadNamespacedLeaseAsync("chroma-operator-leader", OperatorNamespace)).Spec.HolderIdentity;
			var pods = (await Api.CoreV1.ListNamespacedPodAsync(OperatorNamespace, labelSelector: "operator=chroma-operator")).Items;
			if (holder is not null && holder != except && pods.Any(p => p.Name() == holder && p.Metadata.DeletionTimestamp is null && p.Status?.Phase == "Running"))
			{
				TestContext.Out.WriteLine($"operator: {holder} holds the Lease after {watch.Elapsed.TotalSeconds:0.0} s");
				return holder;
			}
			await Task.Delay(500);
		}
		Assert.Fail($"no running operator other than {except} holds the Lease; holder {holder}");
		return null!;
	}

	/// <summary>The running Pods of the operator whose log has the text.</summary>
	public async Task<List<string>> OperatorPodsThatLoggedAsync(string text)
	{
		var found = new List<string>();
		foreach (var pod in (await Api.CoreV1.ListNamespacedPodAsync(OperatorNamespace, labelSelector: "operator=chroma-operator")).Items)
		{
			using var stream = await Api.CoreV1.ReadNamespacedPodLogAsync(pod.Name(), OperatorNamespace);
			using var reader = new StreamReader(stream);
			if ((await reader.ReadToEndAsync()).Contains(text, StringComparison.Ordinal))
				found.Add(pod.Name());
		}
		return found;
	}

	/// <summary>
	/// Scales the Deployment of the operator and waits until that many Pods of it are ready, or none is left. The tests run one at a time:
	/// while the operator is stopped, no other test needs it.
	/// </summary>
	public async Task ScaleOperatorAsync(int replicas)
	{
		const string ns = OperatorNamespace, deployment = "chroma-operator-operator";
		var patch = new V1Patch($"{{\"spec\":{{\"replicas\":{replicas}}}}}", V1Patch.PatchType.MergePatch);
		await Api.AppsV1.PatchNamespacedDeploymentScaleAsync(patch, deployment, ns);
		var watch = Stopwatch.StartNew();
		while (watch.Elapsed < ReadyTimeout)
		{
			var pods = (await Api.CoreV1.ListNamespacedPodAsync(ns, labelSelector: "operator=chroma-operator")).Items;
			var ready = pods.Count(p => p.Metadata.DeletionTimestamp is null && Reconciliation.IsReady(p));
			if (replicas == 0 ? pods.Count == 0 : ready == replicas && pods.Count == replicas)
				return;
			await Task.Delay(500);
		}
		Assert.Fail($"the operator did not reach {replicas} Pods");
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
