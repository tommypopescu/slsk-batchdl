namespace Sockseek.Core.Models;

// Search/browse result file inside an album folder. This is candidate data only;
// executable per-file download jobs are materialized on AlbumJob.TrackJobs.
public sealed class AlbumFile
{
    public SongQuery Query { get; }
    public FileCandidate Candidate { get; }

    public string Filename => Candidate.Filename;
    public bool IsNotAudio => !Utils.IsMusicFile(Filename);

    public AlbumFile(SongQuery query, FileCandidate candidate)
    {
        Query = new SongQuery(query);
        Candidate = candidate;
    }
}
