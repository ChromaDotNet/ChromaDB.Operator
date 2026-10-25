using ChromaDotNet.Operator;
using ChromaDotNet.Operator.Distributed;
using KubeOps.Abstractions.Builder;
using KubeOps.Operator;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

var builder = WebApplication.CreateSlimBuilder(args);
builder.Services.AddSingleton(OperatorOptions.From(builder.Configuration));
// The checks of the external Postgres and bucket of a distributed Chroma.
builder.Services.AddSingleton<IDependencyChecks, DependencyChecks>();
builder.Services
	.AddKubernetesOperator(settings =>
	{
		// One operator works at a time, the holder of the Lease chroma-operator-leader in its namespace (POD_NAMESPACE): during an
		// update, or with a Pod left on a node that was lost, another one waits.
		settings.Name = "chroma-operator";
		settings.WithLeaderElection(LeaderElectionType.Single);
		// The controller puts the finalizer of the claim on a Chroma only while the server owns its claim (ClaimFinalizer).
		settings.WithAutoAttachFinalizers(false);
	})
	.RegisterComponents();
// The Pods change outside the watch of KubeOps, which follows the Chroma resources: their changes put the server in the queue at once.
builder.Services.AddHostedService<PodWatcher>();
// The probes of the Deployment: the process answers on ASPNETCORE_HTTP_PORTS.
builder.Services.AddHealthChecks();

await using var app = builder.Build();
app.MapHealthChecks("/healthz");
await app.RunAsync();
