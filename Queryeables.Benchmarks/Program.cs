using BenchmarkDotNet.Running;

// dotnet run -c Release --project Queryeables.Benchmarks -- --filter "*"
BenchmarkSwitcher.FromAssembly(typeof(Queryeables.Benchmarks.Item).Assembly).Run(args);
