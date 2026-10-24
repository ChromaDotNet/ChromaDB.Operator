using System.Reflection;
using ChromaDotNet.Operator.Controllers;
using k8s;
using k8s.Models;
using KubeOps.Abstractions.Rbac;
using NUnit.Framework;

namespace ChromaDotNet.Operator.Tests;

/// <summary>
/// The RBAC of deploy/chroma-operator.yaml, the file users apply: only what the operator does, for its own ServiceAccount. A new call
/// to the API needs its rule here too, and a rule the code no longer needs is noticed. The rules of the manifest are also those of the
/// RBAC attributes of the code: a change of the attributes without build/manifest.sh fails here, not with a 403 at run time.
/// </summary>
public class ManifestTests
{
	private static readonly List<object> Objects = KubernetesYaml.LoadAllFromString(File.ReadAllText(ManifestPath()));

	[Test]
	public void ClusterRoleHasOnlyWhatTheOperatorUses()
	{
		Assert.That(Granted(), Is.EquivalentTo(new[]
		{
			"/configmaps: create,get,update",
			"/persistentvolumeclaims: create,get,update",
			"/pods: create,delete,get,list,update,watch",
			"/services: create,get,update",
			"chroma.chromadotnet.org/chromaservers: get,list,update,watch",
			"chroma.chromadotnet.org/chromaservers/finalizers: update",
			"chroma.chromadotnet.org/chromaservers/status: update",
		}));
		Assert.That(Objects.OfType<V1ClusterRole>().Single().Rules.All(r => r.NonResourceURLs is null or { Count: 0 }), Is.True);
	}

	// The KubeOps CLI adds the status of each entity, and the kustomization keeps only update on it: the rest comes from the attributes.
	[Test]
	public void ManifestHasTheRulesOfTheAttributes()
	{
		var types = typeof(ChromaServerController).Assembly.GetTypes();
		var entities = types.SelectMany(t => t.GetCustomAttributes<EntityRbacAttribute>())
			.SelectMany(a => a.Entities.Select(e => e.GetCustomAttribute<KubernetesEntityAttribute>()!)
				.Select(k => $"{k.Group}/{k.PluralName}: {Verbs(a.Verbs)}"));
		var generic = types.SelectMany(t => t.GetCustomAttributes<GenericRbacAttribute>())
			.SelectMany(a => a.Groups.SelectMany(g => a.Resources.Select(r => $"{g}/{r}: {Verbs(a.Verbs)}")));
		Assert.That(Granted().Where(g => !g.StartsWith("chroma.chromadotnet.org/chromaservers/status:", StringComparison.Ordinal)),
			Is.EquivalentTo(entities.Concat(generic)));
	}

	[Test]
	public void OperatorRunsAsItsOwnServiceAccount()
	{
		var account = Objects.OfType<V1ServiceAccount>().Single();
		Assert.That((account.Name(), account.Namespace()), Is.EqualTo(("chroma-operator", "chroma-operator-system")));
		var subject = Objects.OfType<V1ClusterRoleBinding>().Single().Subjects.Single();
		Assert.That((subject.Kind, subject.Name, subject.NamespaceProperty), Is.EqualTo(("ServiceAccount", "chroma-operator", "chroma-operator-system")));
		Assert.That(Objects.OfType<V1Deployment>().Single().Spec.Template.Spec.ServiceAccountName, Is.EqualTo("chroma-operator"));
	}

	// One operator at a time: an update stops the old Pod before it starts the new one, and the leader election, on a Lease of the
	// namespace of the operator, covers more replicas and a Pod still running on a node that was lost.
	[Test]
	public void OneOperatorAtATime()
	{
		Assert.That(Objects.OfType<V1Deployment>().Single().Spec.Strategy?.Type, Is.EqualTo("Recreate"));
		var role = Objects.OfType<V1Role>().Single();
		Assert.That(role.Namespace(), Is.EqualTo("chroma-operator-system"));
		Assert.That(role.Rules.Select(r => $"{string.Join(',', r.ApiGroups)}/{string.Join(',', r.Resources)}: {string.Join(',', r.Verbs.Order(StringComparer.Ordinal))}"),
			Is.EqualTo(new[] { "coordination.k8s.io/leases: create,get,update" }));
		var binding = Objects.OfType<V1RoleBinding>().Single();
		Assert.That(binding.RoleRef.Name, Is.EqualTo(role.Name()));
		Assert.That(binding.Subjects.Single().Name, Is.EqualTo("chroma-operator"));
	}

	[Test]
	public void OperatorHasProbes()
	{
		var container = Objects.OfType<V1Deployment>().Single().Spec.Template.Spec.Containers.Single();
		foreach (var probe in new[] { container.LivenessProbe, container.ReadinessProbe })
		{
			Assert.That(probe?.HttpGet?.Path, Is.EqualTo("/healthz"));
			Assert.That(probe!.HttpGet.Port.Value, Is.EqualTo("health"));
		}
		Assert.That(container.Ports.Single(p => p.Name == "health").ContainerPort, Is.EqualTo(8080));
	}

	/// <summary>Each resource of the ClusterRole with its verbs: "group/resource: verb,verb".</summary>
	private static IEnumerable<string> Granted() => Objects.OfType<V1ClusterRole>().Single().Rules
		.SelectMany(r => r.ApiGroups.SelectMany(g => r.Resources.Select(resource => $"{g}/{resource}: {string.Join(',', r.Verbs.Order(StringComparer.Ordinal))}")));

	private static string Verbs(RbacVerb verbs) => string.Join(',', Enum.GetValues<RbacVerb>()
		.Where(v => v is not (RbacVerb.None or RbacVerb.All or RbacVerb.AllExplicit) && verbs.HasFlag(v))
		.Select(v => v.ToString().ToLowerInvariant())
		.Order(StringComparer.Ordinal));

	private static string ManifestPath()
	{
		for (var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); directory is not null; directory = directory.Parent)
		{
			if (File.Exists(Path.Combine(directory.FullName, "ChromaDB.Operator.slnx")))
				return Path.Combine(directory.FullName, "deploy", "chroma-operator.yaml");
		}
		throw new FileNotFoundException("ChromaDB.Operator.slnx not found above " + TestContext.CurrentContext.TestDirectory);
	}
}
