using ChromaDotNet.Operator.Entities;
using NUnit.Framework;
using static ChromaDotNet.Operator.Tests.Fixtures;

namespace ChromaDotNet.Operator.Tests;

public class ResourcesTests
{
	[Test]
	public void NamesFollowTheInstance()
	{
		var server = Server();
		Assert.That(Resources.Pod(server, Image159).Metadata.Name, Is.EqualTo("demo-1"));
		Assert.That(Resources.Pvc(server).Metadata.Name, Is.EqualTo("demo-1"));
		Assert.That(Resources.ConfigMap(server).Metadata.Name, Is.EqualTo("demo-config"));
		Assert.That(Resources.Service(server).Metadata.Name, Is.EqualTo("demo-rw"));
		Assert.That(Resources.Endpoint(server), Is.EqualTo("http://demo-rw.rag.svc:8000"));
	}

	[Test]
	public void DefaultConfiguration() =>
		Assert.That(Resources.ConfigYaml(null), Is.EqualTo("persist_path: \"/data\"\nport: 8000\nlisten_address: \"0.0.0.0\"\n"));

	[Test]
	public void ConfigurationWithOptions()
	{
		var yaml = Resources.ConfigYaml(new V1Alpha1ChromaServer.ConfigSpec { MaxPayloadSizeBytes = 1000, CorsAllowOrigins = ["https://a.example", "say \"hi\"\\"] });
		Assert.That(yaml, Does.Contain("max_payload_size_bytes: 1000\n"));
		Assert.That(yaml, Does.Contain("cors_allow_origins:\n  - \"https://a.example\"\n  - \"say \\\"hi\\\"\\\\\"\n"));
	}

	// The Pod measured in the design: non-root, read-only root filesystem with /tmp writable, HTTP probes, the configuration from the
	// ConfigMap at /config.yaml, which is the path the image passes to chroma run.
	[Test]
	public void PodIsTheMeasuredOne()
	{
		var pod = Resources.Pod(Server(), Image159);
		var container = pod.Spec.Containers.Single();
		Assert.Multiple(() =>
		{
			Assert.That(pod.Spec.SecurityContext.RunAsNonRoot, Is.True);
			Assert.That(pod.Spec.SecurityContext.RunAsUser, Is.EqualTo(1000));
			Assert.That(pod.Spec.SecurityContext.FsGroup, Is.EqualTo(1000));
			Assert.That(container.SecurityContext.ReadOnlyRootFilesystem, Is.True);
			Assert.That(container.SecurityContext.AllowPrivilegeEscalation, Is.False);
			Assert.That(container.SecurityContext.Capabilities.Drop, Is.EqualTo(new[] { "ALL" }));
			Assert.That(container.VolumeMounts.Select(m => m.MountPath), Is.EquivalentTo(new[] { "/data", "/tmp", "/config.yaml" }));
			Assert.That(pod.Spec.Volumes.Single(v => v.Name == "tmp").EmptyDir, Is.Not.Null);
			Assert.That(pod.Spec.Volumes.Single(v => v.Name == "data").PersistentVolumeClaim.ClaimName, Is.EqualTo("demo-1"));
			Assert.That(container.StartupProbe.HttpGet.Path, Is.EqualTo("/api/v2/heartbeat"));
			Assert.That(container.ReadinessProbe.HttpGet.Path, Is.EqualTo("/api/v2/healthcheck"));
			Assert.That(container.LivenessProbe.HttpGet.Path, Is.EqualTo("/api/v2/heartbeat"));
			Assert.That(container.Env, Is.Null, "no CHROMA_ variable: the file is the whole configuration");
			Assert.That(pod.Metadata.OwnerReferences.Single().Uid, Is.EqualTo("uid-1"));
			Assert.That(pod.Metadata.Labels[Labels.Server], Is.EqualTo("demo"));
		});
	}

	[Test]
	public void PodHashChangesWithTheImageAndTheConfiguration()
	{
		var server = Server();
		var hash = Resources.Pod(server, Image159).Metadata.Annotations[Labels.PodSpecHash];
		Assert.That(Resources.Pod(Server(), Image159).Metadata.Annotations[Labels.PodSpecHash], Is.EqualTo(hash), "stable");
		Assert.That(Resources.Pod(server, Image153).Metadata.Annotations[Labels.PodSpecHash], Is.Not.EqualTo(hash), "image");
		server.Spec.Config = new V1Alpha1ChromaServer.ConfigSpec { MaxPayloadSizeBytes = 1000 };
		Assert.That(Resources.Pod(server, Image159).Metadata.Annotations[Labels.PodSpecHash], Is.Not.EqualTo(hash), "configuration");
	}

	[Test]
	public void ClaimIsOwnedOnlyWithDelete()
	{
		var server = Server();
		Assert.That(Resources.Pvc(server).Metadata.OwnerReferences, Is.Null, "Retain by default");
		server.Spec.PvcDeletionPolicy = "Delete";
		Assert.That(Resources.Pvc(server).Metadata.OwnerReferences.Single().Name, Is.EqualTo("demo"));
	}

	[Test]
	public void ServiceSelectsThePod()
	{
		var service = Resources.Service(Server());
		Assert.That(service.Spec.Selector, Is.EqualTo(new Dictionary<string, string> { [Labels.Server] = "demo" }));
		Assert.That(service.Spec.Type, Is.EqualTo("ClusterIP"));
		Assert.That(service.Spec.Ports.Single().Port, Is.EqualTo(8000));
	}
}
