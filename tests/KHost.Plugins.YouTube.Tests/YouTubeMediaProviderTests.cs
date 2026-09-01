using KHost.Plugins.YouTube;
using KHost.Abstractions.Models;
using KHost.Abstractions.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace KHost.Plugins.YouTube.Tests;

public class YouTubeMediaProviderTests : IDisposable
{
    // What `yt-dlp --dump-json --flat-playlist` emits: one object per line, not an array.
    private const string SearchOutput = """
        {"id":"abc123","title":"Africa (Karaoke) & Lyrics","channel":"Karaoke Channel","duration":275}
        {"id":"def456","title":"Wonderwall Karaoke","channel":"Sing King","duration":null}
        """;

    private readonly IPluginContext _plugin = Substitute.For<IPluginContext>();
    private readonly IMediaAcquisitionService _library = Substitute.For<IMediaAcquisitionService>();
    private readonly ISingerQueueService _queue = Substitute.For<ISingerQueueService>();
    private readonly IPerformanceService _performances = Substitute.For<IPerformanceService>();
    private readonly Guid _singerId = Guid.NewGuid();
    private readonly FakeRunner _runner = new() { Output = SearchOutput };
    private readonly YouTubeMediaProvider _provider;
    private readonly string _mediaDirectory = Path.Combine(Path.GetTempPath(), $"khost-yt-tests-{Guid.NewGuid():N}");

    public YouTubeMediaProviderTests()
    {
        _plugin.BindSettings<YouTubeSettings>().Returns(new YouTubeSettings { MaxResults = 10 });
        _library.MediaDirectory.Returns(_mediaDirectory);

        // Enqueuing is composed from the queue and performances now, so without a selected singer
        // every enqueue in here would no-op and the assertions would pass for the wrong reason.
        _queue.SelectedUserId.Returns(_singerId);

        _provider = new YouTubeMediaProvider(
            _plugin, _library, _queue, _performances, NullLogger<YouTubeMediaProvider>.Instance, _runner.RunAsync);
    }

    public void Dispose()
    {
        if (Directory.Exists(_mediaDirectory))
            Directory.Delete(_mediaDirectory, recursive: true);
    }

    /// <summary>
    /// What a host picks a karaoke track on. Artist is not among them on purpose: it is parsed out
    /// of the video title and can be wrong, while the channel is stated by YouTube.
    /// </summary>
    [Fact]
    public void Columns_ShowThePictureTitlePublisherAndDuration()
    {
        Assert.Equal(
            ["thumbnail", MediaResultColumn.TitleKey, "publisher", MediaResultColumn.DurationKey],
            _provider.Columns.Select(column => column.Key));

        Assert.DoesNotContain(MediaResultColumn.ArtistKey, _provider.Columns.Select(column => column.Key));

        // Headed the same as the console's own default, rather than a synonym of it.
        Assert.Equal(["", "Title", "Published by", "Duration"], _provider.Columns.Select(column => column.Header));
    }

    [Fact]
    public void Columns_ThePictureIsAThumbnail_SoTheConsoleDrawsItRatherThanPrintingTheUrl()
        => Assert.Equal(MediaResultColumnKind.Thumbnail, _provider.Columns[0].Kind);

    /// <summary>The title and the length are what is left when the panel runs out of room.</summary>
    [Fact]
    public void Columns_ThePictureAndPublisher_MayBeDroppedWhenNarrow()
    {
        Assert.False(_provider.Columns[0].Essential);
        Assert.False(_provider.Columns[2].Essential);
        Assert.True(_provider.Columns[1].Essential);
        Assert.True(_provider.Columns[3].Essential);
    }

    [Fact]
    public async Task SearchAsync_CarriesThePublishingChannel()
    {
        _runner.Output = """
            {"id":"abc123","title":"Africa (Karaoke)","channel":"Sing King","duration":275,"channel_is_verified":true}
            """;

        var results = await _provider.SearchAsync("africa karaoke");

        // The channel was already being read and then thrown away in Notes, which the console
        // never rendered — this is the information the whole column set exists to surface.
        Assert.Equal("Sing King \u2713", results[0].Fields["publisher"]);
    }

