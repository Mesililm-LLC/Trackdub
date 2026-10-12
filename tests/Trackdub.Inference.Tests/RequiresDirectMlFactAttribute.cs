using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using Trackdub.Domain;
using Trackdub.Inference.Onnx;
using Trackdub.Inference.Onnx.Runtime;
using Trackdub.TestDoubles;
#if WINDOWS
using Trackdub.Inference.Onnx.WindowsMl;
#endif

namespace Trackdub.Inference.Tests;

/// <summary>
/// Skips when DirectML is unavailable (non-Windows hosts, CI without GPU catalog EP, etc.).
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class RequiresDirectMlFactAttribute : FactAttribute
{
    public RequiresDirectMlFactAttribute(
        [CallerFilePath] string sourceFilePath = "",
        [CallerLineNumber] int sourceLineNumber = 0)
    {
        Skip = ResolveDirectMlSkip();
    }

    internal static string? ResolveDirectMlSkip()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return "DirectML smoke requires Windows.";
        }

#if WINDOWS
        WindowsMlOnnxRuntimeNativeResolver.EnsureInitialized();
#else
        // This runs at xunit discovery, before any test. Constructing SessionOptions triggers ORT's own
        // native resolver registration, which would leave GenAiNativeRuntimeSelection unable to claim it.
        GenAiNativeRuntimeSelection.EnsureOrtLoaded(requireVerified: true);
#endif

        using SessionOptions options = new();
        string? catalogFailure = null;
        string? classicFailure = null;
        if (OnnxExecutionSessionFactory.TryAppendDirectMlProvider(options, out catalogFailure)
            || OnnxExecutionSessionFactory.TryAppendDirectMlProviderDirect(options, out classicFailure))
        {
            return null;
        }

#if WINDOWS
        try
        {
            // Catalog registration can populate GetEpDevices, but packaged DirectML does not
            // require it. Do not skip solely because RegisterCertifiedAsync failed or timed out.
            _ = WindowsMlProviderRegistrationPolicy.Shared
                .RegisterForReadinessAsync(ExecutionProviderKind.DirectMl, CancellationToken.None)
                .ConfigureAwait(false)
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception)
        {
            // Append path below is the real availability gate.
        }

        using SessionOptions retryOptions = new();
        if (OnnxExecutionSessionFactory.TryAppendDirectMlProvider(retryOptions, out catalogFailure)
            || OnnxExecutionSessionFactory.TryAppendDirectMlProviderDirect(retryOptions, out classicFailure))
        {
            return null;
        }
#endif

        string detail = catalogFailure ?? classicFailure ?? "unknown failure";
        return $"DirectML execution provider is not available: {detail}";
    }
}

/// <summary>
/// Bundled-model integration test that also requires a working DirectML catalog EP.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class RequiresDirectMlBundledModelFactAttribute : FactAttribute
{
    public RequiresDirectMlBundledModelFactAttribute(
        string relativePath,
        [CallerFilePath] string sourceFilePath = "",
        [CallerLineNumber] int sourceLineNumber = 0)
    {
        Skip = RequiresDirectMlFactAttribute.ResolveDirectMlSkip()
            ?? BundledModelSkipResolver.Resolve([relativePath]);
    }

    public RequiresDirectMlBundledModelFactAttribute(
        params string[] relativePaths)
    {
        Skip = RequiresDirectMlFactAttribute.ResolveDirectMlSkip()
            ?? BundledModelSkipResolver.Resolve(relativePaths);
    }
}
