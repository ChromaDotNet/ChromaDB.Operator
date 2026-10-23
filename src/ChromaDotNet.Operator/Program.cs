using ChromaDotNet.Operator;
using KubeOps.Abstractions.Builder;
using KubeOps.Operator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton(OperatorOptions.From(builder.Configuration));
// The controller puts the finalizer of the claim on a ChromaServer only while the server owns its claim (ClaimFinalizer).
builder.Services
	.AddKubernetesOperator(settings => settings.WithAutoAttachFinalizers(false))
	.RegisterComponents();
// The Pods change outside the watch of KubeOps, which follows the ChromaServers: their changes put the server in the queue at once.
builder.Services.AddHostedService<PodWatcher>();

using var host = builder.Build();
await host.RunAsync();
