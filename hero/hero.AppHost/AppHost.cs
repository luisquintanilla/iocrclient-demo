var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.hero_Web>("hero-web")
    .WithEnvironment("VectorStore__Path", "vector-store.db");

builder.Build().Run();
