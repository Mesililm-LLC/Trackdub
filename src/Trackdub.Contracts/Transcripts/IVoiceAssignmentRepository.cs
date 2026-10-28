using Trackdub.Domain.Tts;

namespace Trackdub.Contracts.Transcripts;

public interface IVoiceAssignmentRepository
{
    /// <summary>Finds a voice assignment by ID, including one retained for historical takes.</summary>
    Task<VoiceAssignment?> GetByIdAsync(Guid projectId, Guid id, CancellationToken cancellationToken);
    Task<VoiceAssignment?> GetAsync(Guid projectId, Guid speakerId, CancellationToken cancellationToken);
    Task<IReadOnlyList<VoiceAssignment>> GetAllAsync(Guid projectId, CancellationToken cancellationToken);
    Task SaveAsync(VoiceAssignment assignment, CancellationToken cancellationToken);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken);
}
