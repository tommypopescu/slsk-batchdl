using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Api;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Server;

namespace Tests.Server;

[TestClass]
public class JobRequestMapperTests
{
    [TestMethod]
    public void ApplySelectedFolderSnapshot_RejectsFileOutsideRequestedFolder()
    {
        var request = new StartFolderDownloadRequestDto(
            new AlbumFolderRefDto("local", @"Artist\Album"),
            SelectedFolder: FolderDto(
                @"Artist\Album",
                [FileDto(@"Artist\Other\01. Artist - Track.mp3")]));

        Assert.ThrowsException<ArgumentException>(() =>
            JobRequestMapper.ApplySelectedFolderSnapshot(ResolvedFolder(), request));
    }

    [TestMethod]
    public void ApplySelectedFolderSnapshot_RejectsFileFromDifferentUser()
    {
        var request = new StartFolderDownloadRequestDto(
            new AlbumFolderRefDto("local", @"Artist\Album"),
            SelectedFolder: FolderDto(
                @"Artist\Album",
                [FileDto(@"Artist\Album\01. Artist - Track.mp3", username: "other")]));

        Assert.ThrowsException<ArgumentException>(() =>
            JobRequestMapper.ApplySelectedFolderSnapshot(ResolvedFolder(), request));
    }

    private static AlbumFolder ResolvedFolder()
        => new("local", @"Artist\Album", []);

    private static AlbumFolderDto FolderDto(string folderPath, IReadOnlyList<FileCandidateDto> files)
        => new(
            new AlbumFolderRefDto("local", folderPath),
            "local",
            folderPath,
            new PeerInfoDto("local"),
            files.Count,
            files.Count,
            files,
            IsFullyRetrieved: true);

    private static FileCandidateDto FileDto(string filename, string username = "local")
        => new(
            new FileCandidateRefDto(username, filename),
            username,
            filename,
            new PeerInfoDto(username),
            Size: 123,
            BitRate: null,
            SampleRate: null,
            Length: null,
            Extension: ".mp3");
}
