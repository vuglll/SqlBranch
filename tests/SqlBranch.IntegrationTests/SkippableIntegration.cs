using System.ComponentModel;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace SqlBranch.IntegrationTests;

/// <summary>
/// Thrown by an integration test that needs a clone mode the pool does not have. The test is then reported as
/// skipped, never as passed (design D12): a thin-mode assertion that did not run says nothing about thin mode.
/// </summary>
public sealed class SkipTestException(string reason) : Exception(reason)
{
    /// <summary>Skips the test when the world's pool is a plain folder: the test is about the managed XFS pool.</summary>
    public static void UnlessManaged(EngineWorld world) => UnlessManaged(world.IsPlain);

    /// <summary>Skips the test when the suite runs on a plain pool, before any world is built.</summary>
    public static void UnlessManagedRequested() => UnlessManaged(EngineWorld.PlainRequested);

    private static void UnlessManaged(bool plain)
    {
        if (plain)
        {
            throw new SkipTestException($"The pool backend is plain ({EngineWorld.PoolBackendVariable}), and this test is about the managed XFS pool.");
        }
    }

    /// <summary>
    /// For a test that must run in thin mode. It is skipped when the suite was asked for a plain pool
    /// (<see cref="EngineWorld.PoolBackendVariable"/>); on any other run a pool that is not thin is a failure, not a skip.
    /// </summary>
    public static void ThinRequired(string mode)
    {
        if (EngineWorld.PlainRequested)
        {
            UnlessThin(mode);
        }

        Assert.Equal("thin", mode);
    }

    /// <summary>Skips the test unless <paramref name="mode"/> is <c>thin</c>.</summary>
    public static void UnlessThin(string mode)
    {
        if (mode != "thin")
        {
            throw new SkipTestException($"The pool's clone mode is {mode}, and this test checks thin mode.");
        }
    }
}

/// <summary>
/// Like <see cref="IntegrationFactAttribute"/>, and a test can also skip itself while it runs by throwing
/// <see cref="SkipTestException"/>. xunit 2 has no such thing built in, so the discoverer below runs the test case with a
/// message bus that turns that one exception into a skipped result.
/// </summary>
[XunitTestCaseDiscoverer("SqlBranch.IntegrationTests.SelfSkippingFactDiscoverer", "SqlBranch.IntegrationTests")]
public sealed class IntegrationThinFactAttribute : FactAttribute
{
    public IntegrationThinFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(IntegrationFactAttribute.EnableVariable) != "1")
        {
            Skip = "Needs Docker. Set SQLBRANCH_IT=1 to run it.";
        }
    }
}

public sealed class SelfSkippingFactDiscoverer(IMessageSink diagnosticMessageSink) : IXunitTestCaseDiscoverer
{
    public IEnumerable<IXunitTestCase> Discover(ITestFrameworkDiscoveryOptions discoveryOptions, ITestMethod testMethod, IAttributeInfo factAttribute)
    {
        yield return new SelfSkippingTestCase(
            diagnosticMessageSink, discoveryOptions.MethodDisplayOrDefault(), discoveryOptions.MethodDisplayOptionsOrDefault(), testMethod);
    }
}

public sealed class SelfSkippingTestCase : XunitTestCase
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    [Obsolete("Called by the de-serializer; should only be called by deriving classes for de-serialization purposes")]
    public SelfSkippingTestCase()
    {
    }

    public SelfSkippingTestCase(
        IMessageSink diagnosticMessageSink, TestMethodDisplay defaultMethodDisplay, TestMethodDisplayOptions defaultMethodDisplayOptions, ITestMethod testMethod)
        : base(diagnosticMessageSink, defaultMethodDisplay, defaultMethodDisplayOptions, testMethod)
    {
    }

    public override async Task<RunSummary> RunAsync(
        IMessageSink diagnosticMessageSink, IMessageBus messageBus, object[] constructorArguments, ExceptionAggregator aggregator, CancellationTokenSource cancellationTokenSource)
    {
        var bus = new SkipConvertingBus(messageBus);
        var summary = await base.RunAsync(diagnosticMessageSink, bus, constructorArguments, aggregator, cancellationTokenSource).ConfigureAwait(false);
        if (bus.Skipped > 0)
        {
            summary.Failed -= bus.Skipped;
            summary.Skipped += bus.Skipped;
        }

        return summary;
    }

    private sealed class SkipConvertingBus(IMessageBus inner) : IMessageBus
    {
        public int Skipped { get; private set; }

        public bool QueueMessage(IMessageSinkMessage message)
        {
            if (message is ITestFailed failed && failed.ExceptionTypes.FirstOrDefault() == typeof(SkipTestException).FullName)
            {
                Skipped++;
                return inner.QueueMessage(new TestSkipped(failed.Test, failed.Messages.FirstOrDefault() ?? "Skipped."));
            }

            return inner.QueueMessage(message);
        }

        public void Dispose() => inner.Dispose();
    }
}