    [Fact]
    public async Task SearchAsync_AnUnverifiedChannel_GetsNoTick()
    {
        _runner.Output = """
            {"id":"abc123","title":"Africa (Karaoke)","channel":"Some Bloke","duration":275}
            """;

        var results = await _provider.SearchAsync("africa karaoke");

        Assert.Equal("Some Bloke", results[0].Fields["publisher"]);
    }

    [Fact]
    public async Task SearchAsync_CarriesTheThumbnail()
    {
        _runner.Output = """
            {"id":"abc123","title":"Africa","channel":"Sing King","duration":275,"thumbnails":[{"url":"https://i.ytimg.com/vi/abc123/hq720.jpg","width":360}]}
            """;

        var results = await _provider.SearchAsync("africa karaoke");

        Assert.Equal("https://i.ytimg.com/vi/abc123/hq720.jpg", results[0].Fields["thumbnail"]);
    }

    [Fact]
    public async Task SearchAsync_NoThumbnailInTheResult_LeavesTheFieldEmpty()
    {
        var results = await _provider.SearchAsync("africa karaoke");

        Assert.Equal(string.Empty, results[0].Fields["thumbnail"]);
    }

    [Fact]
    public async Task SearchAsync_MapsResults()
    {
        var results = await _provider.SearchAsync("africa karaoke");

        Assert.Equal(2, results.Count);
        // Verbatim, decoration included: a host choosing between karaoke uploads is choosing on
        // the words YouTube shows, and the parse would strip the ones that tell them apart.
        Assert.Equal("Africa (Karaoke) & Lyrics", results[0].Title);
        Assert.Equal("abc123", results[0].ForeignKey);
        Assert.Equal("YouTube", results[0].Source);
        Assert.Equal(TimeSpan.FromSeconds(275), results[0].Duration);
    }

    [Fact]
    public async Task SearchAsync_CarriesTheParsedTitleForTheImport()
    {
        var results = await _provider.SearchAsync("africa karaoke");

        // The row shows the raw title, so the parse has to travel beside it — re-running it one
        // row at a time on import would lose the orientation ParseAll settled across the set.
        Assert.Equal("Africa", results[0].Fields["cleanTitle"]);
    }

    [Fact]
    public async Task SearchAsync_NoArtistCarrierInTitle_LeavesArtistEmpty()
    {
        var results = await _provider.SearchAsync("africa karaoke");

        // Neither fixture title names a performer, so the parse has nothing to put there.
        Assert.Equal(string.Empty, results[0].Artist);
    }

    [Fact]
    public async Task SearchAsync_ParsedTitleDiffersFromRaw_KeepsTheRawTitleInNotesBesideTheChannel()
    {
        var results = await _provider.SearchAsync("africa karaoke");

        // The library row keeps the parsed title, so the note is the only place the words the
        // video actually carried survive the import.
        Assert.Equal(
            "Karaoke Channel — “Africa (Karaoke) & Lyrics” — https://www.youtube.com/watch?v=abc123",
            results[0].Notes);
    }

    [Fact]
    public async Task SearchAsync_ParsedTitleMatchesRaw_LeavesTheRawTitleOutOfNotes()
    {
        _runner.Output = """{"id":"clean1","title":"Africa","channel":"Karaoke Channel","duration":100}""";

        var results = await _provider.SearchAsync("africa");

        // Nothing was stripped, so repeating the title back would just be noise beside the link.
        Assert.Equal(
            "Karaoke Channel — https://www.youtube.com/watch?v=clean1",
            Assert.Single(results).Notes);
    }

    [Fact]
    public async Task SearchAsync_ParsedTitleDiffersFromRaw_NoChannel_OmitsTheLeadingDash()
    {
        _runner.Output = """{"id":"nocnl","title":"Wonderwall Karaoke","channel":"","duration":null}""";

        var results = await _provider.SearchAsync("wonderwall");

        // An empty channel must not leave a dangling " — " at the front of the note.
        Assert.Equal(
            "“Wonderwall Karaoke” — https://www.youtube.com/watch?v=nocnl",
            Assert.Single(results).Notes);
    }

