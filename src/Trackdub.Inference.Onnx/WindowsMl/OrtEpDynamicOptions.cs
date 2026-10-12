using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.ML.OnnxRuntime;

namespace Trackdub.Inference.Onnx.WindowsMl;

/// <summary>
/// Calls the native ONNX Runtime <c>SetEpDynamicOptions</c> on a live <see cref="InferenceSession"/>.
/// </summary>
/// <remarks>
/// <para>
/// The pinned managed bindings (Windows ML 2.4.89 / native ORT 1.27.1 on the Windows TFM, and the
/// stock 1.31.0 binding used by the worker host) expose no managed wrapper for
/// <c>SetEpDynamicOptions</c>; both expose the identical private reflection surface —
/// <c>NativeMethods.api_</c> (private static <c>OrtApi</c> struct of Cdecl function pointers) and
/// <c>InferenceSession._nativeHandle</c>. This class is that interop's single owner.
/// </para>
/// <para>
/// Failure is always reported, never thrown: session creation must not fail because an
/// efficiency hint could not be applied. <see cref="IsAvailable"/> is checked by tests so a
/// package bump that reshapes the private surface fails loudly instead of silently no-opping.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows10.0.19041.0")]
internal static class OrtEpDynamicOptions
{
    /// <summary>ORT dynamic-option key for the session's workload type (see kOrtEpDynamicOptionsWorkloadType).</summary>
    public const string WorkloadTypeKey = "ep.dynamic.workload_type";

    /// <summary>Efficiency-oriented workload signal; only NPU execution providers act on it today.</summary>
    public const string EfficientWorkload = "Efficient";

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr DSetEpDynamicOptions(IntPtr session, IntPtr keys, IntPtr values, UIntPtr kvLen);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr DOrtGetErrorMessage(IntPtr status);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DOrtReleaseStatus(IntPtr status);

    private static readonly Type? NativeMethodsType =
        typeof(InferenceSession).Assembly.GetType("Microsoft.ML.OnnxRuntime.NativeMethods", throwOnError: false);

    private static readonly FieldInfo? ApiField =
        NativeMethodsType?.GetField("api_", BindingFlags.NonPublic | BindingFlags.Static);

    private static readonly FieldInfo? HandleField =
        typeof(InferenceSession).GetField("_nativeHandle", BindingFlags.NonPublic | BindingFlags.Instance);

    // OrtApi fields (function pointers), resolved from the api_ struct instance.
    private static readonly object? ApiInstance = ApiField?.GetValue(null);
    private static readonly IntPtr SetEpDynamicOptionsPointer =
        (IntPtr?)(ApiInstance?.GetType().GetField("SetEpDynamicOptions")?.GetValue(ApiInstance)) ?? IntPtr.Zero;
    private static readonly IntPtr GetErrorMessagePointer =
        (IntPtr?)(ApiInstance?.GetType().GetField("GetErrorMessage")?.GetValue(ApiInstance)) ?? IntPtr.Zero;
    private static readonly IntPtr ReleaseStatusPointer =
        (IntPtr?)(ApiInstance?.GetType().GetField("ReleaseStatus")?.GetValue(ApiInstance)) ?? IntPtr.Zero;

    /// <summary>True when every reflection anchor this interop needs exists on the loaded binding.</summary>
    public static bool IsAvailable =>
        NativeMethodsType is not null &&
        ApiField is not null &&
        HandleField is not null &&
        ApiInstance is not null &&
        SetEpDynamicOptionsPointer != IntPtr.Zero &&
        GetErrorMessagePointer != IntPtr.Zero &&
        ReleaseStatusPointer != IntPtr.Zero;

    /// <summary>
    /// Sets <paramref name="value"/> for <see cref="WorkloadTypeKey"/> on <paramref name="session"/>.
    /// Returns false with a reason instead of throwing whenever the call cannot be made or fails;
    /// a null ORT status (success) returns true. Note: the pinned ORT 1.27.1 native build does not
    /// validate the value string at call time — acceptance does not prove the EP honored it.
    /// </summary>
    public static bool TrySetWorkloadType(InferenceSession session, string value, out string? error)
    {
        ArgumentNullException.ThrowIfNull(session);

        error = null;
        if (!IsAvailable)
        {
            error = "The loaded ONNX Runtime managed binding does not expose the SetEpDynamicOptions surface.";
            return false;
        }

        IntPtr handle = (IntPtr?)HandleField?.GetValue(session) ?? IntPtr.Zero;
        if (handle == IntPtr.Zero)
        {
            error = "The inference session has no native handle (disposed?).";
            return false;
        }

        DSetEpDynamicOptions setEp;
        DOrtGetErrorMessage getErrorMessage;
        DOrtReleaseStatus releaseStatus;
        try
        {
            setEp = Marshal.GetDelegateForFunctionPointer<DSetEpDynamicOptions>(SetEpDynamicOptionsPointer);
            getErrorMessage = Marshal.GetDelegateForFunctionPointer<DOrtGetErrorMessage>(GetErrorMessagePointer);
            releaseStatus = Marshal.GetDelegateForFunctionPointer<DOrtReleaseStatus>(ReleaseStatusPointer);
        }
        catch (Exception ex)
        {
            error = $"Resolving the SetEpDynamicOptions function pointer failed: {ex.Message}";
            return false;
        }

        // Two UTF-8 NUL-terminated strings plus two arrays of pointers, per the C API.
        byte[] keyBytes = [.. "ep.dynamic.workload_type"u8.ToArray(), 0];
        byte[] valueBytes = [.. Encoding.UTF8.GetBytes(value), 0];
        IntPtr keyPtr = IntPtr.Zero, valuePtr = IntPtr.Zero, keysArray = IntPtr.Zero, valuesArray = IntPtr.Zero;
        try
        {
            keyPtr = Marshal.AllocHGlobal(keyBytes.Length);
            valuePtr = Marshal.AllocHGlobal(valueBytes.Length);
            keysArray = Marshal.AllocHGlobal(IntPtr.Size);
            valuesArray = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.Copy(keyBytes, 0, keyPtr, keyBytes.Length);
            Marshal.Copy(valueBytes, 0, valuePtr, valueBytes.Length);
            Marshal.WriteIntPtr(keysArray, keyPtr);
            Marshal.WriteIntPtr(valuesArray, valuePtr);

            IntPtr status = setEp(handle, keysArray, valuesArray, (UIntPtr)1);
            if (status == IntPtr.Zero)
            {
                return true;
            }

            error = Marshal.PtrToStringUTF8(getErrorMessage(status)) ?? "<null status message>";
            releaseStatus(status);
            return false;
        }
        catch (Exception ex)
        {
            error = $"SetEpDynamicOptions failed: {ex.Message}";
            return false;
        }
        finally
        {
            if (keysArray != IntPtr.Zero) Marshal.FreeHGlobal(keysArray);
            if (valuesArray != IntPtr.Zero) Marshal.FreeHGlobal(valuesArray);
            if (keyPtr != IntPtr.Zero) Marshal.FreeHGlobal(keyPtr);
            if (valuePtr != IntPtr.Zero) Marshal.FreeHGlobal(valuePtr);
        }
    }
}
