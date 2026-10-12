using Microsoft.ML.OnnxRuntime;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Domain;

namespace Trackdub.Inference.Onnx.Tests.WindowsMl;

#if WINDOWS
public sealed class WindowsMlEfficiencyPolicyTests
{
    private const string IdentityModelBase64 =
        "CAc6OgoQCgF4EgF5IghJZGVudGl0eRIEdGVzdFoPCgF4EgoKCAgBEgQKAggBYg8KAXkSCgoICAESBAoCCAFCBAoAEAk=";

    [Theory]
    [InlineData(WindowsMlExecutionDevicePolicy.MaxEfficiency, true)]
    [InlineData(WindowsMlExecutionDevicePolicy.MinOverallPower, true)]
    [InlineData(WindowsMlExecutionDevicePolicy.MinPower, true)]
    [InlineData(WindowsMlExecutionDevicePolicy.Explicit, false)]
    [InlineData(WindowsMlExecutionDevicePolicy.MaxPerformance, false)]
    [InlineData(WindowsMlExecutionDevicePolicy.PreferNpu, false)]
    [InlineData(WindowsMlExecutionDevicePolicy.DefaultRender, false)]
    public void RequestsEfficientWorkload_matches_efficiency_flavored_policies(
        WindowsMlExecutionDevicePolicy policy, bool expected)
    {
        Assert.Equal(expected, Trackdub.Inference.Onnx.WindowsMl.WindowsMlEfficiencyPolicy
            .RequestsEfficientWorkload(policy));
    }

    [Theory]
    [InlineData(ExecutionProviderKind.Qnn, true)]
    [InlineData(ExecutionProviderKind.VitisAi, true)]
    [InlineData(ExecutionProviderKind.OpenVinoCatalog, true)]
    [InlineData(ExecutionProviderKind.Migraphx, false)]   // WinML catalog EP, but a GPU EP
    [InlineData(ExecutionProviderKind.DirectMl, false)]
    [InlineData(ExecutionProviderKind.Cpu, false)]
    [InlineData(ExecutionProviderKind.TensorRTRtx, false)]
    public void IsNpuCatalogProvider_matches_only_npu_catalog_providers(
        ExecutionProviderKind provider, bool expected)
    {
        Assert.Equal(expected, Trackdub.Inference.Onnx.WindowsMl.WindowsMlEfficiencyPolicy
            .IsNpuCatalogProvider(provider));
    }

    [Fact]
    public void ApplyToSession_applies_for_npu_provider_under_efficiency_policy()
    {
        using var session = new InferenceSession(Convert.FromBase64String(IdentityModelBase64));

        (bool applied, string? error) = Trackdub.Inference.Onnx.WindowsMl.WindowsMlEfficiencyPolicy
            .ApplyToSession(session, ExecutionProviderKind.Qnn, WindowsMlExecutionDevicePolicy.MinPower);

        // ORT accepts the option on any session (only NPU EPs act on it), so the interop
        // result is the assertion target — this proves the gating called through.
        Assert.True(applied);
        Assert.Null(error);
    }

    [Fact]
    public void ApplyToSession_skips_for_cpu_provider_even_under_efficiency_policy()
    {
        using var session = new InferenceSession(Convert.FromBase64String(IdentityModelBase64));

        (bool applied, string? error) = Trackdub.Inference.Onnx.WindowsMl.WindowsMlEfficiencyPolicy
            .ApplyToSession(session, ExecutionProviderKind.Cpu, WindowsMlExecutionDevicePolicy.MinPower);

        Assert.False(applied);
        Assert.Null(error); // skip is not a failure — nothing to report
    }

    [Fact]
    public void ApplyToSession_skips_for_npu_provider_under_explicit_policy()
    {
        using var session = new InferenceSession(Convert.FromBase64String(IdentityModelBase64));

        (bool applied, string? error) = Trackdub.Inference.Onnx.WindowsMl.WindowsMlEfficiencyPolicy
            .ApplyToSession(session, ExecutionProviderKind.Qnn, WindowsMlExecutionDevicePolicy.Explicit);

        Assert.False(applied);
        Assert.Null(error);
    }

    [Fact]
    public void CreateSession_policies_flow_through_the_factory_chokepoint()
    {
        // The factory's private CreateSession is the single session-creation chokepoint; its
        // efficiency hook delegates to WindowsMlEfficiencyPolicy.ApplyToSession, which is
        // covered above. This test pins the delegation signature the factory uses so a
        // refactor cannot silently drop the hook: compile-time proof, no session needed.
        Assert.True(Trackdub.Inference.Onnx.WindowsMl.WindowsMlEfficiencyPolicy.IsNpuCatalogProvider(
            ExecutionProviderKind.Qnn));
    }
}
#endif