    [Fact]
    public async Task SearchAsync_NotesCarryTheWatchUrl()
    {
        var results = await _provider.SearchAsync("africa karaoke");

        // The library row is a local file path once imported; without this the host has no way
        // back to what it was downloaded from.
        Assert.All(
            results,
            result => Assert.Contains($"https://www.youtube.com/watch?v={result.ForeignKey}", result.Notes));
    }

    [Fact]
    public async Task SearchAsync_NullDuration_LeavesItNull()
    {
        var results = await _provider.SearchAsync("africa karaoke");

        Assert.Null(results[1].Duration);
    }

    [Fact]
    public async Task SearchAsync_ZeroDuration_LeavesItNull()
    {
        // A live stream reports 0 rather than omitting the field, and zero renders as "0:00" —
        // a definite-looking length for a song whose length is not known.
        _runner.Output = """{"id":"live1","title":"Karaoke live stream","channel":"c","duration":0}""";

        var results = await _provider.SearchAsync("karaoke live");

        Assert.Null(Assert.Single(results).Duration);
    }

    [Fact]
    public async Task SearchAsync_AsksForTheConfiguredNumberOfResults()
    {
        await _provider.SearchAsync("rick & morty");

        var arguments = _runner.Calls.Single();

        Assert.Equal("ytsearch10:rick & morty Karaoke", arguments[0]);
        Assert.Contains("--dump-json", arguments);

        // Without it yt-dlp resolves every hit in turn, which is a page load per row.
        Assert.Contains("--flat-playlist", arguments);
    }

    [Fact]
    public async Task SearchAsync_PassesTheQueryAsOneArgument_SoQuotesCannotSplitIt()
    {
        await _provider.SearchAsync("don't stop \"believin\"");

        // One argument, verbatim: joining these into a command line is how a title with a quote
        // turns into extra arguments.
        Assert.Equal("ytsearch10:don't stop \"believin\" Karaoke", _runner.Calls.Single()[0]);
    }

    [Fact]
    public async Task SearchAsync_AsksYouTubeForTheKaraokeCut()
    {
        await _provider.SearchAsync("wonderwall");

        // A host wants the backing track, not the record. Without the word YouTube answers with the
        // original every time and the useful results are pages down.
        Assert.Equal("ytsearch10:wonderwall Karaoke", _runner.Calls.Single()[0]);
    }

    [Fact]
    public async Task SearchAsync_PageSizeOverridesConfiguredMax()
    {
        await _provider.SearchAsync("africa", pageSize: 5);

        Assert.Equal("ytsearch5:africa Karaoke", _runner.Calls.Single()[0]);
    }

    [Fact]
    public async Task SearchAsync_ClampsAnAbsurdPageSize()
    {
        await _provider.SearchAsync("africa", pageSize: 5000);

        Assert.Equal("ytsearch50:africa Karaoke", _runner.Calls.Single()[0]);
    }

    [Fact]
    public async Task SearchAsync_SecondPage_ReturnsEmptyWithoutRunningAnything()
    {
        // ytsearch takes a count, not an offset, so page two would re-run page one's search.
        var results = await _provider.SearchAsync("africa", pageNumber: 2);

        Assert.Empty(results);
        Assert.Empty(_runner.Calls);
    }

    [Fact]
    public async Task SearchAsync_BlankQuery_ReturnsEmptyWithoutRunningAnything()
    {
        var results = await _provider.SearchAsync("   ");

        Assert.Empty(results);
        Assert.Empty(_runner.Calls);
    }

    [Fact]
    public async Task SearchAsync_NoMatches_ReturnsEmpty()
    {
        _runner.Output = "";

        Assert.Empty(await _provider.SearchAsync("zzzzz"));
    }

