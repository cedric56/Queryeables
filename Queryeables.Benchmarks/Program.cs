using BenchmarkDotNet.Running;

// dotnet run -c Release --project Queryeables.Benchmarks -- --filter "*"
// A benchmark that fails (an exception in its setup or its body) fails the run: the CI runs the network benchmarks once
// (--job dry) to check that a real gRPC server answers.
var summaries = BenchmarkSwitcher.FromAssembly(typeof(Queryeables.Benchmarks.Item).Assembly).Run(args).ToList();
var failed = summaries.Count == 0
             || summaries.Any(s => s.HasCriticalValidationErrors || s.Reports.Length == 0 || s.Reports.Any(r => !r.Success));
return failed ? 1 : 0;
