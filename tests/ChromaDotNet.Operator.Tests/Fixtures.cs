using ChromaDotNet.Operator.Entities;
using k8s;
using k8s.Models;
using KubeOps.KubernetesClient;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace ChromaDotNet.Operator.Tests;

internal static class Fixtures
{
	public const string Image159 = "docker.io/chromadb/chroma:1.5.9";
	public const string Image153 = "docker.io/chromadb/chroma:1.5.3";
	public static readonly DateTime Now = new(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);
	public static readonly OperatorOptions Defaults = new();

	public static V1Alpha1ChromaServer Server(string image = Image159, string? dataVersion = null, string? runningImage = null)
	{
		var server = new V1Alpha1ChromaServer
		{
			Metadata = new V1ObjectMeta { Name = "demo", NamespaceProperty = "rag", Uid = "uid-1", Generation = 3 },
			Spec = new V1Alpha1ChromaServer.ServerSpec { Image = image, Storage = new V1Alpha1ChromaServer.StorageSpec { Size = "1Gi" } },
		};
		if (dataVersion is not null || runningImage is not null)
			server.Status = new V1Alpha1ChromaServer.ServerStatus { DataVersion = dataVersion, Image = runningImage, Phase = "Ready" };
		return server;
	}

	/// <summary>The Pod the operator would create, as the kubelet reports it once it runs.</summary>
	public static V1Pod RunningPod(V1Alpha1ChromaServer server, string image, bool ready = true)
	{
		var pod = Resources.Pod(server, image);
		pod.Status = new V1PodStatus
		{
			Phase = "Running",
			Conditions = [new V1PodCondition { Type = "Ready", Status = ready ? "True" : "False" }],
			ContainerStatuses =
			[
				new V1ContainerStatus
				{
					Name = "chroma",
					Image = image,
					ImageID = "id",
					Ready = ready,
					Started = true,
					RestartCount = 0,
					State = new V1ContainerState { Running = new V1ContainerStateRunning() },
				},
			],
		};
		return pod;
	}

	public static V1Condition Condition(Outcome outcome, string type) => outcome.Status.Conditions.Single(c => c.Type == type);

	/// <summary>A logger that keeps the ids of the events it gets.</summary>
	public sealed class ListLogger<T> : ILogger<T>
	{
		public List<int> Events { get; } = [];

		public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

		public bool IsEnabled(LogLevel logLevel) => true;

		public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
			Events.Add(eventId.Id);
	}

	/// <summary>The Pods a controller with a fake client deleted: it deletes them through the API client, with preconditions.</summary>
	public static int PodDeletes(IKubernetesClient client) =>
		client.ApiClient.CoreV1.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(ICoreV1Operations.DeleteNamespacedPodWithHttpMessagesAsync));
}
