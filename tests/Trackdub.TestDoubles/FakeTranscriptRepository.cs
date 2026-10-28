using Trackdub.Contracts.Transcripts;
using Trackdub.Domain.Transcript;

namespace Trackdub.TestDoubles;

public sealed class FakeTranscriptRepository : ITranscriptRepository
{
    private readonly List<TranscriptRevision> revisions = [];
    private readonly Dictionary<Guid, List<TranscriptSegment>> segmentsByRevision = [];

    public IReadOnlyList<TranscriptRevision> Revisions => revisions;

    /// <summary>Seed a revision and its segments without triggering revision-number logic.</summary>
    public void Seed(TranscriptRevision revision, IReadOnlyList<TranscriptSegment>? segments = null)
    {
        revisions.Add(revision);
        segmentsByRevision[revision.Id] = segments is null ? [] : [.. segments];
    }

    public Task<TranscriptRevision?> GetCurrentRevisionAsync(Guid projectId, CancellationToken cancellationToken)
    {
        TranscriptRevision? revision = revisions
            .Where(r => r.ProjectId == projectId)
            .OrderByDescending(r => r.RevisionNumber)
            .FirstOrDefault();
        return Task.FromResult(revision);
    }

    public Task<IReadOnlyList<TranscriptSegment>> GetSegmentsAsync(
        Guid transcriptRevisionId,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<TranscriptSegment> result = segmentsByRevision.TryGetValue(
            transcriptRevisionId, out List<TranscriptSegment>? segments)
            ? segments
            : [];
        return Task.FromResult(result);
    }

    public Task<int> GetNextRevisionNumberAsync(Guid projectId, CancellationToken cancellationToken)
    {
        int max = revisions
            .Where(r => r.ProjectId == projectId)
            .Select(r => r.RevisionNumber)
            .DefaultIfEmpty(0)
            .Max();
        return Task.FromResult(max + 1);
    }

    public Task SaveRevisionAsync(
        TranscriptRevision revision,
        IReadOnlyList<TranscriptSegment> segments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(segments);

        int index = revisions.FindIndex(r => r.Id == revision.Id);
        if (index >= 0)
        {
            revisions[index] = revision;
        }
        else
        {
            revisions.Add(revision);
        }

        segmentsByRevision[revision.Id] = [.. segments];
        return Task.CompletedTask;
    }

    public Task ReassignSpeakerAsync(
        Guid projectId,
        Guid sourceSpeakerId,
        Guid targetSpeakerId,
        CancellationToken cancellationToken)
    {
        foreach (var (_, segments) in segmentsByRevision)
        {
            for (int i = 0; i < segments.Count; i++)
            {
                if (segments[i].SpeakerId == sourceSpeakerId)
                {
                    segments[i] = segments[i] with
                    {
                        SpeakerId = targetSpeakerId,
                        SpeakerAssignmentOrigin = SpeakerAssignmentOrigin.Manual,
                    };
                }
            }
        }

        return Task.CompletedTask;
    }

    public Task ReassignAndMergeSpeakersAsync(
        Guid projectId,
        Guid sourceSpeakerId,
        Guid targetSpeakerId,
        CancellationToken cancellationToken) =>
        ReassignSpeakerAsync(projectId, sourceSpeakerId, targetSpeakerId, cancellationToken);
}
