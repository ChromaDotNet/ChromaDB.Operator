using ChromaDotNet.Operator;
using KubeOps.Operator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton(OperatorOptions.From(builder.Configuration));
builder.Services
	.AddKubernetesOperator()
	.RegisterComponents();
// The Pods change outside the watch of KubeOps, which follows the ChromaServers: their changes put the server in the queue at once.
builder.Services.AddHostedService<PodWatcher>();

using var host = builder.Build();
await host.RunAsync();
