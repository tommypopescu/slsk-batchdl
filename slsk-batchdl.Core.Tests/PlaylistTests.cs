using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sldl.Core.Models;
using Sldl.Core.Jobs;
using Sldl.Core;
using Sldl.Core.Services;
using Sldl.Core.Settings;

namespace Tests.Playlist
{
    [TestClass]
    public class PlaylistTests
    {
        private string testM3uPath = null!;

        [TestInitialize]
        public void Setup()
        {
            testM3uPath = Path.Join(Path.GetTempPath(), $"test_playlist_{Guid.NewGuid()}.m3u8");
        }

        [TestCleanup]
        public void Cleanup()
        {
            if (File.Exists(testM3uPath))
                File.Delete(testM3uPath);
        }

        [TestMethod]
        public void Playlist_PreservesOrderAndHandlesDynamicExpansion()
        {
            var queue = new JobList("Test Queue");

            var song1 = new SongJob(new SongQuery { Artist = "Artist1", Title = "Title1" });
            song1.UpdateState(JobState.Done);
            song1.DownloadPath = "Artist1/Title1.mp3";
            queue.Add(song1);

            var album1 = new AlbumJob(new AlbumQuery { Artist = "AlbumArtist", Album = "Album1" });
            var songA = new SongJob(new SongQuery { Artist = "AlbumArtist", Title = "TrackA" });
            songA.UpdateState(JobState.Done);
            songA.DownloadPath = "AlbumArtist/Album1/TrackA.mp3";
            var songB = new SongJob(new SongQuery { Artist = "AlbumArtist", Title = "TrackB" });
            songB.UpdateState(JobState.Done);
            songB.DownloadPath = "AlbumArtist/Album1/TrackB.mp3";
            var folder = new AlbumFolder("user", "AlbumArtist\\Album1", [songA, songB]);
            album1.ResolvedTarget = folder;
            album1.UpdateState(JobState.Done);
            queue.Add(album1);

            var song2 = new SongJob(new SongQuery { Artist = "Artist2", Title = "Title2" });
            song2.UpdateState(JobState.Done);
            song2.DownloadPath = "Artist2/Title2.mp3";
            queue.Add(song2);

            var editor = new M3uEditor(testM3uPath, queue, M3uOption.Playlist, false);
            editor.Update();

            var lines = File.ReadAllLines(testM3uPath);
            Assert.AreEqual(4, lines.Length);
            Assert.AreEqual("Artist1/Title1.mp3", lines[0].Replace('\\', '/'));
            Assert.AreEqual("AlbumArtist/Album1/TrackA.mp3", lines[1].Replace('\\', '/'));
            Assert.AreEqual("AlbumArtist/Album1/TrackB.mp3", lines[2].Replace('\\', '/'));
            Assert.AreEqual("Artist2/Title2.mp3", lines[3].Replace('\\', '/'));
        }

        [TestMethod]
        public void Playlist_AggregateJob_OnlyEmitsSuccessfulVariant()
        {
            var queue = new JobList("Test Queue");

            var agg = new AggregateJob(new SongQuery { Artist = "Artist", Title = "Title" });
            var variant1 = new SongJob(new SongQuery { Artist = "Artist", Title = "Title" });
            variant1.Fail(FailureReason.NoSuitableFileFound);
            var variant2 = new SongJob(new SongQuery { Artist = "Artist", Title = "Title (Remix)" });
            variant2.UpdateState(JobState.Done);
            variant2.DownloadPath = "Artist/Title (Remix).mp3";
            var variant3 = new SongJob(new SongQuery { Artist = "Artist", Title = "Title (Live)" });
            variant3.Fail(FailureReason.AllDownloadsFailed);

            agg.Songs.AddRange([variant1, variant2, variant3]);
            agg.UpdateState(JobState.Done);
            queue.Add(agg);

            var editor = new M3uEditor(testM3uPath, queue, M3uOption.Playlist, false);
            editor.Update();

            var lines = File.ReadAllLines(testM3uPath);
            Assert.AreEqual(1, lines.Length);
            Assert.AreEqual("Artist/Title (Remix).mp3", lines[0].Replace('\\', '/'));
        }
    }
}