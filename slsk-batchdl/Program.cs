﻿using AngleSharp.Text;
using Soulseek;
using System.Collections.Concurrent;
using System.Data;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Net.Sockets;
using Konsole;

using Models;
using Enums;
using Extractors;
using Services;
using static Printing;

using Directory = System.IO.Directory;
using File = System.IO.File;
using SlFile = Soulseek.File;
using System.Text;

public static partial class Program
{
    const int updateInterval = 100;
    public static bool initialized = false;
    public static bool skipUpdate = false;
    public static bool interceptKeys = false;
    public static event EventHandler<ConsoleKey>? keyPressed;

    public static ISoulseekClient client = null!;
    public static IExtractor extractor = null!;
    public static TrackLists trackLists = null!;

    public static readonly ConcurrentDictionary<Track, SearchInfo> searches = new();
    public static readonly ConcurrentDictionary<string, DownloadWrapper> downloads = new();
    public static readonly ConcurrentDictionary<string, int> userSuccessCounts = new();

    private static Searcher searchService = null!;

    public static async Task Main(string[] args)
    {
        Console.ResetColor();
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Help.PrintAndExitIfNeeded(args);

        var config = new Config(args);

        if (config.input.Length == 0)
            Config.InputError($"No input provided");

        (config.inputType, extractor) = ExtractorRegistry.GetMatchingExtractor(config.input, config.inputType);

        Console.WriteLine($"Item: {config.input} [{config.inputType}]");
        
        trackLists = await extractor.GetTracks(config.input, config.maxTracks, config.offset, config.reverse, config);

        WriteLineIf("Got tracks", config.debugInfo);

        config.PostProcessArgs();

        trackLists.UpgradeListTypes(config.aggregate, config.album);
        trackLists.SetListEntryOptions();

        PrepareListEntries(config);

        await MainLoop();

        WriteLineIf("Mainloop done", config.debugInfo);
    }


    public static async Task InitClientAndUpdateIfNeeded(Config config)
    {
        if (initialized)
            return;

        bool needLogin = !config.PrintTracks;
        if (needLogin)
        {
            // If client is not null, assume it's injected for testing
            if (client == null)
            {
                var connectionOptions = new ConnectionOptions(configureSocket: (socket) =>
                {
                    socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                    socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
                    socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 15);
                    socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 15);
                });

                var clientOptions = new SoulseekClientOptions(
                    transferConnectionOptions: connectionOptions,
                    serverConnectionOptions: connectionOptions,
                    listenPort: config.listenPort
                );

                client = new SoulseekClient(clientOptions);
            }

            if (!config.useRandomLogin && (string.IsNullOrEmpty(config.username) || string.IsNullOrEmpty(config.password)))
                Config.InputError("No soulseek username or password");

            await Login(config, config.useRandomLogin);

            var searchSemaphore = new RateLimitedSemaphore(config.searchesPerTime, TimeSpan.FromSeconds(config.searchRenewTime));
            searchService = new Searcher(client, searchSemaphore);

