using System.Text.Json.Nodes;
using ChromaDotNet.Operator.Distributed;
using ChromaDotNet.Operator.Entities;
using k8s;
using k8s.Models;
using NUnit.Framework;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace ChromaDotNet.Operator.Tests;

/// <summary>
/// The objects of a distributed Chroma are those of helm template of the chart distributed-chroma, for the values the operator derives
/// from the spec. build/chart.sh renders the chart for each case into Chart/&lt;case&gt;.yaml; the operator
/// builds the objects for Chart/&lt;case&gt;.chroma.yaml, and they must be the same, but for the differences of DistributedResources: no Namespace,
/// members [] instead of null, the Chroma as controller and the two labels of the operator. Values are compared as text, as YAML
/// writes them: 2 and "2" are the same quantity for the API.
/// </summary>
public class ChartTests
{
	[TestCase("default")]
	[TestCase("custom")]
	[TestCase("previous")]
	public void ObjectsAreThoseOfTheChart(string name)
	{
		var chroma = LoadChroma(name);
		var version = DistributedVersion.Find(chroma.Spec.Distributed!.Version)!;
		var ours = DistributedResources.All(chroma, version).ToDictionary(Key, o => Normalize(JsonNode.Parse(KubernetesJson.Serialize(o))!, ours: true));
		var chart = LoadChart(name).Where(o => o["kind"]!.ToString() != "Namespace").ToDictionary(o => $"{o["kind"]}/{o["metadata"]!["name"]}", o => Normalize(o, ours: false));

		Assert.That(ours.Keys.Order(StringComparer.Ordinal), Is.EqualTo(chart.Keys.Order(StringComparer.Ordinal)), "the same objects");
		Assert.Multiple(() =>
		{
			foreach (var (key, expected) in chart)
				Assert.That(Difference(ours[key], expected, key), Is.Null);
		});
	}

	// The comparison sees a change anywhere: a value, an item more, an object less.
	[Test]
	public void ComparisonSeesADifference()
	{
		var chroma = LoadChroma("default");
		var chart = LoadChart("default").Where(o => o["kind"]!.ToString() != "Namespace").ToDictionary(o => $"{o["kind"]}/{o["metadata"]!["name"]}", o => Normalize(o, ours: false));
		var objects = DistributedResources.All(chroma, DistributedVersion.Known[0]);
		Assert.That(objects, Has.Count.EqualTo(chart.Count).And.Count.EqualTo(66));

		var query = objects.OfType<V1StatefulSet>().Single(s => s.Name() == DistributedResources.QueryService);
		query.Spec.Template.Spec.Containers[0].Env.Single(e => e.Name == "AWS_REGION").Value = "eu-west-1";
		Assert.That(Difference(Normalize(JsonNode.Parse(KubernetesJson.Serialize(query)), ours: true), chart["StatefulSet/query-service"], "query"),
			Does.Contain("query.spec.template.spec.containers[0].env[4].value"));

		var sysdb = objects.OfType<V1Deployment>().Single(d => d.Name() == DistributedResources.Sysdb);
		sysdb.Spec.Template.Spec.Containers[0].Env.Add(new V1EnvVar { Name = "EXTRA", Value = "1" });
		Assert.That(Difference(Normalize(JsonNode.Parse(KubernetesJson.Serialize(sysdb)), ours: true), chart["Deployment/sysdb"], "sysdb"), Does.Contain("items"));

		var role = objects.OfType<V1Role>().Single(r => r.Name() == "pod-watcher");
		role.Rules[0].Verbs = [.. role.Rules[0].Verbs, "delete"];
		Assert.That(Difference(Normalize(JsonNode.Parse(KubernetesJson.Serialize(role)), ours: true), chart["Role/pod-watcher"], "role"), Is.Not.Null);
	}

	// The only differences, kept by Normalize on the side of the operator: they must be there.
	[Test]
	public void ObjectsHaveTheControllerAndTheLabelsOfTheOperator()
	{
		var chroma = LoadChroma("default");
		foreach (var o in DistributedResources.All(chroma, DistributedVersion.Known[0]))
		{
			Assert.That(o.Metadata.OwnerReferences.Single().Controller, Is.True, Key(o));
			Assert.That(o.Metadata.OwnerReferences.Single().Uid, Is.EqualTo("uid-chart"), Key(o));
			Assert.That(o.Metadata.Labels[Labels.Chroma], Is.EqualTo("chroma"), Key(o));
			Assert.That(o.Metadata.Labels["app.kubernetes.io/managed-by"], Is.EqualTo(Labels.ManagedBy), Key(o));
			Assert.That(o.Metadata.NamespaceProperty, Is.EqualTo("chroma"), Key(o));
		}
	}

	// With Helm 4 the chart writes members: null, which the API refuses: the operator writes an empty list.
	[Test]
	public void MemberListsAreEmptyLists()
	{
		var memberLists = DistributedResources.Foundation(LoadChroma("default"), DistributedVersion.Known[0]).OfType<V1MemberList>().ToList();
		Assert.That(memberLists, Has.Count.EqualTo(5));
		Assert.That(memberLists.Select(m => KubernetesJson.Serialize(m.Spec)), Is.All.EqualTo("{\"members\":[]}"));
	}

