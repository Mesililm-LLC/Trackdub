using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Trackdub.Analyzers;

/// <summary>
/// Flags callers inside methods whose name contains Mix, Mixer, Blend, or Render that do
/// not pass <c>normalizePeak: true</c> to <see cref="WavePcm16"/>.
/// <see cref="WriteSamplesAsync(string, IReadOnlyList{float}, int, int, bool, CancellationToken)"/>.
///
/// Cumulative mixes (multichannel downmix, ambient-layer + dubbed speech, ducking
/// regions) reach sample values outside [-1, 1]. Without the per-track scaler,
/// the post-scale <see cref="Math.Clamp(double, double, double)"/> branch
/// hard-clips to <c>short.MaxValue</c> and loses dynamic information.
///
/// Convention: any method that buffers a multi-source or post-transform mixed
/// output passing through WavePcm16 must opt in via <c>normalizePeak: true</c>.
/// See <c>docs/adr/ADR-0012-wave-pcm16-loudness-policy.md</c>.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class WavePcm16MultiSourceMixOptInAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "TRACKDUB001";

    private const string HelpLink = "https://github.com/MesilimLLC/Trackdub/blob/main/docs/adr/ADR-0012-wave-pcm16-loudness-policy.md";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "Multi-source mix missing normalizePeak opt-in",
        "Method '{0}' is named like a Mix/Mixer/Blend/Render but does not pass normalizePeak: true to WavePcm16.WriteSamplesAsync. Cumulative mixes will hard-clip to short.MaxValue. See ADR-0012.",
        "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        helpLinkUri: HelpLink);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterOperationAction(AnalyzeInvocation, OperationKind.Invocation);
    }

    private const string TargetNamespace = "Trackdub.Media.Waveforms";

    private static void AnalyzeInvocation(OperationAnalysisContext context)
    {
        var invocation = (IInvocationOperation)context.Operation;
        IMethodSymbol method = invocation.TargetMethod;

        // Restrict to Trackdub.Media.Waveforms.WavePcm16 so other static types named WavePcm16 in
        // different namespaces (e.g., test stubs, future sibling writers) do not false-positive.
        if (method.ContainingType is not INamedTypeSymbol containingType)
        {
            return;
        }

        if (!containingType.ContainingNamespace.ToDisplayString().Equals(TargetNamespace, StringComparison.Ordinal)
            || containingType.Name != "WavePcm16")
        {
            return;
        }

        if (method.Name != "WriteSamplesAsync")
        {
            return;
        }

        // Only the 6-arg overload of WriteSamplesAsync declares normalizePeak explicitly. The 5-arg
        // overload defaults it to false via internal forwarding. Either way: only the literal-true
        // arg counts as opt-in. Missing, false, or non-constant args all leave the call to clip.
        bool optedIn = false;
        foreach (IArgumentOperation argument in invocation.Arguments)
        {
            if (argument.Parameter?.Name == "normalizePeak"
                && argument.Value.ConstantValue.HasValue
                && argument.Value.ConstantValue.Value is true)
            {
                optedIn = true;
                break;
            }
        }

        if (optedIn)
        {
            return;
        }

        if (context.ContainingSymbol is not ISymbol containingSymbol)
        {
            return;
        }

        string methodName = containingSymbol.Name;
        bool mixLike =
            methodName.Contains("Mix", StringComparison.Ordinal)
            || methodName.Contains("Mixer", StringComparison.Ordinal)
            || methodName.Contains("Blend", StringComparison.Ordinal)
            || methodName.Contains("Render", StringComparison.Ordinal);

        if (!mixLike)
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.Syntax.GetLocation(), methodName));
    }
}