            var UpdateTask = Task.Run(() => Update(config));
            WriteLineIf("Update started", config.debugInfo);
        }

        initialized = true;
    }


    static void PreprocessTracks(TrackListEntry tle)
    {
        static void preprocessTrack(Config config, Track track)
        {
            if (config.removeFt)
            {
                track.Title = track.Title.RemoveFt();
                track.Artist = track.Artist.RemoveFt();
            }
            if (config.removeBrackets)
            {
                track.Title = track.Title.RemoveSquareBrackets();
            }
            if (config.regexToReplace.Title.Length + config.regexToReplace.Artist.Length + config.regexToReplace.Album.Length > 0)
            {
                track.Title = Regex.Replace(track.Title, config.regexToReplace.Title, config.regexReplaceBy.Title);
                track.Artist = Regex.Replace(track.Artist, config.regexToReplace.Artist, config.regexReplaceBy.Artist);
                track.Album = Regex.Replace(track.Album, config.regexToReplace.Album, config.regexReplaceBy.Album);
            }
            if (config.artistMaybeWrong)
            {
                track.ArtistMaybeWrong = true;
            }

            track.Artist = track.Artist.Trim();
            track.Album = track.Album.Trim();
            track.Title = track.Title.Trim();
        }

        preprocessTrack(tle.config, tle.source);
        
        for (int k = 0; k < tle.list.Count; k++)
        {
            foreach (var ls in tle.list)
            {
                for (int i = 0; i < ls.Count; i++)
                {
                    preprocessTrack(tle.config, ls[i]);
                }
            }
        }
    }


    static void PrepareListEntries(Config startConfig)
    {
        var editors = new Dictionary<(string path, M3uOption option), M3uEditor>();
        var skippers = new Dictionary<(string dir, SkipMode mode, bool checkCond), FileSkipper>();

        foreach (var tle in trackLists.lists)
        {
            tle.config = startConfig.Copy();
            tle.config.UpdateProfiles(tle);
            startConfig = tle.config;

            if (tle.extractorCond != null)
            {
                tle.config.necessaryCond.AddConditions(tle.extractorCond);
                tle.extractorCond = null;
            }
            if (tle.extractorPrefCond != null)
            {
                tle.config.preferredCond.AddConditions(tle.extractorPrefCond);
                tle.extractorPrefCond = null;
            }

            var indexOption = tle.config.writeIndex ? M3uOption.Index : M3uOption.None;
            if (indexOption != M3uOption.None || (tle.config.skipExisting && tle.config.skipMode == SkipMode.Index) || tle.config.skipNotFound)
            {
                string indexPath;
                if (tle.config.indexFilePath.Length > 0)
                    indexPath = tle.config.indexFilePath.Replace("{playlist-name}", tle.ItemNameOrSource().ReplaceInvalidChars(" ").Trim());
                else
                    indexPath = Path.Join(tle.config.parentDir, tle.DefaultFolderName(), "_index.sldl");

                if (editors.TryGetValue((indexPath, indexOption), out var indexEditor))
                {
                    tle.indexEditor = indexEditor;
                }
                else
                {
                    tle.indexEditor = new M3uEditor(indexPath, trackLists, indexOption, true);
                    editors.Add((indexPath, indexOption), tle.indexEditor);
                }
            }

            var playlistOption = tle.config.writePlaylist ? M3uOption.Playlist : M3uOption.None;
            if (playlistOption != M3uOption.None)
            {
                string m3uPath;
                if (tle.config.m3uFilePath.Length > 0)
                    m3uPath = tle.config.m3uFilePath.Replace("{playlist-name}", tle.ItemNameOrSource().ReplaceInvalidChars(" ").Trim());
                else
                    m3uPath = Path.Join(tle.config.parentDir, tle.DefaultFolderName(), tle.DefaultPlaylistName());

                if (editors.TryGetValue((m3uPath, playlistOption), out var playlistEditor))
                {
                    tle.playlistEditor = playlistEditor;
                }
                else
                {
                    tle.playlistEditor = new M3uEditor(m3uPath, trackLists, playlistOption, false);
                    editors.Add((m3uPath, playlistOption), tle.playlistEditor);
                }
            }

            if (tle.config.skipExisting)
            {
                bool checkCond = tle.config.skipCheckCond || tle.config.skipCheckPrefCond;

                if (skippers.TryGetValue((tle.config.parentDir, tle.config.skipMode, checkCond), out var outputDirSkipper))
                {
                    tle.outputDirSkipper = outputDirSkipper;
                }
                else
                {
                    tle.outputDirSkipper = FileSkipperRegistry.GetSkipper(tle.config.skipMode, tle.config.parentDir, checkCond);
                    skippers.Add((tle.config.parentDir, tle.config.skipMode, checkCond), tle.outputDirSkipper);
                }

                if (tle.config.skipMusicDir.Length > 0)
                {
                    if (skippers.TryGetValue((tle.config.skipMusicDir, tle.config.skipModeMusicDir, checkCond), out var musicDirSkipper))
                    {
                        tle.musicDirSkipper = musicDirSkipper;
                    }
                    else
                    {
                        tle.musicDirSkipper = FileSkipperRegistry.GetSkipper(tle.config.skipModeMusicDir, tle.config.skipMusicDir, checkCond);
                        skippers.Add((tle.config.skipMusicDir, tle.config.skipModeMusicDir, checkCond), tle.musicDirSkipper);
                    }
                }
            }
        }
    }


    static async Task MainLoop()
    {
        if (trackLists.Count == 0) return;

        var tle0 = trackLists.lists[0];
        bool enableParallelSearch = tle0.config.parallelAlbumSearch && !tle0.config.PrintResults && !tle0.config.PrintTracks && trackLists.lists.Any(x => x.CanParallelSearch);
        var parallelSearches = new List<(TrackListEntry tle, Task<(bool, ResponseData)> task)>();
        var parallelSearchSemaphore = new SemaphoreSlim(tle0.config.parallelAlbumSearchProcesses);

        tle0.PrintLines();

        for (int i = 0; i < trackLists.lists.Count; i++)
        {
            if (!enableParallelSearch && i > 0) Console.WriteLine();

            var tle = trackLists[i];
            var config = tle.config;

            if (tle.preprocessTracks) PreprocessTracks(tle);
            if (!enableParallelSearch) tle.PrintLines();

            var existing = new List<Track>();
            var notFound = new List<Track>();

            if (config.skipNotFound && !config.PrintResults)
            {
                if (tle.sourceCanBeSkipped && SetNotFoundLastTime(config, tle.source, tle.indexEditor))
                    notFound.Add(tle.source);

                if (tle.source.State != TrackState.NotFoundLastTime && !tle.needSourceSearch)
                {
                    foreach (var tracks in tle.list)
                        notFound.AddRange(DoSkipNotFound(config, tracks, tle.indexEditor));
                }
            }

            if (config.skipExisting && !config.PrintResults && tle.source.State != TrackState.NotFoundLastTime)
            {
                if (tle.sourceCanBeSkipped && SetExisting(tle, FileSkipperContext.FromTrackListEntry(tle), tle.source))
                    existing.Add(tle.source);

                if (tle.source.State != TrackState.AlreadyExists && !tle.needSourceSearch)
                {
                    foreach (var tracks in tle.list)
                        existing.AddRange(DoSkipExisting(tle, tracks));
                }
            }

            if (config.PrintTracks)
            {
                if (tle.source.Type == TrackType.Normal)
                {
                    PrintTracksTbd(tle.list[0].Where(t => t.State == TrackState.Initial).ToList(), existing, notFound, tle.source.Type, config);
                }
                else
                {
                    var tl = new List<Track>();
                    if (tle.source.State == TrackState.Initial) tl.Add(tle.source);
                    PrintTracksTbd(tl, existing, notFound, tle.source.Type, config, summary: false);
                }
                continue;
            }

            if (tle.sourceCanBeSkipped)
            {
                if (tle.source.State == TrackState.AlreadyExists)
                {
                    Console.WriteLine($"{tle.source.Type} download '{tle.source.ToString(true)}' already exists at {tle.source.DownloadPath}, skipping");
                    continue;
                }
            
                if (tle.source.State == TrackState.NotFoundLastTime)
                {
                    Console.WriteLine($"{tle.source.Type} download '{tle.source.ToString(true)}' was not found during a prior run, skipping");
                    continue;
                }
            }

            if (tle.needSourceSearch)
            {
                await InitClientAndUpdateIfNeeded(config);

                ProgressBar? progress = null;

                async Task<(bool, ResponseData)> sourceSearch()
                {
                    await parallelSearchSemaphore.WaitAsync();

                    progress = enableParallelSearch ? Printing.GetProgressBar(config) : null;
                    var part = progress == null ? "" : "  ";
                    Printing.RefreshOrPrint(progress, 0, $"{part}{tle.source.Type} download: {tle.source.ToString(true)}, searching..", print: true);

                    bool foundSomething = false;
                    var responseData = new ResponseData();

                    if (tle.source.Type == TrackType.Album)
                    {
                        tle.list = await searchService.GetAlbumDownloads(tle.source, responseData, config);
                        foundSomething = tle.list.Count > 0 && tle.list[0].Count > 0;
                    }
                    else if (tle.source.Type == TrackType.Aggregate)
                    {
                        tle.list.Insert(0, await searchService.GetAggregateTracks(tle.source, responseData, config));
                        foundSomething = tle.list.Count > 0 && tle.list[0].Count > 0;
                    }
                    else if (tle.source.Type == TrackType.AlbumAggregate)
                    {
                        var res = await searchService.GetAggregateAlbums(tle.source, responseData, config);

                        foreach (var item in res)
                        {
                            var newSource = new Track(tle.source) { Type = TrackType.Album, ItemNumber = -1 };
                            var albumTle = new TrackListEntry() 
                            {
                                source = newSource,
                                list = item,
                                config = config,
                                needSourceSearch = false,
                                sourceCanBeSkipped = true,
                                preprocessTracks = false,
                                indexEditor = tle.indexEditor,
                                playlistEditor = tle.playlistEditor,
                            };
                            albumTle.itemName = tle.itemName;
                            trackLists.AddEntry(albumTle);
                        }

                        foundSomething = res.Count > 0;
                    }

                    tle.needSourceSearch = false;

                    if (!foundSomething)
                    {
                        var lockedFiles = responseData.lockedFilesCount > 0 ? $" (Found {responseData.lockedFilesCount} locked files)" : "";
                        Printing.RefreshOrPrint(progress, 0, $"No results: {tle.source}{lockedFiles}", true);
                    }
                    else if (progress != null)
                    {
                        Printing.RefreshOrPrint(progress, 0, $"Found results: {tle.source}", true);
                    }

                    parallelSearchSemaphore.Release();

                    return (foundSomething, responseData);
                }

                if (!enableParallelSearch || !tle.CanParallelSearch)
                {
                    (bool foundSomething, ResponseData responseData) = await sourceSearch();

                    if (!foundSomething)
                    {
                        if (!config.PrintResults)
                        {
                            tle.source.State = TrackState.Failed;
                            tle.source.FailureReason = FailureReason.NoSuitableFileFound;
                            tle.indexEditor?.Update();
                        }

                        continue;
                    }

                    if (config.skipExisting && tle.needSkipExistingAfterSearch)
                    {
                        foreach (var tracks in tle.list)
                            existing.AddRange(DoSkipExisting(tle, tracks));
                    }

                    if (tle.gotoNextAfterSearch)
                    {
                        continue;
                    }
                }
                else
                {
                    parallelSearches.Add((tle, sourceSearch()));
                    continue;
                }
            }

            if (config.PrintResults)
            {
                await InitClientAndUpdateIfNeeded(config);
                await PrintResults(tle, existing, notFound, config, searchService);
                continue;
            }

            if (parallelSearches.Count > 0 && !tle.CanParallelSearch)
            {
                await parallelDownloads();
            }

            if (!enableParallelSearch || !tle.CanParallelSearch)
            {
                await download(tle, config, notFound, existing);
            }
        }

        if (parallelSearches.Count > 0)
        {
            await parallelDownloads();
        }

        if (!trackLists[^1].config.DoNotDownload && (trackLists.lists.Count > 0 || trackLists.Flattened(false, false).Skip(1).Any()))
        {
            PrintComplete(trackLists);
        }

        async Task download(TrackListEntry tle, Config config, List<Track>? notFound, List<Track>? existing)
        {
            tle.indexEditor?.Update();
            tle.playlistEditor?.Update();

            if (tle.source.Type != TrackType.Album)
            {
                PrintTracksTbd(tle.list[0].Where(t => t.State == TrackState.Initial).ToList(), existing, notFound, tle.source.Type, config);
            }

            if (notFound != null && existing != null && notFound.Count + existing.Count >= tle.list.Sum(x => x.Count))
            {
                return;
            }

            await InitClientAndUpdateIfNeeded(config);

            if (tle.source.Type == TrackType.Normal)
            {
                await DownloadNormal(config, tle);
            }
            else if (tle.source.Type == TrackType.Album)
            {
                await DownloadAlbum(config, tle);
            }
            else if (tle.source.Type == TrackType.Aggregate)
            {
                await DownloadNormal(config, tle);
            }
        }

        async Task parallelDownloads()
        {
            await Task.WhenAll(parallelSearches.Select(x => x.task));

            foreach (var (tle, task) in parallelSearches)
            {
                (bool foundSomething, var responseData) = task.Result;

                if (foundSomething)
                {
                    Console.WriteLine($"Downloading: {tle.source}");
                    await download(tle, tle.config, null, null);
                }
                else
                {
                    if (!tle.config.PrintResults)
                    {
                        tle.source.State = TrackState.Failed;
                        tle.source.FailureReason = FailureReason.NoSuitableFileFound;
                        tle.indexEditor?.Update();
                    }
                    if (tle.config.skipExisting && tle.needSkipExistingAfterSearch)
                    {
                        foreach (var tracks in tle.list)
                            DoSkipExisting(tle, tracks);
                    }
                }
            }

            parallelSearches.Clear();
        }
    }


    static List<Track> DoSkipExisting(TrackListEntry tle, List<Track> tracks)
    {
        var context = FileSkipperContext.FromTrackListEntry(tle);
        var existing = new List<Track>();
        foreach (var track in tracks)
        {
            if (SetExisting(tle, context, track))
            {
                existing.Add(track);
            }
        }
        return existing;
    }


    static bool SetExisting(TrackListEntry tle, FileSkipperContext context, Track track)
    {
        string? path = null;

        if (tle.outputDirSkipper != null)
        {
            if (!tle.outputDirSkipper.IndexIsBuilt)
                tle.outputDirSkipper.BuildIndex();

            tle.outputDirSkipper.TrackExists(track, context, out path);
        }

        if (path == null && tle.musicDirSkipper != null)
        {
            if (!tle.musicDirSkipper.IndexIsBuilt)
            {
                Console.WriteLine($"Building music directory index..");
                tle.musicDirSkipper.BuildIndex();
            }

            tle.musicDirSkipper.TrackExists(track, context, out path);
        }

        if (path != null)
        {
            track.State = TrackState.AlreadyExists;
            track.DownloadPath = path;
        }

        return path != null;
    }


    static List<Track> DoSkipNotFound(Config config, List<Track> tracks, M3uEditor indexEditor)
    {
        var notFound = new List<Track>();
        foreach (var track in tracks)
        {
            if (SetNotFoundLastTime(config, track, indexEditor))
            {
                notFound.Add(track);
            }
        }
        return notFound;
    }


    static bool SetNotFoundLastTime(Config config, Track track, M3uEditor indexEditor)
    {
        if (indexEditor.TryGetPreviousRunResult(track, out var prevTrack))
        {
            if (prevTrack.FailureReason == FailureReason.NoSuitableFileFound || prevTrack.State == TrackState.NotFoundLastTime)
            {
                track.State = TrackState.NotFoundLastTime;
                return true;
            }
        }
        return false;
    }


    static async Task DownloadNormal(Config config, TrackListEntry tle)
    {
        var tracks = tle.list[0];

        var semaphore = new SemaphoreSlim(config.concurrentProcesses);

        var organizer = new FileManager(tle, config);

        var downloadTasks = tracks.Select(async (track, index) =>
        {
            using var cts = new CancellationTokenSource();
            await DownloadTask(config, tle, track, semaphore, organizer, cts, false, true, true);
            tle.indexEditor?.Update();
            tle.playlistEditor?.Update();
        });

        await Task.WhenAll(downloadTasks);

        if (config.removeTracksFromSource && tracks.All(t => t.State == TrackState.Downloaded || t.State == TrackState.AlreadyExists))
            await extractor.RemoveTrackFromSource(tle.source);
    }


    static async Task DownloadAlbum(Config config, TrackListEntry tle)
    {
        var organizer = new FileManager(tle, config);
        List<Track>? tracks = null;
        var retrievedFolders = new HashSet<string>();
        bool succeeded = false;
        string? soulseekDir = null;
        int index = 0;

        async Task runAlbumDownloads(List<Track> tracks, SemaphoreSlim semaphore, CancellationTokenSource cts)
        {
            var downloadTasks = tracks.Select(async track =>
            {
                await DownloadTask(config, tle, track, semaphore, organizer, cts, true, true, true);
            });
            await Task.WhenAll(downloadTasks);
        }

        while (tle.list.Count > 0 && !config.albumArtOnly)
        {
            bool wasInteractive = config.interactiveMode;
            bool retrieveCurrent = true;
            index = 0;

            if (config.interactiveMode)
            {
                (index, tracks, retrieveCurrent) = await InteractiveModeAlbum(tle, tle.list, true, retrievedFolders);
                if (index == -1) break;
                if (index == -2) Environment.Exit(0);
            }
            else
            {
                tracks = tle.list[index];
            }

            soulseekDir = Utils.GreatestCommonDirectorySlsk(tracks.Select(t => t.FirstDownload.Filename));

            organizer.SetRemoteCommonDir(soulseekDir);

            if (!config.interactiveMode && !wasInteractive)
            {
                Console.WriteLine();
                PrintAlbum(tracks);
            }

            using var semaphore = new SemaphoreSlim(999); // Needs to be uncapped due to a bug that causes album downloads to fail after some time
            using var cts = new CancellationTokenSource();

            bool userCancelled = false;
            void onKeyPressed(object? sender, ConsoleKey key)
            {
                if (key == ConsoleKey.C)
                {
                    userCancelled = true;
                    cts.Cancel();
                }
            }
            interceptKeys = true;
            keyPressed += onKeyPressed;

            try
            {
                await runAlbumDownloads(tracks, semaphore, cts);

                if (!config.noBrowseFolder && retrieveCurrent && !retrievedFolders.Contains(soulseekDir))
                {
                    Console.WriteLine("Getting all files in folder...");

                    int newFilesFound = await searchService.CompleteFolder(tracks, tracks[0].FirstResponse, soulseekDir);
                    retrievedFolders.Add(tracks[0].FirstUsername + '\\' + soulseekDir);

                    if (newFilesFound > 0)
                    {
                        Console.WriteLine($"Found {newFilesFound} more files, downloading:");
                        await runAlbumDownloads(tracks, semaphore, cts);
                    }
                    else
                    {
                        Console.WriteLine("No more files found.");
                    }
                }

                succeeded = true;
                break;
            }
            catch (OperationCanceledException)
            {
                if (userCancelled)
                {
                    Console.WriteLine("\nDownload cancelled. ");

                    if (tracks.Any(t => t.State == TrackState.Downloaded && t.DownloadPath.Length > 0))
                    {
                        var defaultAction = config.DeleteAlbumOnFail ? "Yes" : config.IgnoreAlbumFail ? "No" : $"Move to {config.failedAlbumPath}";
                        Console.Write($"Delete files? [Y/n] (default: {defaultAction}): ");
                        var res = Console.ReadLine().Trim().ToLower();
                        if (res == "y") 
                            OnAlbumFail(tracks, true, config);
                        else if (res == "" && !config.IgnoreAlbumFail) 
                            OnAlbumFail(tracks, config.DeleteAlbumOnFail, config);
                    }

                    if (!config.interactiveMode)
                    {
                        Console.WriteLine("Entering interactive mode");
                        config.interactiveMode = true;
                        tle.config.UpdateProfiles(tle);
                        tle.PrintLines();
                    }
                }
                else
                {
                    if (!config.IgnoreAlbumFail) 
                        OnAlbumFail(tracks, config.DeleteAlbumOnFail, config); 
                }
            }
            finally
            {
                interceptKeys = false;
                keyPressed -= onKeyPressed;
            }

            if (!succeeded)
            {
                organizer.SetRemoteCommonDir(null);
                tle.list.RemoveAt(index);
            }
        }

        if (succeeded)
        {
            tle.source.State = TrackState.Downloaded;

            var downloadedAudio = tracks.Where(t => !t.IsNotAudio && t.State == TrackState.Downloaded && t.DownloadPath.Length > 0);
            if (downloadedAudio.Any())
            {
                tle.source.DownloadPath = Utils.GreatestCommonDirectory(downloadedAudio.Select(t => t.DownloadPath));

                if (config.removeTracksFromSource)
                {
                    await extractor.RemoveTrackFromSource(tle.source);
                }
            }
        }
        else if (index != -1)
        {
            tle.source.State = TrackState.Failed;
        }

        List<Track>? additionalImages = null;
        
        if (config.albumArtOnly || succeeded && config.albumArtOption != AlbumArtOption.Default)
        {
            Console.WriteLine($"\nDownloading additional images:");
            additionalImages = await DownloadImages(config, tle, tle.list, config.albumArtOption, tle.list[index], organizer);
            tracks?.AddRange(additionalImages);
        }

        if (tracks != null && tle.source.DownloadPath.Length > 0)
        {
            organizer.OrganizeAlbum(tle.source, tracks, additionalImages);
        }

        tle.indexEditor?.Update();
        tle.playlistEditor?.Update();

        OnComplete(tle, tle.source, true, tle.indexEditor, tle.playlistEditor);
    }


    static void OnAlbumFail(List<Track>? tracks, bool deleteDownloaded, Config config)
    {
        if (tracks == null) return;

        foreach (var track in tracks)
        {
            if (track.DownloadPath.Length > 0 && File.Exists(track.DownloadPath))
            {
                try
                {
                    if (deleteDownloaded || track.DownloadPath.EndsWith(".incomplete"))
                    {
                        File.Delete(track.DownloadPath);
                    }
                    else if (config.failedAlbumPath.Length > 0)
                    {
                        var newPath = Path.Join(config.failedAlbumPath, Path.GetRelativePath(config.parentDir, track.DownloadPath));
                        Directory.CreateDirectory(Path.GetDirectoryName(newPath));
                        Utils.Move(track.DownloadPath, newPath);
                    }

                    Utils.DeleteAncestorsIfEmpty(Path.GetDirectoryName(track.DownloadPath), config.parentDir);
                }
                catch (Exception e) 
                {
                    Printing.WriteLine($"Error: Unable to move or delete file '{track.DownloadPath}' after album fail: {e}");
                }
            }
        }
    }


    static async Task<List<Track>> DownloadImages(Config config, TrackListEntry tle, List<List<Track>> downloads, AlbumArtOption option, List<Track>? chosenAlbum, FileManager fileManager)
    {
        var downloadedImages = new List<Track>();
        long mSize = 0;
        int mCount = 0;

        if (chosenAlbum != null)
        {
            string dir = Utils.GreatestCommonDirectorySlsk(chosenAlbum.Select(t => t.FirstDownload.Filename));
            fileManager.SetDefaultFolderName(Path.GetFileName(Utils.NormalizedPath(dir))); 
        }

        if (option == AlbumArtOption.Default)
            return downloadedImages;

        int[]? sortedLengths = null;

        if (chosenAlbum != null && chosenAlbum.Any(t => !t.IsNotAudio))
            sortedLengths = chosenAlbum.Where(t => !t.IsNotAudio).Select(t => t.Length).OrderBy(x => x).ToArray();

        var albumArts = downloads
            .Where(ls => chosenAlbum == null || searchService.AlbumsAreSimilar(chosenAlbum, ls, sortedLengths))
            .Select(ls => ls.Where(t => Utils.IsImageFile(t.FirstDownload.Filename)))
            .Where(ls => ls.Any());

        if (!albumArts.Any())
        {
            Console.WriteLine("No images found");
            return downloadedImages;
        }
        else if (!albumArts.Skip(1).Any() && albumArts.First().All(y => y.State != TrackState.Initial))
        {
            Console.WriteLine("No additional images found");
            return downloadedImages;
        }

        if (option == AlbumArtOption.Largest)
        {
            albumArts = albumArts
                .OrderByDescending(tracks => tracks.Select(t => t.FirstDownload.Size).Max() / 1024 / 100)
                .ThenByDescending(tracks => tracks.First().FirstResponse.UploadSpeed / 1024 / 300)
                .ThenByDescending(tracks => tracks.Select(t => t.FirstDownload.Size).Sum() / 1024 / 100);

            if (chosenAlbum != null)
            {
                mSize = chosenAlbum
                    .Where(t => t.State == TrackState.Downloaded && Utils.IsImageFile(t.DownloadPath))
                    .Select(t => t.FirstDownload.Size)
                    .DefaultIfEmpty(0)
                    .Max();
            }
        }
        else if (option == AlbumArtOption.Most)
        {
            albumArts = albumArts
                .OrderByDescending(tracks => tracks.Count())
                .ThenByDescending(tracks => tracks.First().FirstResponse.UploadSpeed / 1024 / 300)
                .ThenByDescending(tracks => tracks.Select(t => t.FirstDownload.Size).Sum() / 1024 / 100);

            if (chosenAlbum != null)
            {
                mCount = chosenAlbum
                    .Count(t => t.State == TrackState.Downloaded && Utils.IsImageFile(t.DownloadPath));
            }
        }

        var albumArtLists = albumArts.Select(ls => ls.ToList()).ToList();

        bool needImageDownload(List<Track> list)
        {
            if (list.All(t => t.State == TrackState.Downloaded || t.State == TrackState.AlreadyExists))
                return false;
            else if (option == AlbumArtOption.Most)
                return mCount < list.Count;
            else if (option == AlbumArtOption.Largest)
                return mSize < list.Max(t => t.FirstDownload.Size) - 1024 * 50;
            return true;
        }

        while (albumArtLists.Count > 0)
        {
            int index = 0;
            bool wasInteractive = config.interactiveMode;
            List<Track> tracks;

            if (config.interactiveMode)
            {
                (index, tracks, _) = await InteractiveModeAlbum(tle, albumArtLists, false, null);
                if (index == -1) break;
                if (index == -2) Environment.Exit(0);
            }
            else
            {
                tracks = albumArtLists[index];
            }

            albumArtLists.RemoveAt(index);

            if (!needImageDownload(tracks))
            {
                Console.WriteLine("Image requirements already satisfied.");
                return downloadedImages;
            }

            if (!config.interactiveMode && !wasInteractive)
            {
                Console.WriteLine();
                PrintAlbum(tracks);
            }

            fileManager.downloadingAdditinalImages = true;
            fileManager.SetRemoteCommonImagesDir(Utils.GreatestCommonDirectorySlsk(tracks.Select(t => t.FirstDownload.Filename)));

            bool allSucceeded = true;
            using var semaphore = new SemaphoreSlim(1);
            using var cts = new CancellationTokenSource();

            bool userCancelled = false;
            void onKeyPressed(object? sender, ConsoleKey key)
            {
                if (key == ConsoleKey.C)
                {
                    userCancelled = true;
                    cts.Cancel();
                }
            }
            interceptKeys = true;
            keyPressed += onKeyPressed;

            try
            {
                foreach (var track in tracks)
                {
                    await DownloadTask(config, tle, track, semaphore, fileManager, cts, false, false, false);

                    if (track.State == TrackState.Downloaded)
                        downloadedImages.Add(track);
                    else
                        allSucceeded = false;
                }
            }
            catch (OperationCanceledException)
            {
                if (userCancelled)
                {
                    Console.WriteLine("\nDownload cancelled. ");
                    if (tracks.Any(t => t.State == TrackState.Downloaded && t.DownloadPath.Length > 0))
                    {
                        Console.Write("Delete files? [Y/n] (default: Yes): ");
                        var res = Console.ReadLine().Trim().ToLower();
                        if (res == "y" || res == "")
                            OnAlbumFail(tracks, true, config);
                    }

                    if (!config.interactiveMode)
                    {
                        Console.WriteLine("Entering interactive mode");
                        config.interactiveMode = true;
                    }

                    continue;
                }
                else
                {
                    throw;
                }
            }
            finally
            {
                interceptKeys = false;
                keyPressed -= onKeyPressed;
            }

            if (allSucceeded)
                break;
        }

        return downloadedImages;
    }


    static async Task DownloadTask(Config config, TrackListEntry tle, Track track, SemaphoreSlim semaphore, FileManager organizer, CancellationTokenSource cts, bool cancelOnFail, bool removeFromSource, bool organize)
    {
        if (track.State != TrackState.Initial)
            return;

        await semaphore.WaitAsync(cts.Token);

        int tries = config.unknownErrorRetries;
        string savedFilePath = "";
        SlFile? chosenFile = null;

        ProgressBar? progress = null;

        while (tries > 0)
        {
            await WaitForLogin(config);

            cts.Token.ThrowIfCancellationRequested();

            try
            {
                progress = Printing.GetProgressBar(config);
                (savedFilePath, chosenFile) = await searchService.SearchAndDownload(track, organizer, tle, config, progress, cts);
            }
            catch (Exception ex)
            {
                WriteLineIf($"Error: {ex}", config.debugInfo);
                if (!IsConnectedAndLoggedIn())
                {
                    continue;
                }
                else if (ex is SearchAndDownloadException sdEx)
                {
                    lock (trackLists)
                    {
                        track.State = TrackState.Failed;
                        track.FailureReason = sdEx.reason;
                    }

                    if (cancelOnFail)
                    {
                        cts.Cancel();
                        throw new OperationCanceledException();
                    }
                }
                else if (ex is OperationCanceledException && cts.IsCancellationRequested)
                {
                    lock (trackLists)
                    {
                        track.State = TrackState.Failed;
                        track.FailureReason = FailureReason.Other;
                    }
                    throw;
                }
                else
                {
                    tries--;
                    continue;
                }
            }

            break;
        }

        if (tries == 0 && cancelOnFail)
        {
            lock (trackLists)
            {
                track.State = TrackState.Failed;
                track.FailureReason = FailureReason.Other;
            }

            cts.Cancel();
            throw new OperationCanceledException();
        }

        if (savedFilePath.Length > 0)
        {
            lock (trackLists)
            {
                track.State = TrackState.Downloaded;
                track.DownloadPath = savedFilePath;
            }

            if (removeFromSource && config.removeTracksFromSource)
            {
                try
                {
                    await extractor.RemoveTrackFromSource(track);
                }
                catch (Exception ex)
                {
                    WriteLine($"\n{ex.Message}\n{ex.StackTrace}\n", ConsoleColor.DarkYellow, true);
                }
            }
        }

        if (track.State == TrackState.Downloaded && organize)
        {
            lock (trackLists)
            {
                organizer?.OrganizeAudio(track, chosenFile);
            }
        }

        if (tle.config.HasOnComplete)
        {
            var savedText = progress.Line1;
            var savedPos = progress.Current;
            RefreshOrPrint(progress, savedPos, "  OnComplete:".PadRight(14) + $" {track}");
            OnComplete(tle, track, false, tle.indexEditor, tle.playlistEditor);
            RefreshOrPrint(progress, savedPos, savedText);
        }

        semaphore.Release();
    }


    static async Task<(int index, List<Track> tracks, bool retrieveFolder)> InteractiveModeAlbum(TrackListEntry tle, List<List<Track>> list, bool retrieveFolder, HashSet<string>? retrievedFolders)
    {
        int aidx = 0;

        static string interactiveModeInput()
        {
            var buffer = new StringBuilder();
            var firstKey = true;
            var cursorPos = 0;

            while (true)
            {
                var key = Console.ReadKey(true);

                // Handle special keys that should work at any time
                if (key.Key == ConsoleKey.DownArrow || key.Key == ConsoleKey.UpArrow || key.Key == ConsoleKey.Escape)
                {
                    // Clear the current line if there's any content
                    if (buffer.Length > 0)
                    {
                        Console.SetCursorPosition(0, Console.CursorTop);
                        Console.Write(new string(' ', buffer.Length + 1));
                        Console.SetCursorPosition(0, Console.CursorTop);
                    }

                    if (key.Key == ConsoleKey.DownArrow)
                        return "n";
                    else if (key.Key == ConsoleKey.UpArrow)
                        return "p";
                    else
                        return "q";
                }

                // On first keypress, check for action keys
                if (firstKey && "pnyqrs".Contains(key.KeyChar))
                    return key.KeyChar.ToString();

                // Handle editing keys
                if (key.Key == ConsoleKey.Enter)
                {
                    Console.WriteLine();
                    return buffer.ToString();
                }
                else if (key.Key == ConsoleKey.LeftArrow && cursorPos > 0)
                {
                    cursorPos--;
                    Console.SetCursorPosition(Console.CursorLeft - 1, Console.CursorTop);
                }
                else if (key.Key == ConsoleKey.RightArrow && cursorPos < buffer.Length)
                {
                    cursorPos++;
                    Console.SetCursorPosition(Console.CursorLeft + 1, Console.CursorTop);
                }
                else if (key.Key == ConsoleKey.Backspace && cursorPos > 0)
                {
                    buffer.Remove(cursorPos - 1, 1);
                    cursorPos--;

                    var restOfLine = buffer.ToString(cursorPos, buffer.Length - cursorPos);
                    Console.Write("\b" + restOfLine + " ");
                    Console.SetCursorPosition(Console.CursorLeft - (restOfLine.Length + 1), Console.CursorTop);
                }
                else if (key.Key == ConsoleKey.Delete && cursorPos < buffer.Length)
                {
                    buffer.Remove(cursorPos, 1);

                    var restOfLine = buffer.ToString(cursorPos, buffer.Length - cursorPos);
                    Console.Write(restOfLine + " ");
                    Console.SetCursorPosition(Console.CursorLeft - (restOfLine.Length + 1), Console.CursorTop);
                }
                else if (!char.IsControl(key.KeyChar))
                {
                    buffer.Insert(cursorPos, key.KeyChar);
                    cursorPos++;

                    var restOfLine = buffer.ToString(cursorPos - 1, buffer.Length - (cursorPos - 1));
                    Console.Write(restOfLine);
                    Console.SetCursorPosition(Console.CursorLeft - (restOfLine.Length - 1), Console.CursorTop);
                }

                firstKey = false;
            }
        }

        static void clearCurrentLine()
        {
            int currentLineCursor = Console.CursorTop;
            Console.SetCursorPosition(0, Console.CursorTop);
            Console.Write(new string(' ', Console.BufferWidth));
            Console.SetCursorPosition(0, currentLineCursor);
        }

        static void clearOutput(int toPos)
        {
            if (Console.IsOutputRedirected) return;

            int pos = Console.CursorTop;

            while (pos > toPos && pos > 0)
            {
                //Console.Write("\x1b[1A"); // Move up one line
                //Console.Write("\x1b[2K"); // Clear entire line
                Console.SetCursorPosition(0, pos - 1);
                clearCurrentLine();
                pos--;
            }

            Console.SetCursorPosition(0, toPos);
        }

        string retrieveAll1 = retrieveFolder ? "| [r]            " : "";
        string retrieveAll2 = retrieveFolder ? "| Load All Files " : "";
        Console.WriteLine();
        WriteLine($" [Up/p] | [Down/n] | [Enter] | [y]              {retrieveAll1}| [s]  | [Esc/q]", ConsoleColor.Green);
        WriteLine($" Prev   | Next     | Accept  | Stop Interactive {retrieveAll2}| Skip | Quit", ConsoleColor.Green);

        Console.WriteLine();
        int savedPos = Console.CursorTop;

        while (true)
        {
            var tracks = list[aidx];
            var response = tracks[0].FirstResponse;
            var username = tracks[0].FirstUsername;


            WriteLine($"[{aidx + 1} / {list.Count}]", ConsoleColor.DarkGray);

            PrintAlbum(tracks, indices: true);
            Console.WriteLine();

        Loop:
            string userInputStr = interactiveModeInput().Trim().ToLower();
            Debug.WriteLine(userInputStr);

            string command = userInputStr;
            string options = "";
            string folder = "";

            if (command.StartsWith("d:"))
            {
                options = command.Substring(2).Trim();
                command = "d";
            }

            switch (command)
            {
                case "p":
                    var prevTracks = list[aidx];
                    clearOutput(savedPos);
                    aidx = (aidx + list.Count - 1) % list.Count;
                    break;
                case "n":
                    var currTracks = list[aidx];
                    clearOutput(savedPos);
                    aidx = (aidx + 1) % list.Count;
                    break;
                case "s":
                    return (-1, new List<Track>(), false);
                case "q":
                    return (-2, new List<Track>(), false);
                case "y":
                    Console.WriteLine("Exiting interactive mode");
                    tle.config.interactiveMode = false;
                    tle.config.UpdateProfiles(tle);
                    tle.PrintLines();
                    return (aidx, tracks, true);
                case "r":
                    if (!retrieveFolder)
                        goto Loop;
                    folder = Utils.GreatestCommonDirectorySlsk(tracks.Select(t => t.FirstDownload.Filename));
                    goto case "complete_folder";
                case "cd ..":
                    if (!retrieveFolder)
                        goto Loop;
                    var currentFolder = Utils.GreatestCommonDirectorySlsk(tracks.Select(t => t.FirstDownload.Filename));
                    var parentFolder = Utils.GetDirectoryNameSlsk(currentFolder);
                    if (string.IsNullOrEmpty(parentFolder))
                    {
                        Console.WriteLine("This is the top directory");
                        goto Loop;
                    }
                    folder = parentFolder;
                    goto case "complete_folder";
                case "complete_folder":
                    if (retrieveFolder && !retrievedFolders.Contains(username + '\\' + folder))
                    {
                        Console.WriteLine("Getting all files in folder...");
                        int newFiles = await searchService.CompleteFolder(tracks, response, folder);
                        retrievedFolders.Add(username + '\\' + folder);
                        if (newFiles == 0)
                        {
                            Console.WriteLine("No more files found.");
                            goto Loop;
                        }
                        else
                        {
                            Console.WriteLine($"Found {newFiles} more files in the folder:");
                            clearOutput(savedPos);
                            break;
                        }
                    }
                    goto Loop;
                case "d":
                    if (options.Length == 0)
                        return (aidx, tracks, true);
                    try
                    {
                        var indices = options.Split(',')
                            .SelectMany(option =>
                            {
                                if (option.Contains('-'))
                                {
                                    var parts = option.Split(new char[] { '-' });
                                    int start = string.IsNullOrEmpty(parts[0]) ? 1 : int.Parse(parts[0]);
                                    int end = string.IsNullOrEmpty(parts[1]) ? tracks.Count : int.Parse(parts[1]);
                                    return Enumerable.Range(start, end - start + 1);
                                }
                                return new[] { int.Parse(option) };
                            })
                            .Distinct()
                            .ToArray();
                        return (aidx, indices.Select(i => tracks[i - 1]).ToList(), false);
                    }
                    catch
                    {
                        Console.WriteLine("Error: Invalid range");
                        goto Loop;
                    }
                case "":
                    return (aidx, tracks, true);
                default:
                    Console.WriteLine($"Error: Invalid input {userInputStr}");
                    goto Loop;
            }
        }
    }




    static async Task Update(Config startConfig)
    {
        while (true)
        {
            if (!skipUpdate)
            {
                try
                {
                    if (IsConnectedAndLoggedIn())
                    {
                        foreach (var (key, val) in searches)
                        {
                            if (val == null)
                                searches.TryRemove(key, out _);
                        }

                        foreach (var (key, val) in downloads)
                        {
                            if (val != null)
                            {
                                lock (val)
                                {
                                    if ((DateTime.Now - val.UpdateLastChangeTime()).TotalMilliseconds > val.tle.config.maxStaleTime)
                                    {
                                        val.stalled = true;
                                        val.UpdateText();

                                        try { val.cts.Cancel(); } catch { }
                                        downloads.TryRemove(key, out _);
                                    }
                                    else
                                    {
                                        val.UpdateText();
                                    }
                                }
                            }
                            else
                            {
                                downloads.TryRemove(key, out _);
                            }
                        }
                    }
                    else
                    {
                        if (!client.State.HasFlag(SoulseekClientStates.LoggedIn) 
                            && !client.State.HasFlag(SoulseekClientStates.LoggingIn) 
                            && !client.State.HasFlag(SoulseekClientStates.Connecting))
                        {
                            WriteLine($"\nDisconnected, logging in\n", ConsoleColor.DarkYellow, true);
                            try { await Login(startConfig, startConfig.useRandomLogin); }
                            catch (Exception ex)
                            {
                                string banMsg = startConfig.useRandomLogin ? "" : " (possibly a 30-minute ban caused by frequent searches)";
                                WriteLine($"{ex.Message}{banMsg}", ConsoleColor.DarkYellow, true);
                            }
                        }

                        foreach (var (key, val) in downloads)
                        {
                            if (val != null)
                                lock (val) { val.UpdateLastChangeTime(updateAllFromThisUser: false, forceChanged: true); }
                            else
                                downloads.TryRemove(key, out _);
                        }
                    }
                }
                catch (Exception ex)
                {
                    WriteLine($"\n{ex.Message}\n", ConsoleColor.DarkYellow, true);
                }

                if (interceptKeys && Console.KeyAvailable)
                {
                    var key = Console.ReadKey(intercept: true).Key;
                    keyPressed?.Invoke(null, key);
                }
            }

            await Task.Delay(updateInterval);
        }
    }


    static async Task Login(Config config, bool random = false, int tries = 3)
    {
        string user = config.username, pass = config.password;
        if (random)
        {
            var r = new Random();
            const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            user = new string(Enumerable.Repeat(chars, 10).Select(s => s[r.Next(s.Length)]).ToArray());
            pass = new string(Enumerable.Repeat(chars, 10).Select(s => s[r.Next(s.Length)]).ToArray());
        }

        WriteLine($"Login {user}");

        while (true)
        {
            try
            {
                WriteLineIf($"Connecting {user}", config.debugInfo);
                await client.ConnectAsync(user, pass);
                if (!config.noModifyShareCount)
                {
                    WriteLineIf($"Setting share count", config.debugInfo);
                    await client.SetSharedCountsAsync(20, 100);
                }
                break;
            }
            catch (Exception e)
            {
                WriteLineIf($"Exception while logging in: {e}", config.debugInfo);
                if (!(e is Soulseek.AddressException || e is System.TimeoutException) && --tries == 0)
                    throw;
            }
            await Task.Delay(500);
            WriteLineIf($"Retry login {user}", config.debugInfo);
        }

        WriteLineIf($"Logged in {user}", config.debugInfo);
    }


    static void OnComplete(TrackListEntry tle, Track track, bool isAlbumOnComplete, M3uEditor? indexEditor, M3uEditor? playlistEditor)
    {
        if (!tle.config.HasOnComplete)
            return;

        bool needUpdateIndex = false;
        int firstCommandStatus = -1;
        int prevCommandStatus = -1;
        string? prevStdout = null;
        string? prevStderr = null;
        string? firstStdout = null;
        string? firstStderr = null;

        for (int i = 0; i < tle.config.onComplete.Count; i++)
        {
            var onComplete = tle.config.onComplete[i];

            if (string.IsNullOrWhiteSpace(onComplete))
                continue;

            bool useShellExecute = false;
            bool createNoWindow = false;
            bool hasAlbumOnComplete = false;
            bool readOutput = false;
            bool useOutputToUpdateIndex = false;
            var startInfo = new ProcessStartInfo();

            while (onComplete.Length > 2)
            {
                if (onComplete[1] == ':')
                {
                    if (onComplete[0] == 's')
                    {
                        useShellExecute = true;
                        onComplete = onComplete[2..];
                    }
                    else if (onComplete[0] == 'a')
                    {
                        hasAlbumOnComplete = true;
                        onComplete = onComplete[2..];
                    }
                    else if (onComplete[0] == 'h')
                    {
                        createNoWindow = true;
                        onComplete = onComplete[2..];
                    }
                    else if (onComplete[0] == 'u')
                    {
                        useOutputToUpdateIndex = true;
                        onComplete = onComplete[2..];
                    }
                    else if (onComplete[0] == 'r')
                    {
                        readOutput = true;
                        onComplete = onComplete[2..];
                    }
                    else if (onComplete[0].IsDigit())
                    {
                        if ((int)track.State != int.Parse(onComplete[0].ToString())) return;
                        onComplete = onComplete[2..];
                    }
                    else
                    {
                        break;
                    }
                }
                else
                {
                    break;
                }
            }

            if (hasAlbumOnComplete ^ isAlbumOnComplete)
            {
                continue;
            }

            var process = new Process();

            TagLib.File? audio = null;

            if (FileManager.HasTagVariables(onComplete))
            {
                try { audio = TagLib.File.Create(track.DownloadPath); }
                catch { }
            }

            onComplete = FileManager.ReplaceVariables(onComplete, tle, audio, track.FirstDownload, track, null)
                .Replace("{exitcode}", prevCommandStatus.ToString())
                .Replace("{first-exitcode}", firstCommandStatus.ToString())
                .Replace("{stdout}", string.IsNullOrWhiteSpace(prevStdout) ? "null" : prevStdout)
                .Replace("{stderr}", string.IsNullOrWhiteSpace(prevStderr) ? "null" : prevStderr)
                .Replace("{first-stdout}", string.IsNullOrWhiteSpace(firstStdout) ? "null" : firstStdout)
                .Replace("{first-stderr}", string.IsNullOrWhiteSpace(firstStderr) ? "null" : firstStderr)
                .Trim();

            if (onComplete[0] == '"')
            {
                int e = onComplete.IndexOf('"', 1);
                if (e > 1)
                {
                    startInfo.FileName = onComplete[1..e];
                    startInfo.Arguments = onComplete.Substring(e + 1, onComplete.Length - e - 1);
                }
                else
                {
                    startInfo.FileName = onComplete.Trim('"');
                }
            }
            else
            {
                string[] parts = onComplete.Split(' ', 2);
                startInfo.FileName = parts[0];
                startInfo.Arguments = parts.Length > 1 ? parts[1] : "";
            }

            startInfo.UseShellExecute = useShellExecute;
            startInfo.CreateNoWindow = createNoWindow;

            if (useOutputToUpdateIndex || readOutput)
            {
                startInfo.UseShellExecute = false;
                startInfo.RedirectStandardOutput = true;
                startInfo.RedirectStandardError = true;
            }

            process.StartInfo = startInfo;

            WriteLineIf($"on-complete: FileName={startInfo.FileName}, Arguments={startInfo.Arguments}", tle.config.debugInfo);
            Debug.WriteLine($"on-complete: FileName={startInfo.FileName}, Arguments={startInfo.Arguments}");

            try
            {
                process.Start();

                if (startInfo.RedirectStandardOutput)
                {
                    var readStdout = process.StandardOutput.ReadToEndAsync();
                    var readStderr = process.StandardError.ReadToEndAsync();
                    Task.WaitAll(readStdout, readStderr);

                    prevStdout = readStdout.Result.Trim().Trim('"');
                    prevStderr = readStderr.Result.Trim().Trim('"');

                    if (i == 0)
                    {
                        firstStdout = prevStdout;
                        firstStderr = prevStderr;
                    }
                }

                process.WaitForExit();

                if (useOutputToUpdateIndex && !string.IsNullOrWhiteSpace(prevStdout))
                {
                    string[] parts = prevStdout.Split(';', 2);
                    if (int.TryParse(parts[0], out int newState))
                    {
                        track.State = (TrackState)newState;
                        if (parts.Length > 1)
                            track.DownloadPath = parts[1];
                        needUpdateIndex = true;
                    }
                }

                prevCommandStatus = process.ExitCode;
                if (i == 0) firstCommandStatus = process.ExitCode;
            }
            catch (Exception ex)
            {
                WriteLine($"Error while executing on-complete action with FileName={startInfo.FileName}, Arguments={startInfo.Arguments}:\n{ex}", ConsoleColor.DarkYellow);
                return;
            }
            finally
            {
                process.Close();
            }
        }

        if (needUpdateIndex)
        {
            indexEditor?.Update();
            playlistEditor?.Update();
        }
    }




    public static async Task WaitForLogin(Config config)
    {
        while (true)
        {
            WriteLineIf($"Wait for login, state: {client.State}", config.debugInfo);
            if (IsConnectedAndLoggedIn())
                break;
            await Task.Delay(1000);
        }
    }


    public static bool IsConnectedAndLoggedIn()
    {
        return client != null && client.State.HasFlag(SoulseekClientStates.Connected) && client.State.HasFlag(SoulseekClientStates.LoggedIn);
    }
}
