namespace Trackdub.Architecture.Tests;

/// <summary>
/// Guards the pull-request triggers that keep stacked pull requests (base = another branch) in CI.
/// </summary>
/// <remarks>
/// A <c>branches:</c> filter on <c>pull_request</c> silently drops every stacked pull request from
/// the run: the workflow fires only for pull requests whose base is <c>main</c>, so a stacked branch
/// reaches <c>main</c> without ever having run those checks. Asserting the filter out here stops a
/// later edit from reintroducing it unnoticed.
/// <para>
/// Only <c>branches</c> is rejected. A <c>paths</c> filter is not a base-branch filter: it selects
/// which changes run the workflow and applies identically to every base branch, so
/// <c>model-audit.yml</c> and <c>benchmark-report-validation.yml</c> keep theirs deliberately.
/// </para>
/// </remarks>
public sealed class WorkflowTriggerTests
{
    [Theory]
    [InlineData(".github/workflows/ci.yml")]
    [InlineData(".github/workflows/model-audit.yml")]
    [InlineData(".github/workflows/benchmark-report-validation.yml")]
    public void Pull_request_trigger_is_not_narrowed_to_a_base_branch(string workflowPath)
    {
        string workflow = File.ReadAllText(ResolveRepositoryFile(workflowPath));

        IReadOnlyList<string> filters = ReadTriggerFilters(workflow, "pull_request");

        Assert.DoesNotContain(
            "branches",
            filters);
    }

    [Fact]
    public void Base_branch_filter_is_detected_on_the_trigger_that_carries_it()
    {
        // A missing "branches" is what the guard above wants to see, and it is also what a reader that
        // cannot see anything would return, so pin both directions and the sibling trigger
        // boundaries on samples rather than trusting the real file alone.
        Assert.Contains("branches", ReadTriggerFilters(NarrowedSample, "pull_request"));
        Assert.Empty(ReadTriggerFilters(UnfilteredSample, "pull_request"));
        Assert.Contains("branches", ReadTriggerFilters(UnfilteredSample, "push"));

        // A paths-only trigger is narrowed, yet not by base branch, so it must survive the guard
        // above. Without this the reader could start filtering everything out and the guard would
        // pass for the wrong reason.
        Assert.Equal(["paths"], ReadTriggerFilters(PathFilteredSample, "pull_request"));
        Assert.Contains("branches", ReadTriggerFilters(PathFilteredSample, "push"));
    }

    private const string NarrowedSample = """
        name: CI

        on:
          push:
            branches: [main]
          pull_request:
            branches: [main]
          workflow_dispatch:
        """;

    private const string UnfilteredSample = """
        name: CI

        on:
          push:
            branches: [main]
          # A comment at trigger indentation, between the two triggers.
          pull_request:
          workflow_dispatch:
        """;

    private const string PathFilteredSample = """
        name: Model manifest audit

        on:
          push:
            branches: [main]
            paths:
              - "src/Trackdub.Inference/Runtime/ModelManifest/**"
          pull_request:
            paths:
              - "src/Trackdub.Inference/Runtime/ModelManifest/**"
          workflow_dispatch:
        """;

    /// <summary>
    /// Returns the filter keys declared directly under <paramref name="trigger"/> in the workflow's
    /// top-level <c>on:</c> block — <c>branches</c>, <c>paths</c>, <c>types</c> and friends. An
    /// empty result means the trigger runs for every value along that axis.
    /// </summary>
    private static IReadOnlyList<string> ReadTriggerFilters(string workflow, string trigger)
    {
        string[] lines = workflow.ReplaceLineEndings("\n").Split('\n');

        int onIndex = Array.FindIndex(
            lines,
            static line => line.Length > 0 && line[0] != ' ' && line.TrimEnd() == "on:");
        Assert.True(onIndex >= 0, "Workflow declares no top-level 'on:' block.");

        int triggerIndex = -1;
        int triggerIndent = int.MaxValue;
        for (int i = onIndex + 1; i < lines.Length; i++)
        {
            if (IsBlankOrComment(lines[i]))
            {
                continue;
            }

            int indent = IndentOf(lines[i]);
            if (indent == 0)
            {
                break; // the 'on:' block ends at the next top-level key
            }

            if (indent > triggerIndent)
            {
                continue; // a filter of an already seen sibling trigger
            }

            triggerIndent = indent;
            if (ContentOf(lines[i]) == trigger + ":")
            {
                triggerIndex = i;
                break;
            }
        }

        Assert.True(
            triggerIndex >= 0,
            $"Workflow declares no '{trigger}' trigger under its top-level 'on:' block.");

        var filters = new List<string>();
        int? filterIndent = null;
        for (int i = triggerIndex + 1; i < lines.Length; i++)
        {
            if (IsBlankOrComment(lines[i]))
            {
                continue;
            }

            int indent = IndentOf(lines[i]);
            if (indent <= triggerIndent)
            {
                break; // the next sibling trigger, or the end of the 'on:' block
            }

            filterIndent ??= indent;
            if (indent != filterIndent)
            {
                continue; // a value nested under a filter, not a filter key
            }

            string content = ContentOf(lines[i]);
            int colon = content.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0)
            {
                filters.Add(content[..colon].Trim());
            }
        }

        return filters;
    }

    private static string ResolveRepositoryFile(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Join(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate '{relativePath}' walking up from '{AppContext.BaseDirectory}'.");
    }

    private static bool IsBlankOrComment(string line) =>
        string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#');

    private static int IndentOf(string line) => line.Length - line.TrimStart().Length;

    /// <summary>Trims the line and drops any inline comment, so key comparisons ignore both.</summary>
    private static string ContentOf(string line)
    {
        string trimmed = line.TrimEnd();
        int comment = trimmed.IndexOf(" #", StringComparison.Ordinal);
        return (comment >= 0 ? trimmed[..comment] : trimmed).Trim();
    }
}