    [Fact]
    public async Task SearchAsync_SkipsARowItCannotUse()
    {
        // A truncated line and a row with no id: neither can be enqueued or opened, and neither is
        // worth failing the whole search over.
        _runner.Output = """
            {"id":"abc123","title":"Fine","channel":"c","duration":10}
            {"title":"No id here","duration":10}
            {"id":"trunc","title":"Half a li
            """;

        var results = await _provider.SearchAsync("africa");

        Assert.Equal("abc123", Assert.Single(results).ForeignKey);
    }

    [Fact]
    public void Actions_ExposesOneTopLevelEnqueueActionWithOpenOnYouTubeAsASubAction()
    {
        var action = Assert.Single(_provider.Actions);

        Assert.Equal("Enqueue", action.DisplayName);
        var subAction = Assert.Single(action.SubActions);
        Assert.Equal("Open on YouTube", subAction.DisplayName);
    }

    [Fact]
    public async Task DownloadAndEnqueueAsync_FileDoesNotExist_BeginsThenEnqueuesThenDownloadsThenCompletes()
    {
        var entity = BuildEntity("dl-happy", "Africa", "Toto", TimeSpan.FromMinutes(3), "some notes");
        var destination = TrackDestinationFor(entity);

        Assert.False(File.Exists(destination));

        var order = new List<string>();
        var mediaId = Guid.NewGuid();
        StubBegin(mediaId);
        _library.When(l => l.BeginImportAsync(Arg.Any<MediaImportRequest>())).Do(_ => order.Add("Begin"));
        _performances.When(p => p.CreateAndEnqueueAsync(Arg.Is<Performance>(x => x.MediaId == mediaId)))
            .Do(_ => order.Add("Enqueue"));
        _library.When(l => l.CompleteImportAsync(mediaId)).Do(_ => order.Add("Complete"));

        // yt-dlp writes the destination as a side effect of running; the fake mirrors that so the
        // post-download existence check finds something.
        _runner.OnRun = _ =>
        {
            order.Add("Run");
            File.WriteAllBytes(destination, [1]);
        };

        await Enqueue(entity);

        var arguments = _runner.Calls.Single();
        Assert.Equal($"https://www.youtube.com/watch?v={entity.ForeignKey}", arguments[0]);

        var destinationIndex = arguments.ToList().IndexOf("-o");
        Assert.True(destinationIndex >= 0);
        Assert.Equal(destination, arguments[destinationIndex + 1]);

        await _library.Received(1).BeginImportAsync(Arg.Is<MediaImportRequest>(r =>
            r.FilePath == destination
            && r.Title == entity.Title
            && r.Artist == entity.Artist
            && r.Duration == entity.Duration
            && r.Notes == entity.Notes));
        await _performances.Received(1).CreateAndEnqueueAsync(
            Arg.Is<Performance>(p => p.MediaId == mediaId && p.SingerId == _singerId));
        await _library.Received(1).CompleteImportAsync(mediaId);
        await _library.DidNotReceive().ImportAsync(Arg.Any<MediaImportRequest>());

        // The queue must show the Downloading row the moment the host clicks, not after the
        // (possibly minutes-long) download finishes.
        Assert.Equal(["Begin", "Enqueue", "Run", "Complete"], order);
    }

    [Fact]
    public async Task DownloadAndEnqueueAsync_FileDoesNotExist_SetsSourceToTheProvidersDisplayName()
    {
        var entity = BuildEntity("dl-source", "Africa", "Toto", TimeSpan.FromMinutes(3), "notes");
        var destination = TrackDestinationFor(entity);
        var mediaId = Guid.NewGuid();
        StubBegin(mediaId);
        _runner.OnRun = _ => File.WriteAllBytes(destination, [1]);

        await Enqueue(entity);

        await _library.Received(1).BeginImportAsync(Arg.Is<MediaImportRequest>(r => r.Source == "YouTube"));
    }

