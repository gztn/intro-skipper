// Copyright (C) 2024 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Analyzers;

/// <summary>
/// Media file analyzer used to detect end credits that consist of text overlaid on a black background.
/// Bisects the end of the video file to perform an efficient search.
/// </summary>
public class BlackFrameAnalyzer(ILogger<BlackFrameAnalyzer> logger) : IMediaFileAnalyzer
{
    private static readonly PluginConfiguration _config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
    private readonly TimeSpan _maximumError = new(0, 0, 4);
    private readonly ILogger<BlackFrameAnalyzer> _logger = logger;
    private readonly int _minimumCreditsDuration = _config.MinimumCreditsDuration;
    private readonly int _maximumCreditsDuration = _config.MaximumCreditsDuration;
    private readonly int _maximumMovieCreditsDuration = _config.MaximumMovieCreditsDuration;
    private readonly int _blackFrameMinimumPercentage = _config.BlackFrameMinimumPercentage;

    /// <inheritdoc />
    public async Task<IReadOnlyList<QueuedEpisode>> AnalyzeMediaFiles(
        IReadOnlyList<QueuedEpisode> analysisQueue,
        AnalysisMode mode,
        CancellationToken cancellationToken)
    {
        if (mode != AnalysisMode.Credits)
        {
            throw new NotImplementedException("mode must equal Credits");
        }

        var creditTimes = new List<Segment>();

        bool isFirstEpisode = true;

        double searchStart = _minimumCreditsDuration;

        var searchDistance = 2 * _minimumCreditsDuration;

        foreach (var episode in analysisQueue.Where(e => !e.GetAnalyzed(mode)))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var creditDuration = episode.IsMovie ? _maximumMovieCreditsDuration : _maximumCreditsDuration;

            var chapters = Plugin.Instance!.GetChapters(episode.EpisodeId);
            var lastSuitableChapter = chapters.LastOrDefault(c =>
                {
                    var start = TimeSpan.FromTicks(c.StartPositionTicks).TotalSeconds;
                    return start >= _minimumCreditsDuration && start <= creditDuration;
                });

            if (lastSuitableChapter is not null)
            {
                searchStart = TimeSpan.FromTicks(lastSuitableChapter.StartPositionTicks).TotalSeconds;
                isFirstEpisode = false;
            }

            if (isFirstEpisode)
            {
                var scanTime = episode.Duration - searchStart;
                var tr = new TimeRange(scanTime - 0.5, scanTime); // Short search range since accuracy isn't important here.

                var frames = FFmpegWrapper.DetectBlackFrames(episode, tr, _blackFrameMinimumPercentage);

                while (frames.Length > 0) // While black frames are found increase searchStart
                {
                    searchStart += searchDistance;

                    scanTime = episode.Duration - searchStart;
                    tr = new TimeRange(scanTime - 0.5, scanTime);

                    frames = FFmpegWrapper.DetectBlackFrames(episode, tr, _blackFrameMinimumPercentage);

                    if (searchStart > creditDuration)
                    {
                        searchStart = creditDuration;
                        break;
                    }
                }

                if (searchStart == _minimumCreditsDuration) // Skip if no black frames were found
                {
                    continue;
                }

                isFirstEpisode = false;
            }

            var credit = AnalyzeMediaFile(
                episode,
                searchStart,
                searchDistance,
                _blackFrameMinimumPercentage);

            if (credit is null || !credit.Valid)
            {
                // If no credits were found, reset the first-episode search logic for the next episode in the sequence.
                searchStart = _minimumCreditsDuration;
                isFirstEpisode = true;
                continue;
            }

            searchStart = episode.Duration - credit.Start + (0.5 * searchDistance);

            creditTimes.Add(credit);
            episode.SetAnalyzed(mode, true);
        }

        var analyzerHelper = new AnalyzerHelper(_logger);
        var adjustedCreditTimes = analyzerHelper.AdjustIntroTimes(analysisQueue, creditTimes, mode);

        await Plugin.Instance!.UpdateTimestamps(adjustedCreditTimes, mode).ConfigureAwait(false);

        return analysisQueue;
    }

    /// <summary>
    /// Analyzes an individual media file. Only public because of unit tests.
    /// </summary>
    /// <param name="episode">Media file to analyze.</param>
    /// <param name="searchStart">Search Start Piont.</param>
    /// <param name="searchDistance">Search Distance.</param>
    /// <param name="minimum">Percentage of the frame that must be black.</param>
    /// <returns>Credits timestamp.</returns>
    public Segment? AnalyzeMediaFile(QueuedEpisode episode, double searchStart, int searchDistance, int minimum)
    {
        // Start by analyzing the last N minutes of the file.
        var upperLimit = searchStart;
        var lowerLimit = Math.Max(searchStart - searchDistance, _minimumCreditsDuration);
        var start = TimeSpan.FromSeconds(upperLimit);
        var end = TimeSpan.FromSeconds(lowerLimit);
        var firstFrameTime = 0.0;

        var creditDuration = episode.IsMovie ? _maximumMovieCreditsDuration : _maximumCreditsDuration;

        // Continue bisecting the end of the file until the range that contains the first black
        // frame is smaller than the maximum permitted error.
        while (start - end > _maximumError)
        {
            // Analyze the middle two seconds from the current bisected range
            var midpoint = (start + end) / 2;
            var scanTime = episode.Duration - midpoint.TotalSeconds;
            var tr = new TimeRange(scanTime, scanTime + 2);

            _logger.LogTrace(
                "{Episode}, dur {Duration}, bisect [{BStart}, {BEnd}], time [{Start}, {End}]",
                episode.Name,
                episode.Duration,
                start,
                end,
                tr.Start,
                tr.End);

            var frames = FFmpegWrapper.DetectBlackFrames(episode, tr, minimum);
            _logger.LogTrace(
                "{Episode} at {Start} has {Count} black frames",
                episode.Name,
                tr.Start,
                frames.Length);

            if (frames.Length == 0)
            {
                // Since no black frames were found, slide the range closer to the end
                start = midpoint - TimeSpan.FromSeconds(2);

                if (midpoint - TimeSpan.FromSeconds(lowerLimit) < _maximumError)
                {
                    lowerLimit = Math.Max(lowerLimit - (0.5 * searchDistance), _minimumCreditsDuration);

                    // Reset end for a new search with the increased duration
                    end = TimeSpan.FromSeconds(lowerLimit);
                }
            }
            else
            {
                // Some black frames were found, slide the range closer to the start
                end = midpoint;
                firstFrameTime = frames[0].Time + scanTime;

                if (TimeSpan.FromSeconds(upperLimit) - midpoint < _maximumError)
                {
                    upperLimit = Math.Min(upperLimit + (0.5 * searchDistance), creditDuration);

                    // Reset start for a new search with the increased duration
                    start = TimeSpan.FromSeconds(upperLimit);
                }
            }
        }

        if (firstFrameTime > 0)
        {
            return new(episode.EpisodeId, new TimeRange(firstFrameTime, episode.Duration));
        }

        return null;
    }
}