	// The config files have the corrections of the design and nothing else: no credentials of Minio, the bucket and the namespace of
	// the Chroma. The secrets are never in them.
	[Test]
	public void ConfigFilesHoldNoCredentials()
	{
		var chroma = LoadChroma("custom");
		var worker = DistributedConfig.Worker(DistributedVersion.Known[0], "team-a", "vectors");
		Assert.That(worker, Does.Not.Contain("Minio").And.Not.Contain("minio123").And.Not.Contain("chroma-storage").And.Not.Contain(".chroma\""));
		Assert.That(worker.Split("credentials: \"AWS\"").Length - 1, Is.EqualTo(8));
		var configs = DistributedResources.Foundation(chroma, DistributedVersion.Known[0]).OfType<V1ConfigMap>().Select(c => c.Data["config.yaml"]);
		Assert.That(configs, Has.None.Contain("team-a-s3").And.None.Contain("team-a-pg").And.None.Contain("$PG_PASSWORD"));
	}

	// The namespace is written before the bucket: a bucket ending in .chroma stays as it is.
	[Test]
	public void BucketEndingInTheNamespaceOfTheChartStays()
	{
		var worker = DistributedConfig.Worker(DistributedVersion.Known[0], "rag", "data.chroma");
		Assert.That(worker, Does.Contain("bucket: \"data.chroma\"").And.Not.Contain("bucket: \"data.rag\""));
		Assert.That(worker, Does.Contain("host: \"sysdb.rag\""));
	}

	internal static V1Alpha1Chroma LoadChroma(string name)
	{
		var chroma = KubernetesYaml.Deserialize<V1Alpha1Chroma>(File.ReadAllText(Fixtures.RepositoryFile("tests", "ChromaDotNet.Operator.Tests", "Chart", name + ".chroma.yaml")));
		chroma.Metadata.Uid = "uid-chart";
		return chroma;
	}

	private static string Key(IKubernetesObject<V1ObjectMeta> o) => $"{o.Kind}/{o.Metadata.Name}";

	/// <summary>The documents of the file, as JSON, with every scalar as text.</summary>
	private static List<JsonObject> LoadChart(string name)
	{
		using var reader = new StringReader(File.ReadAllText(Fixtures.RepositoryFile("tests", "ChromaDotNet.Operator.Tests", "Chart", name + ".yaml")));
		var parser = new Parser(reader);
		var deserializer = new DeserializerBuilder().Build();
		parser.Consume<StreamStart>();
		var documents = new List<JsonObject>();
		while (parser.Accept<DocumentStart>(out _))
		{
			if (ToJson(deserializer.Deserialize<object?>(parser)) is JsonObject document)
				documents.Add(document);
		}
		return documents;
	}

	private static JsonNode? ToJson(object? value) => value switch
	{
		null => null,
		IDictionary<object, object?> map => new JsonObject(map.Select(e => KeyValuePair.Create(e.Key.ToString()!, ToJson(e.Value)))),
		IList<object?> list => new JsonArray(list.Select(ToJson).ToArray()),
		_ => JsonValue.Create(value.ToString()),
	};

	/// <summary>
	/// Removes nulls, empty objects and empty lists, and writes scalars as text. On the side of the operator it also removes the
	/// differences the design allows: the owner references and the two labels of the operator.
	/// </summary>
	private static JsonNode? Normalize(JsonNode? node, bool ours)
	{
		if (ours && node is JsonObject root && root["metadata"] is JsonObject metadata)
		{
			metadata.Remove("ownerReferences");
			if (metadata["labels"] is JsonObject labels)
			{
				labels.Remove("app.kubernetes.io/managed-by");
				labels.Remove(Labels.Chroma);
			}
		}
		return Clean(node);
	}

	private static JsonNode? Clean(JsonNode? node)
	{
		switch (node)
		{
			case null:
				return null;
			case JsonObject o:
				var cleaned = new JsonObject();
				foreach (var (key, value) in o.ToList())
				{
					if (Clean(value) is { } c)
						cleaned[key] = c;
				}
				return cleaned.Count == 0 ? null : cleaned;
			case JsonArray a:
				var items = a.Select(Clean).Where(i => i is not null).ToArray();
				return items.Length == 0 ? null : new JsonArray(items);
			default:
				return JsonValue.Create(node.ToString());
		}
	}

	/// <summary>Null when the two are the same; otherwise the first path where they differ, with both values.</summary>
	private static string? Difference(JsonNode? ours, JsonNode? chart, string path)
	{
		switch (ours, chart)
		{
			case (null, null):
				return null;
			case (JsonObject o, JsonObject c):
				foreach (var key in o.Select(p => p.Key).Union(c.Select(p => p.Key)).Order(StringComparer.Ordinal))
				{
					if (Difference(o[key], c[key], $"{path}.{key}") is { } d)
						return d;
				}
				return null;
			case (JsonArray o, JsonArray c):
				if (o.Count != c.Count)
					return $"{path}: {o.Count} items, the chart has {c.Count}: {o.ToJsonString()} / {c.ToJsonString()}";
				for (var i = 0; i < o.Count; i++)
				{
					if (Difference(o[i], c[i], $"{path}[{i}]") is { } d)
						return d;
				}
				return null;
			default:
				var a = ours?.ToJsonString();
				var b = chart?.ToJsonString();
				return a == b ? null : $"{path}: the operator has {a ?? "nothing"}, the chart {b ?? "nothing"}";
		}
	}
}