    [Fact]
    public async Task DownloadAndEnqueueAsync_CleanTitleCarried_ImportsTheParsedTitleNotTheRawOne()
    {
        var entity = BuildEntity(
            "dl-clean", "Africa (Karaoke) & Lyrics", "Toto", TimeSpan.FromMinutes(3), "notes") with
        {
            Fields = new Dictionary<string, string> { ["cleanTitle"] = "Africa" },
        };
        var destination = TrackDestinationFor(entity);
        StubBegin(Guid.NewGuid());
        _runner.OnRun = _ => File.WriteAllBytes(destination, [1]);

        await Enqueue(entity);

        // The list is allowed to be verbatim; the library is not, or every row reads
        // "(Karaoke) & Lyrics" forever.
        await _library.Received(1).BeginImportAsync(Arg.Is<MediaImportRequest>(r => r.Title == "Africa"));
    }

    [Fact]
    public async Task DownloadAndEnqueueAsync_NoCleanTitleCarried_FallsBackToTheRowsOwnTitle()
    {
        // A row from an older search, or any caller that did not go through SearchAsync.
        var entity = BuildEntity("dl-noclean", "Africa (Karaoke)", "Toto", TimeSpan.FromMinutes(3), "notes");
        var destination = TrackDestinationFor(entity);
        StubBegin(Guid.NewGuid());
        _runner.OnRun = _ => File.WriteAllBytes(destination, [1]);

        await Enqueue(entity);

        await _library.Received(1).BeginImportAsync(
            Arg.Is<MediaImportRequest>(r => r.Title == "Africa (Karaoke)"));
    }

    [Fact]
    public async Task DownloadAndEnqueueAsync_BlankCleanTitleCarried_FallsBackToTheRowsOwnTitle()
    {
        var entity = BuildEntity("dl-blankclean", "Africa (Karaoke)", "Toto", TimeSpan.FromMinutes(3), "n") with
        {
            Fields = new Dictionary<string, string> { ["cleanTitle"] = "" },
        };
        var destination = TrackDestinationFor(entity);
        StubBegin(Guid.NewGuid());
        _runner.OnRun = _ => File.WriteAllBytes(destination, [1]);

        await Enqueue(entity);

        // A parse that stripped everything must not leave the library row with a blank name.
        await _library.Received(1).BeginImportAsync(
            Arg.Is<MediaImportRequest>(r => r.Title == "Africa (Karaoke)"));
    }

    [Fact]
    public async Task DownloadAndEnqueueAsync_FileAlreadyExists_SetsSourceToTheProvidersDisplayName()
    {
        var entity = BuildEntity("dl-source-exists", "Wonderwall", "Oasis", TimeSpan.FromMinutes(4), "n");
        var destination = TrackDestinationFor(entity);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllBytes(destination, [1]);
        _library.ImportAsync(Arg.Any<MediaImportRequest>()).Returns(Guid.NewGuid());

        await Enqueue(entity);

        await _library.Received(1).ImportAsync(Arg.Is<MediaImportRequest>(r => r.Source == "YouTube"));
    }

    [Fact]
    public async Task DownloadAndEnqueueAsync_DownloadArguments_IncludeNewlineSoProgressLinesAreEmittedOnePerLine()
    {
        var entity = BuildEntity("dl-newline", "Song", "", null, "");
        var destination = TrackDestinationFor(entity);
        StubBegin(Guid.NewGuid());
        _runner.OnRun = _ => File.WriteAllBytes(destination, [1]);

        await Enqueue(entity);

        Assert.Contains("--newline", _runner.Calls.Single());
    }

    [Fact]
    public async Task DownloadAndEnqueueAsync_RunnerEmitsProgressLines_ReportsProgressWithASaneFraction()
    {
        var entity = BuildEntity("dl-progress", "Song", "", null, "");
        var destination = TrackDestinationFor(entity);
        var mediaId = Guid.NewGuid();
        StubBegin(mediaId);

        _runner.LinesToStream =
        [
            "[download] Destination: video.f137.mp4",
            "[download]  50.0% of 10.00MiB at 1.00MiB/s ETA 00:05",
            "[download] 100% of 10.00MiB in 00:10",
        ];
        _runner.OnRun = _ => File.WriteAllBytes(destination, [1]);

        await Enqueue(entity);

        await _library.Received().ReportDownloadProgressAsync(
            mediaId, Arg.Is<double>(f => f >= 0.0 && f <= 1.0));
        await _library.Received(1).ReportDownloadProgressAsync(mediaId, 0.25);
        await _library.Received(1).ReportDownloadProgressAsync(mediaId, 0.5);
    }

