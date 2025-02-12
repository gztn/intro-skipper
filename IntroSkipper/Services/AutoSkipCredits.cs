// Copyright (C) 2024 Intro-Skipper contributors <intro-skipper.org>
// SPDX-License-Identifier: GPL-3.0-only.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Timer = System.Timers.Timer;

namespace IntroSkipper.Services
{
    /// <summary>
    /// Automatically skip past credit sequences.
    /// Commands clients to seek to the end of the credits as soon as they start playing it.
    /// </summary>
    /// <remarks>
    /// Initializes a new instance of the <see cref="AutoSkipCredits"/> class.
    /// </remarks>
    /// <param name="userDataManager">User data manager.</param>
    /// <param name="sessionManager">Session manager.</param>
    /// <param name="logger">Logger.</param>
    public class AutoSkipCredits(
        IUserDataManager userDataManager,
        ISessionManager sessionManager,
        ILogger<AutoSkipCredits> logger) : IHostedService, IDisposable
    {
        private readonly object _sentSeekCommandLock = new();

        private ILogger<AutoSkipCredits> _logger = logger;
        private IUserDataManager _userDataManager = userDataManager;
        private ISessionManager _sessionManager = sessionManager;
        private Timer _playbackTimer = new(1000);
        private Dictionary<string, bool> _sentSeekCommand = [];
        private HashSet<string> _clientList = [];

        private void AutoSkipCreditChanged(object? sender, BasePluginConfiguration e)
        {
            var configuration = (PluginConfiguration)e;
            _clientList = [.. configuration.ClientList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
            var newState = configuration.AutoSkipCredits || _clientList.Count > 0;
            _logger.LogDebug("Setting playback timer enabled to {NewState}", newState);
            _playbackTimer.Enabled = newState;
        }

        private void UserDataManager_UserDataSaved(object? sender, UserDataSaveEventArgs e)
        {
            var itemId = e.Item.Id;
            var newState = false;
            var episodeNumber = e.Item.IndexNumber.GetValueOrDefault(-1);

            // Ignore all events except playback start & end
            if (e.SaveReason != UserDataSaveReason.PlaybackStart && e.SaveReason != UserDataSaveReason.PlaybackFinished)
            {
                return;
            }

            // Lookup the session for this item.
            SessionInfo? session = null;

            try
            {
                foreach (var needle in _sessionManager.Sessions)
                {
                    if (needle.UserId == e.UserId && needle.NowPlayingItem.Id == itemId)
                    {
                        session = needle;
                        break;
                    }
                }

                if (session == null)
                {
                    _logger.LogInformation("Unable to find session for {Item}", itemId);
                    return;
                }
            }
            catch (Exception ex) when (ex is NullReferenceException || ex is ResourceNotFoundException)
            {
                return;
            }

            // If this is the first episode in the season, and SkipFirstEpisode is false, pretend that we've already sent the seek command for this playback session.
            if (Plugin.Instance!.Configuration.SkipFirstEpisode && episodeNumber == 1)
            {
                newState = true;
            }

            // Reset the seek command state for this device.
            lock (_sentSeekCommandLock)
            {
                var device = session.DeviceId;

                _logger.LogDebug("Resetting seek command state for session {Session}", device);
                _sentSeekCommand[device] = newState;
            }
        }

        private void PlaybackTimer_Elapsed(object? sender, ElapsedEventArgs e)
        {
            foreach (var session in _sessionManager.Sessions.Where(s => Plugin.Instance!.Configuration.AutoSkipCredits || _clientList.Contains(s.Client, StringComparer.OrdinalIgnoreCase)))
            {
                var deviceId = session.DeviceId;
                var itemId = session.NowPlayingItem.Id;
                var position = session.PlayState.PositionTicks / TimeSpan.TicksPerSecond;

                // Don't send the seek command more than once in the same session.
                lock (_sentSeekCommandLock)
                {
                    if (_sentSeekCommand.TryGetValue(deviceId, out var sent) && sent)
                    {
                        _logger.LogTrace("Already sent seek command for session {Session}", deviceId);
                        continue;
                    }
                }

                // Assert that credits were detected for this item.
                var credit = Plugin.Instance!.GetSegmentByMode(itemId, AnalysisMode.Credits);
                if (!credit.Valid)
                {
                    continue;
                }

                // Seek is unreliable if called at the very end of an episode.
                var adjustedStart = credit.Start + Plugin.Instance.Configuration.SecondsOfCreditsStartToPlay;
                var adjustedEnd = credit.End - Plugin.Instance.Configuration.RemainingSecondsOfIntro;

                _logger.LogTrace(
                    "Playback position is {Position}, credits run from {Start} to {End}",
                    position,
                    adjustedStart,
                    adjustedEnd);

                if (position < adjustedStart || position > adjustedEnd)
                {
                    continue;
                }

                // Notify the user that credits are being skipped for them.
                var notificationText = Plugin.Instance!.Configuration.AutoSkipCreditsNotificationText;
                if (!string.IsNullOrWhiteSpace(notificationText))
                {
                    _sessionManager.SendMessageCommand(
                    session.Id,
                    session.Id,
                    new MessageCommand
                    {
                        Header = string.Empty,      // some clients require header to be a string instead of null
                        Text = notificationText,
                        TimeoutMs = 2000,
                    },
                    CancellationToken.None);
                }

                _logger.LogDebug("Sending seek command to {Session}", deviceId);

                _sessionManager.SendPlaystateCommand(
                    session.Id,
                    session.Id,
                    new PlaystateRequest
                    {
                        Command = PlaystateCommand.Seek,
                        ControllingUserId = session.UserId.ToString(),
                        SeekPositionTicks = (long)adjustedEnd * TimeSpan.TicksPerSecond,
                    },
                    CancellationToken.None);

                // Flag that we've sent the seek command so that it's not sent repeatedly
                lock (_sentSeekCommandLock)
                {
                    _logger.LogTrace("Setting seek command state for session {Session}", deviceId);
                    _sentSeekCommand[deviceId] = true;
                }
            }
        }

        /// <summary>
        /// Dispose.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Protected dispose.
        /// </summary>
        /// <param name="disposing">Dispose.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (!disposing)
            {
                return;
            }

            _playbackTimer.Stop();
            _playbackTimer.Dispose();
        }

        /// <inheritdoc />
        public Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogDebug("Setting up automatic credit skipping");

            _userDataManager.UserDataSaved += UserDataManager_UserDataSaved;
            Plugin.Instance!.ConfigurationChanged += AutoSkipCreditChanged;

            // Make the timer restart automatically and set enabled to match the configuration value.
            _playbackTimer.AutoReset = true;
            _playbackTimer.Elapsed += PlaybackTimer_Elapsed;

            AutoSkipCreditChanged(null, Plugin.Instance.Configuration);

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task StopAsync(CancellationToken cancellationToken)
        {
            _userDataManager.UserDataSaved -= UserDataManager_UserDataSaved;
            return Task.CompletedTask;
        }
    }
}
