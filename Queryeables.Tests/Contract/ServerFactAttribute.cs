using Xunit.Abstractions;
using Xunit.Sdk;

namespace Queryeables.Tests.Contract;

/// <summary>
/// A test that runs against a database server when it says which one (<see cref="Server"/>, or
/// <see cref="RequiresServerAttribute"/> on its class):
/// reported as skipped, with the reason, when the environment variable holding its connection string is not set. The CI
/// starts the servers and sets the variables; on a developer machine, the tests that need a missing server are skipped
/// instead of failing.
/// </summary>
[XunitTestCaseDiscoverer("Queryeables.Tests.Contract.ServerFactDiscoverer", "Queryeables.Tests")]
[AttributeUsage(AttributeTargets.Method)]
public sealed class ServerFactAttribute : FactAttribute
{
    /// <summary>The environment variable holding the connection string; when not set, the class's.</summary>
    public string? Server { get; set; }
}

/// <summary>The test class needs the server whose connection string is in <paramref name="variable"/>.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class RequiresServerAttribute(string variable) : Attribute
{
    public string Variable { get; } = variable;
}

public sealed class ServerFactDiscoverer(IMessageSink diagnosticMessageSink) : FactDiscoverer(diagnosticMessageSink)
{
    protected override IXunitTestCase CreateTestCase(ITestFrameworkDiscoveryOptions discoveryOptions, ITestMethod testMethod,
        IAttributeInfo factAttribute)
    {
        var requires = testMethod.TestClass.Class.GetCustomAttributes(typeof(RequiresServerAttribute)).FirstOrDefault();
        var variable = factAttribute.GetNamedArgument<string?>(nameof(ServerFactAttribute.Server))
                       ?? requires?.GetConstructorArguments().FirstOrDefault() as string;
        if (variable is null || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable)))
            return base.CreateTestCase(discoveryOptions, testMethod, factAttribute);

        return new XunitSkippedDataRowTestCase(DiagnosticMessageSink, discoveryOptions.MethodDisplayOrDefault(),
            discoveryOptions.MethodDisplayOptionsOrDefault(), testMethod,
            $"Needs a server: set {variable} to its connection string to run it.");
    }
}