    [Fact]
    public async Task DownloadAndEnqueueAsync_ReportDownloadProgressAsyncThrows_DoesNotFailTheDownload()
    {
        var entity = BuildEntity("dl-progress-throws", "Song", "", null, "");
        var destination = TrackDestinationFor(entity);
        var mediaId = Guid.NewGuid();
        StubBegin(mediaId);
        _library.ReportDownloadProgressAsync(Arg.Any<Guid>(), Arg.Any<double>())
            .Returns<Task>(_ => throw new InvalidOperationException("host rejected it"));

        _runner.LinesToStream = ["[download]  10.0% of 10.00MiB at 1.00MiB/s ETA 00:05"];
        _runner.OnRun = _ => File.WriteAllBytes(destination, [1]);

        await Enqueue(entity);

        await _library.Received(1).CompleteImportAsync(mediaId);
    }

    [Fact]
    public async Task DownloadAndEnqueueAsync_FileAlreadyExists_SkipsTheDownloadButStillImportsAndEnqueues()
    {
        var entity = BuildEntity("dl-exists", "Wonderwall", "Oasis", TimeSpan.FromMinutes(4), "n");
        var destination = TrackDestinationFor(entity);

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllBytes(destination, [1]);

        var mediaId = Guid.NewGuid();
        _library.ImportAsync(Arg.Any<MediaImportRequest>()).Returns(mediaId);

        await Enqueue(entity);

        // A re-click must not re-fetch a video already sitting in the library's cache.
        Assert.Empty(_runner.Calls);
        await _library.Received(1).ImportAsync(Arg.Any<MediaImportRequest>());
        await _performances.Received(1).CreateAndEnqueueAsync(
            Arg.Is<Performance>(p => p.MediaId == mediaId && p.SingerId == _singerId));
        await _library.DidNotReceive().BeginImportAsync(Arg.Any<MediaImportRequest>());
        await _library.DidNotReceive().CompleteImportAsync(Arg.Any<Guid>());
    }

    /// <summary>
    /// The enqueue rule used to live behind the host's plugin facade; composing it here means the
    /// no-singer case is this provider's to get right. The import still stands — the file is in the
    /// library either way, and only the queue entry needed a singer.
    /// </summary>
    [Fact]
    public async Task DownloadAndEnqueueAsync_NoSingerSelected_ImportsWithoutEnqueuing()
    {
        var entity = BuildEntity("dl-nosinger", "Creep", "Radiohead", TimeSpan.FromMinutes(4), "n");
        var destination = TrackDestinationFor(entity);

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllBytes(destination, [1]);

        _queue.SelectedUserId.Returns((Guid?)null);
        _library.ImportAsync(Arg.Any<MediaImportRequest>()).Returns(Guid.NewGuid());

        await Enqueue(entity);

        await _library.Received(1).ImportAsync(Arg.Any<MediaImportRequest>());
        await _performances.DidNotReceive().CreateAndEnqueueAsync(Arg.Any<Performance>());
    }

    [Fact]
    public async Task DownloadAndEnqueueAsync_DownloadProducesNoFile_FailsImportThenThrows()
    {
        var entity = BuildEntity("dl-fail", "Missing", "", null, "");
        var mediaId = Guid.NewGuid();
        StubBegin(mediaId);

        // _runner.OnRun left unset: the fake runs "successfully" but never writes the file, the
        // same shape a real yt-dlp failure leaves behind.
        await Assert.ThrowsAsync<InvalidOperationException>(() => Enqueue(entity));

        await _library.Received(1).FailImportAsync(mediaId);
        await _library.DidNotReceive().CompleteImportAsync(Arg.Any<Guid>());
    }

