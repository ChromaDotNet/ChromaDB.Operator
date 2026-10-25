using System.Text;
using ChromaDotNet.Operator.Distributed;
using ChromaDotNet.Operator.Entities;
using k8s;
using k8s.Autorest;
using k8s.Models;
using KubeOps.KubernetesClient;
using NSubstitute;

namespace ChromaDotNet.Operator.Tests;

/// <summary>
/// The objects of one namespace behind a fake client, for the passes on a distributed Chroma: lists return copies, server-side apply
/// stores the object and raises its generation, and the status written is kept. The checks of Postgres and the bucket pass unless told.
/// </summary>
internal sealed class FakeCluster
{
	public IKubernetesClient Client { get; } = Substitute.For<IKubernetesClient>();

	public IDependencyChecks Checks { get; } = Substitute.For<IDependencyChecks>();

	public Dictionary<(string Kind, string Name), IKubernetesObject<V1ObjectMeta>> Objects { get; } = [];

	public List<string> Applied { get; } = [];

	public List<string> Created { get; } = [];

	public List<V1Pod> Pods { get; } = [];

	public List<V1Secret> Secrets { get; } = [];

	public List<V1Alpha1Chroma> Chromas { get; } = [];

	public V1Alpha1Chroma.ChromaStatus? Status { get; private set; }

