// Copyright (C) 2024 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Serialization;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.Helper;
using MediaBrowser.Common;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Updates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IntroSkipper;

/// <summary>
/// Intro skipper plugin. Uses audio analysis to find common sequences of audio shared between episodes.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    private readonly ILibraryManager _libraryManager;
    private readonly IItemRepository _itemRepository;
    private readonly IApplicationHost _applicationHost;
    private readonly ILogger<Plugin> _logger;
    private readonly string _introPath;
    private readonly string _creditsPath;
    private readonly string _dbPath;

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationHost">Application host.</param>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    /// <param name="serverConfiguration">Server configuration manager.</param>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="itemRepository">Item repository.</param>
    /// <param name="logger">Logger.</param>
    public Plugin(
        IApplicationHost applicationHost,
        IApplicationPaths applicationPaths,
        IXmlSerializer xmlSerializer,
        IServerConfigurationManager serverConfiguration,
        ILibraryManager libraryManager,
        IItemRepository itemRepository,
        ILogger<Plugin> logger)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;

        _applicationHost = applicationHost;
        _libraryManager = libraryManager;
        _itemRepository = itemRepository;
        _logger = logger;

        FFmpegPath = serverConfiguration.GetEncodingOptions().EncoderAppPathDisplay;

        ArgumentNullException.ThrowIfNull(applicationPaths);

        var pluginDirName = "introskipper";
        var pluginCachePath = "chromaprints";

        var introsDirectory = Path.Join(applicationPaths.DataPath, pluginDirName);
        FingerprintCachePath = Path.Join(introsDirectory, pluginCachePath);
        _introPath = Path.Join(applicationPaths.DataPath, pluginDirName, "intros.xml");
        _creditsPath = Path.Join(applicationPaths.DataPath, pluginDirName, "credits.xml");
        _dbPath = Path.Join(applicationPaths.DataPath, pluginDirName, "introskipper.db");

        // Create the base & cache directories (if needed).
        if (!Directory.Exists(FingerprintCachePath))
        {
            Directory.CreateDirectory(FingerprintCachePath);
        }

        // migrate from XMLSchema to DataContract
        XmlSerializationHelper.MigrateXML(_introPath);
        XmlSerializationHelper.MigrateXML(_creditsPath);

        var oldConfigFile = Path.Join(applicationPaths.PluginConfigurationsPath, "ConfusedPolarBear.Plugin.IntroSkipper.xml");

        if (File.Exists(oldConfigFile))
        {
            try
            {
                XmlSerializer serializer = new XmlSerializer(typeof(PluginConfiguration));
                using FileStream fileStream = new FileStream(oldConfigFile, FileMode.Open);
                var settings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit, // Disable DTD processing
                    XmlResolver = null // Disable the XmlResolver
                };

                using var reader = XmlReader.Create(fileStream, settings);
                if (serializer.Deserialize(reader) is PluginConfiguration oldConfig)
                {
                    Instance.UpdateConfiguration(oldConfig);
                    fileStream.Close();
                    File.Delete(oldConfigFile);
                }
            }
            catch (Exception ex)
            {
                // Handle exceptions, such as file not found, deserialization errors, etc.
                _logger.LogWarning("Something stupid happened: {Exception}", ex);
            }
        }

        MigrateRepoUrl(serverConfiguration);

        // Initialize database, restore timestamps if available.
        try
        {
            using var db = new IntroSkipperDbContext(_dbPath);
            db.Database.EnsureCreated();
            db.ApplyMigrations();
            if (File.Exists(_introPath) || File.Exists(_creditsPath))
            {
                RestoreTimestampsAsync(db).GetAwaiter().GetResult();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Error initializing database: {Exception}", ex);
        }

        // Inject the skip intro button code into the web interface.
        try
        {
            InjectSkipButton(applicationPaths.WebPath);
        }
        catch (Exception ex)
        {
            WarningManager.SetFlag(PluginWarning.UnableToAddSkipButton);

            _logger.LogError("Failed to add skip button to web interface. See https://github.com/intro-skipper/intro-skipper/wiki/Troubleshooting#skip-button-is-not-visible for the most common issues. Error: {Error}", ex);
        }

        FFmpegWrapper.CheckFFmpegVersion();
    }

    /// <summary>
    /// Gets the path to the database.
    /// </summary>
    public string DbPath => _dbPath;

    /// <summary>
    /// Gets the most recent media item queue.
    /// </summary>
    public ConcurrentDictionary<Guid, List<QueuedEpisode>> QueuedMediaItems { get; } = new();

    /// <summary>
    /// Gets or sets the total number of episodes in the queue.
    /// </summary>
    public int TotalQueued { get; set; }

    /// <summary>
    /// Gets or sets the number of seasons in the queue.
    /// </summary>
    public int TotalSeasons { get; set; }

    /// <summary>
    /// Gets the directory to cache fingerprints in.
    /// </summary>
    public string FingerprintCachePath { get; private set; }

    /// <summary>
    /// Gets the full path to FFmpeg.
    /// </summary>
    public string FFmpegPath { get; private set; }

    /// <inheritdoc />
    public override string Name => "Intro Skipper";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("c83d86bb-a1e0-4c35-a113-e2101cf4ee6b");

    /// <summary>
    /// Gets the plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <summary>
    /// Restore previous analysis results from disk.
    /// </summary>
    /// <param name="db">IntroSkipperDbContext.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public async Task RestoreTimestampsAsync(IntroSkipperDbContext db)
    {
        // Import intros
        if (File.Exists(_introPath))
        {
            var introList = XmlSerializationHelper.DeserializeFromXml<Segment>(_introPath);
            foreach (var intro in introList)
            {
                var dbSegment = new DbSegment(intro, AnalysisMode.Introduction);
                db.DbSegment.Add(dbSegment);
            }
        }

        // Import credits
        if (File.Exists(_creditsPath))
        {
            var creditList = XmlSerializationHelper.DeserializeFromXml<Segment>(_creditsPath);
            foreach (var credit in creditList)
            {
                var dbSegment = new DbSegment(credit, AnalysisMode.Credits);
                db.DbSegment.Add(dbSegment);
            }
        }

        await db.SaveChangesAsync().ConfigureAwait(false);

        File.Delete(_introPath);
        File.Delete(_creditsPath);
    }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html"
            },
            new PluginPageInfo
            {
                Name = "visualizer.js",
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.visualizer.js"
            },
            new PluginPageInfo
            {
                Name = "skip-intro-button.js",
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.inject.js"
            }
        ];
    }

    internal BaseItem? GetItem(Guid id)
    {
        return id != Guid.Empty ? _libraryManager.GetItemById(id) : null;
    }

    internal IReadOnlyList<Folder> GetCollectionFolders(Guid id)
    {
        var item = GetItem(id);
        return item is not null ? _libraryManager.GetCollectionFolders(item) : [];
    }

    /// <summary>
    /// Gets the full path for an item.
    /// </summary>
    /// <param name="id">Item id.</param>
    /// <returns>Full path to item.</returns>
    internal string GetItemPath(Guid id)
    {
        var item = GetItem(id);
        if (item == null)
        {
            // Handle the case where the item is not found
            _logger.LogWarning("Item with ID {Id} not found.", id);
            return string.Empty;
        }

        return item.Path;
    }

    /// <summary>
    /// Gets all chapters for this item.
    /// </summary>
    /// <param name="id">Item id.</param>
    /// <returns>List of chapters.</returns>
    internal IReadOnlyList<ChapterInfo> GetChapters(Guid id)
    {
        var item = GetItem(id);
        if (item == null)
        {
            // Handle the case where the item is not found
            _logger.LogWarning("Item with ID {Id} not found.", id);
            return [];
        }

        return _itemRepository.GetChapters(item);
    }

    internal async Task UpdateTimestamps(IReadOnlyList<Segment> newTimestamps, AnalysisMode mode)
    {
        if (newTimestamps.Count == 0)
        {
            return;
        }

        _logger.LogDebug("Starting UpdateTimestamps with {Count} segments for mode {Mode}", newTimestamps.Count, mode);

        using var db = new IntroSkipperDbContext(_dbPath);

        var segments = newTimestamps.Select(s => new DbSegment(s, mode)).ToList();

        var newItemIds = segments.Select(s => s.ItemId).ToHashSet();
        var existingIds = db.DbSegment
            .Where(s => s.Type == mode && newItemIds.Contains(s.ItemId))
            .Select(s => s.ItemId)
            .ToHashSet();

        foreach (var segment in segments)
        {
            if (existingIds.Contains(segment.ItemId))
            {
                db.DbSegment.Update(segment);
            }
            else
            {
                db.DbSegment.Add(segment);
            }
        }

        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    internal async Task ClearInvalidSegments()
    {
        using var db = new IntroSkipperDbContext(_dbPath);
        db.DbSegment.RemoveRange(db.DbSegment.Where(s => s.End == 0));
        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    internal async Task CleanTimestamps(HashSet<Guid> episodeIds)
    {
        using var db = new IntroSkipperDbContext(_dbPath);
        db.DbSegment.RemoveRange(db.DbSegment
            .Where(s => !episodeIds.Contains(s.ItemId)));
        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    internal IReadOnlyDictionary<AnalysisMode, Segment> GetSegmentsById(Guid id)
    {
        using var db = new IntroSkipperDbContext(_dbPath);
        return db.DbSegment
                .Where(s => s.ItemId == id)
                .ToDictionary(
                    s => s.Type,
                    s => new Segment
                    {
                        EpisodeId = s.ItemId,
                        Start = s.Start,
                        End = s.End
                    });
    }

    internal Segment GetSegmentByMode(Guid id, AnalysisMode mode)
    {
        using var db = new IntroSkipperDbContext(_dbPath);
        return db.DbSegment
                .Where(s => s.ItemId == id && s.Type == mode)
                .Select(s => new Segment
                {
                    EpisodeId = s.ItemId,
                    Start = s.Start,
                    End = s.End
                }).FirstOrDefault() ?? new Segment(id);
    }

    internal async Task UpdateAnalyzerActionAsync(Guid id, IReadOnlyDictionary<AnalysisMode, AnalyzerAction> analyzerActions)
    {
        using var db = new IntroSkipperDbContext(_dbPath);
        var existingEntries = await db.DbSeasonInfo
            .Where(s => s.SeasonId == id)
            .ToDictionaryAsync(s => s.Type)
            .ConfigureAwait(false);

        foreach (var (mode, action) in analyzerActions)
        {
            var dbSeasonInfo = new DbSeasonInfo(id, mode, action);
            if (existingEntries.TryGetValue(mode, out var existing))
            {
                db.Entry(existing).CurrentValues.SetValues(dbSeasonInfo);
            }
            else
            {
                db.DbSeasonInfo.Add(dbSeasonInfo);
            }
        }

        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    internal IReadOnlyDictionary<AnalysisMode, AnalyzerAction> GetAnalyzerAction(Guid id)
    {
        using var db = new IntroSkipperDbContext(_dbPath);
        return db.DbSeasonInfo.Where(s => s.SeasonId == id).ToDictionary(s => s.Type, s => s.Action);
    }

    internal AnalyzerAction GetAnalyzerAction(Guid id, AnalysisMode mode)
    {
        using var db = new IntroSkipperDbContext(_dbPath);
        return db.DbSeasonInfo.FirstOrDefault(s => s.SeasonId == id && s.Type == mode)?.Action ?? AnalyzerAction.Default;
    }

    internal async Task CleanSeasonInfoAsync()
    {
        using var db = new IntroSkipperDbContext(_dbPath);
        var obsoleteSeasons = await db.DbSeasonInfo
            .Where(s => !Instance!.QueuedMediaItems.Keys.Contains(s.SeasonId))
            .ToListAsync().ConfigureAwait(false);
        db.DbSeasonInfo.RemoveRange(obsoleteSeasons);
        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    private void MigrateRepoUrl(IServerConfigurationManager serverConfiguration)
    {
        try
        {
            List<string> oldRepos =
            [
            "https://raw.githubusercontent.com/intro-skipper/intro-skipper/master/manifest.json",
                "https://raw.githubusercontent.com/jumoog/intro-skipper/master/manifest.json",
                "https://manifest.intro-skipper.workers.dev/manifest.json"
            ];
            // Access the current server configuration
            var config = serverConfiguration.Configuration;

            // Get the list of current plugin repositories
            var pluginRepositories = config.PluginRepositories.ToList();

            // check if old plugins exits
            if (pluginRepositories.Exists(repo => repo.Url != null && oldRepos.Contains(repo.Url)))
            {
                // remove all old plugins
                pluginRepositories.RemoveAll(repo => repo.Url != null && oldRepos.Contains(repo.Url));

                // Add repository only if it does not exit and the OverideManifestUrl Option is activated
                if (!pluginRepositories.Exists(repo => repo.Url == "https://manifest.intro-skipper.org/manifest.json") && Instance!.Configuration.OverrideManifestUrl)
                {
                    // Add the new repository to the list
                    pluginRepositories.Add(new RepositoryInfo
                    {
                        Name = "intro skipper (automatically migrated by plugin)",
                        Url = "https://manifest.intro-skipper.org/manifest.json",
                        Enabled = true,
                    });
                }

                // Update the configuration with the new repository list
                config.PluginRepositories = [.. pluginRepositories];

                // Save the updated configuration
                serverConfiguration.SaveConfiguration();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while migrating repo URL");
        }
    }

    /// <summary>
    /// Inject the skip button script into the web interface.
    /// </summary>
    /// <param name="webPath">Full path to index.html.</param>
    private void InjectSkipButton(string webPath)
    {
        string pattern;
        // Inject the skip intro button code into the web interface.
        string indexPath = Path.Join(webPath, "index.html");

        // Parts of this code are based off of JellyScrub's script injection code.
        // https://github.com/nicknsy/jellyscrub/blob/main/Nick.Plugin.Jellyscrub/JellyscrubPlugin.cs#L38

        _logger.LogDebug("Reading index.html from {Path}", indexPath);
        string contents = File.ReadAllText(indexPath);

        if (!Instance!.Configuration.SkipButtonEnabled)
        {
            pattern = @"<script src=""configurationpage\?name=skip-intro-button\.js.*<\/script>";
            if (!Regex.IsMatch(contents, pattern, RegexOptions.IgnoreCase))
            {
                return;
            }

            contents = Regex.Replace(contents, pattern, string.Empty, RegexOptions.IgnoreCase);
            File.WriteAllText(indexPath, contents);
            return; // Button is disabled, so remove and abort
        }

        // change URL with every release to prevent the Browsers from caching
        string scriptTag = "<script src=\"configurationpage?name=skip-intro-button.js&release=" + GetType().Assembly.GetName().Version + "\"></script>";

        // Only inject the script tag once
        if (contents.Contains(scriptTag, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("The skip button has already been injected.");
            return;
        }

        // remove old version if necessary
        pattern = @"<script src=""configurationpage\?name=skip-intro-button\.js.*<\/script>";
        contents = Regex.Replace(contents, pattern, string.Empty, RegexOptions.IgnoreCase);

        // Inject a link to the script at the end of the <head> section.
        // A regex is used here to ensure the replacement is only done once.
        Regex headEnd = new Regex(@"</head>", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        contents = headEnd.Replace(contents, scriptTag + "</head>", 1);

        // Write the modified file contents
        File.WriteAllText(indexPath, contents);

        _logger.LogInformation("Skip button added successfully.");
    }
}