    [Fact]
    public async Task DownloadAndEnqueueAsync_RunnerThrows_FailsImportThenPropagates()
    {
        var entity = BuildEntity("dl-throw", "Broken", "", null, "");
        var mediaId = Guid.NewGuid();
        StubBegin(mediaId);
        _runner.ThrowOnRun = new InvalidOperationException("yt-dlp exploded");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => Enqueue(entity));

        Assert.Equal("yt-dlp exploded", exception.Message);
        await _library.Received(1).FailImportAsync(mediaId);
        await _library.DidNotReceive().CompleteImportAsync(Arg.Any<Guid>());
    }

    [Fact]
    public async Task DownloadAndEnqueueAsync_SecondCallWhileFirstStillDownloading_ReturnsWithoutDuplicateRunOrEnqueue()
    {
        var entity = BuildEntity("dl-guard", "Slow", "", null, "");
        var mediaId = Guid.NewGuid();
        StubBegin(mediaId);

        // Blocks the fake runner mid-"download" so a second call lands while the first is still
        // in flight. No file is ever written, so both attempts follow the non-exists branch.
        _runner.Gate = new TaskCompletionSource<string>();

        var firstCall = Enqueue(entity);

        var secondCall = Enqueue(entity);
        await secondCall;

        Assert.Single(_runner.Calls);
        await _performances.Received(1).CreateAndEnqueueAsync(
            Arg.Is<Performance>(p => p.MediaId == mediaId && p.SingerId == _singerId));
        await _library.Received(1).BeginImportAsync(Arg.Any<MediaImportRequest>());

        _runner.Gate.SetResult("");
        await Assert.ThrowsAsync<InvalidOperationException>(() => firstCall);

        await _library.Received(1).FailImportAsync(mediaId);

        // The finally block must have removed the ForeignKey from the in-flight set, so a third
        // call after the first settles is allowed to start its own run.
        _runner.Gate = null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Enqueue(entity));
        Assert.Equal(2, _runner.Calls.Count);
    }

    [Fact]
    public async Task DownloadAndEnqueueAsync_PassesTheTicketsCancellationTokenToTheRunner()
    {
        var entity = BuildEntity("dl-token", "Song", "", null, "");
        var destination = TrackDestinationFor(entity);
        using var cts = new CancellationTokenSource();
        StubBegin(Guid.NewGuid(), cts.Token);
        _runner.OnRun = _ => File.WriteAllBytes(destination, [1]);

        await Enqueue(entity);

        Assert.Equal(cts.Token, _runner.LastToken);
    }

    [Fact]
    public async Task DownloadAndEnqueueAsync_CancelledRun_DeletesArtifactsAndDiscardsImport()
    {
        var entity = BuildEntity("dl-cancel", "Interrupted", "", null, "");
        var destination = TrackDestinationFor(entity);
        var directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);

        // What a cancelled bv+ba download leaves behind: yt-dlp's own intermediates and a
        // per-stream fragment file, pre-existing before the run — plus an unrelated video that
        // must survive the sweep because it does not share this ForeignKey's prefix. Production
        // code exits early via the "already downloaded" branch if the destination itself exists
        // beforehand, so the destination is written by the (cancelled) run, like the happy path.
        File.WriteAllBytes(destination + ".part", [1]);
        File.WriteAllBytes(destination + ".ytdl", [1]);
        var fragment = Path.Combine(directory, $"{entity.ForeignKey}.f137.mp4");
        File.WriteAllBytes(fragment, [1]);
        var unrelated = Path.Combine(directory, "someone-elses-video.mp4");
        File.WriteAllBytes(unrelated, [1]);

        var mediaId = Guid.NewGuid();
        StubBegin(mediaId);
        _runner.OnRun = _ => File.WriteAllBytes(destination, [1]);
        _runner.ThrowOnRun = new OperationCanceledException();

        await Assert.ThrowsAsync<OperationCanceledException>(() => Enqueue(entity));

        Assert.False(File.Exists(destination));
        Assert.False(File.Exists(destination + ".part"));
        Assert.False(File.Exists(destination + ".ytdl"));
        Assert.False(File.Exists(fragment));
        Assert.True(File.Exists(unrelated));

        await _library.Received(1).DiscardImportAsync(mediaId);
        await _library.DidNotReceive().FailImportAsync(Arg.Any<Guid>());
        await _library.DidNotReceive().CompleteImportAsync(Arg.Any<Guid>());

        // The finally block must still have released the ForeignKey guard on the cancel path.
        _runner.ThrowOnRun = null;
        _runner.OnRun = _ => File.WriteAllBytes(destination, [1]);
        await Enqueue(entity);
        Assert.Equal(2, _runner.Calls.Count);
    }

    [Fact]
    public async Task DownloadAndEnqueueAsync_CancelledRun_DestinationSurvivesCleanup_FailsImportInstead()
    {
        var entity = BuildEntity("dl-cancel-stuck", "Stuck", "", null, "");
        var destination = TrackDestinationFor(entity);
        var directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);

        // A directory sitting at the destination path: File.Exists reports it as absent (so the
        // early "already downloaded" branch is not taken), but File.Delete on a directory throws,
        // so the cleanup sweep cannot make it go away — exactly the "file remains" case.
        Directory.CreateDirectory(destination);
        File.WriteAllBytes(Path.Combine(destination, "stray"), [1]);
        Assert.False(File.Exists(destination));

        var mediaId = Guid.NewGuid();
        StubBegin(mediaId);
        _runner.ThrowOnRun = new OperationCanceledException();

        await Assert.ThrowsAsync<OperationCanceledException>(() => Enqueue(entity));

        Assert.True(Directory.Exists(destination));
        await _library.Received(1).FailImportAsync(mediaId);
        await _library.DidNotReceive().DiscardImportAsync(Arg.Any<Guid>());
        await _library.DidNotReceive().CompleteImportAsync(Arg.Any<Guid>());
    }

    private void StubBegin(Guid mediaId, CancellationToken token = default)
        => _library.BeginImportAsync(Arg.Any<MediaImportRequest>())
            .Returns(new ImportTicket { MediaId = mediaId, Cancellation = token });

    private Task Enqueue(MediaSearchEntity entity) => _provider.Actions.Single().PerformAsync(entity);

    private static MediaSearchEntity BuildEntity(
        string foreignKey, string title, string artist, TimeSpan? duration, string notes)
        => new()
        {
            SourceDisplayName = "YouTube",
            Source = "YouTube",
            ForeignKey = foreignKey,
            Title = title,
            Artist = artist,
            Duration = duration,
            Notes = notes,
        };

    /// <summary>Computes the destination the same way production code does.</summary>
    private string TrackDestinationFor(MediaSearchEntity entity)
        => Path.Combine(_mediaDirectory, "youtube", $"{entity.ForeignKey}.mp4");

    private sealed class FakeRunner
    {
        public string Output { get; set; } = "";
        public List<IReadOnlyList<string>> Calls { get; } = [];
        public Action<IReadOnlyList<string>>? OnRun { get; set; }
        public Exception? ThrowOnRun { get; set; }
        public CancellationToken? LastToken { get; private set; }

        /// <summary>Set to make a call hang until the test releases it, to simulate an in-flight download.</summary>
        public TaskCompletionSource<string>? Gate { get; set; }

        /// <summary>Lines to hand the caller's onLine callback, simulating streamed yt-dlp output.</summary>
        public IReadOnlyList<string> LinesToStream { get; set; } = [];

        public async Task<string> RunAsync(
            IReadOnlyList<string> arguments, CancellationToken cancellationToken, Action<string>? onLine = null)
        {
            Calls.Add(arguments);
            LastToken = cancellationToken;
            OnRun?.Invoke(arguments);

            foreach (var line in LinesToStream)
                onLine?.Invoke(line);

            if (Gate is { } gate)
                await gate.Task;

            if (ThrowOnRun is { } exception)
                throw exception;

            return Output;
        }
    }
}