	public FakeCluster()
	{
		Setup<V1ServiceAccount>("ServiceAccount");
		Setup<V1Role>("Role");
		Setup<V1RoleBinding>("RoleBinding");
		Setup<V1MemberList>("MemberList");
		Setup<V1ConfigMap>("ConfigMap");
		Setup<V1Service>("Service");
		Setup<V1Job>("Job");
		Setup<V1Deployment>("Deployment");
		Setup<V1StatefulSet>("StatefulSet");
		Client.ListAsync<V1Pod>(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(_ => (IList<V1Pod>)Pods.ToList());
		Client.ListAsync<V1Alpha1Chroma>(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(_ => (IList<V1Alpha1Chroma>)Chromas.ToList());
		Client.GetAsync<V1Secret>(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(call => Secrets.FirstOrDefault(s => s.Name() == call.ArgAt<string>(0)));
		Client.UpdateStatusAsync(Arg.Any<V1Alpha1Chroma>(), Arg.Any<CancellationToken>()).Returns(call =>
		{
			var chroma = call.Arg<V1Alpha1Chroma>();
			Status = Copy(chroma).Status;
			return chroma;
		});
		Checks.PostgresAsync(Arg.Any<PostgresTarget>(), Arg.Any<CancellationToken>()).Returns(new CheckResult(true, "Connected", "Postgres answers."));
		Checks.BucketAsync(Arg.Any<BucketTarget>(), Arg.Any<CancellationToken>()).Returns(new CheckResult(true, "Reachable", "The bucket answers."));
		Client.ApiClient.CoreV1.DeleteNamespacedPodWithHttpMessagesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<V1DeleteOptions>(), Arg.Any<string>(), Arg.Any<int?>(),
				Arg.Any<bool?>(), Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
			.Returns(call =>
			{
				Pods.RemoveAll(p => p.Name() == call.ArgAt<string>(0));
				return new HttpOperationResponse<V1Pod>();
			});
		Client.ApiClient.BatchV1.DeleteNamespacedJobWithHttpMessagesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<V1DeleteOptions>(), Arg.Any<string>(), Arg.Any<int?>(),
				Arg.Any<bool?>(), Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
			.Returns(call =>
			{
				Objects.Remove(("Job", call.ArgAt<string>(0)));
				return new HttpOperationResponse<V1Status>();
			});
		AddSecret("chroma-postgres", ("username", "chroma"), ("password", "s3cret"));
		AddSecret("chroma-storage", ("accessKeyId", "key"), ("secretAccessKey", "secret"));
	}

	public void AddSecret(string name, params (string Key, string Value)[] data)
	{
		Secrets.RemoveAll(s => s.Name() == name);
		Secrets.Add(new V1Secret
		{
			Metadata = new V1ObjectMeta { Name = name, NamespaceProperty = "chroma" },
			Data = data.ToDictionary(d => d.Key, d => Encoding.UTF8.GetBytes(d.Value)),
		});
	}

	public T Get<T>(string kind, string name) where T : IKubernetesObject<V1ObjectMeta> => (T)Objects[(kind, name)];

	/// <summary>The controller of the workload has seen its last spec, and its replicas are updated and ready.</summary>
	public void RollOut(string component, int replicas)
	{
		switch (Objects.GetValueOrDefault(("Deployment", component)) ?? Objects[("StatefulSet", component)])
		{
			case V1Deployment d:
				d.Status = new V1DeploymentStatus { ObservedGeneration = d.Metadata.Generation, Replicas = replicas, UpdatedReplicas = replicas, ReadyReplicas = replicas };
				break;
			case V1StatefulSet s:
				s.Status = new V1StatefulSetStatus { ObservedGeneration = s.Metadata.Generation, Replicas = replicas, UpdatedReplicas = replicas, ReadyReplicas = replicas, CurrentRevision = "r1", UpdateRevision = "r1" };
				break;
		}
	}

	public void CompleteJob(string name, string type = "Complete") =>
		Get<V1Job>("Job", name).Status = new V1JobStatus { Conditions = [new V1JobCondition { Type = type, Status = "True", Message = "done" }] };

	/// <summary>Every component rolled out with its replicas, and the MemberLists listing one member each.</summary>
	public void AllReady(V1Alpha1Chroma chroma)
	{
		foreach (var component in DistributedResources.Tiers.SelectMany(t => t))
			RollOut(component, DistributedResources.Replicas(chroma, component));
		foreach (var (memberList, _, _) in DistributedResources.MemberLists)
			Get<V1MemberList>("MemberList", memberList).Spec.Members = [new V1MemberList.Member { MemberId = memberList + "-0" }];
	}

	public static T Copy<T>(T value) => KubernetesJson.Deserialize<T>(KubernetesJson.Serialize(value));

	private static string? SpecOf(object o) => System.Text.Json.Nodes.JsonNode.Parse(KubernetesJson.Serialize(o))?["spec"]?.ToJsonString();

	private void Setup<T>(string kind) where T : IKubernetesObject<V1ObjectMeta>
	{
		Client.ListAsync<T>(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
			.Returns(_ => (IList<T>)Objects.Values.OfType<T>().Select(Copy).ToList());
		Client.PatchAsync<T>(Arg.Any<V1Patch>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
			.Returns(call =>
			{
				var applied = Copy((T)call.Arg<V1Patch>().Content);
				var key = (kind, applied.Name());
				var before = Objects.GetValueOrDefault(key);
				// As the API does: the generation goes up when the spec changes, not at an apply that changes nothing.
				var sameSpec = before is not null && SpecOf(before) == SpecOf(applied);
				applied.Metadata.Generation = sameSpec ? before!.Metadata.Generation : (before?.Metadata.Generation ?? 0) + 1;
				applied.Metadata.Uid = before?.Metadata.Uid ?? "uid-" + applied.Name();
				// The status is the controller's, not part of the apply.
				switch (applied, before)
				{
					case (V1Deployment d, V1Deployment b):
						d.Status = b.Status;
						break;
					case (V1StatefulSet s, V1StatefulSet b):
						s.Status = b.Status;
						break;
					case (V1Job j, V1Job b):
						j.Status = b.Status;
						break;
				}
				Objects[key] = applied;
				Applied.Add($"{kind}/{applied.Name()}");
				return Copy(applied);
			});
		Client.CreateAsync(Arg.Any<T>(), Arg.Any<CancellationToken>()).Returns(call =>
		{
			var created = Copy(call.Arg<T>());
			Objects[(kind, created.Name())] = created;
			Created.Add($"{kind}/{created.Name()}");
			return Copy(created);
		});
	}
}
