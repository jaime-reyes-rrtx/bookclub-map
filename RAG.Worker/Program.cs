using RAG.Core.Services;
using RAG.Worker;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddRagCore(builder.Configuration);
builder.Services.AddHttpClient();
builder.Services.AddHostedService<OllamaModelWarmupService>();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
await host.Services.EnsureRagDatabaseAsync();
await host.RunAsync();
