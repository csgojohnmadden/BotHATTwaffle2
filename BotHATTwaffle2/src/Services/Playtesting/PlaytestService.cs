﻿using System;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using BotHATTwaffle2.Handlers;
using BotHATTwaffle2.Models.LiteDB;
using BotHATTwaffle2.Services.Calendar;
using BotHATTwaffle2.src.Handlers;
using Discord;
using FluentScheduler;

namespace BotHATTwaffle2.Services.Playtesting
{
    public class PlaytestService
    {
        private const ConsoleColor LOG_COLOR = ConsoleColor.DarkYellow;
        private static AnnouncementMessage _announcementMessage;
        private readonly GoogleCalendar _calendar;
        private readonly DataService _data;
        private readonly LogHandler _log;
        private readonly int _failedRetryCount = 60;
        private int _failedToFetch;
        private DateTime _lastSeenEditTime;
        private AnnounceMessage _oldMessage;
        public bool CanReserveServers = true;

        public PlaytestService(DataService data, GoogleCalendar calendar, LogHandler log, Random random)
        {
            _data = data;
            _log = log;
            _calendar = calendar;

            PlaytestAnnouncementMessage = null;
            _oldMessage = null;

            _announcementMessage = new AnnouncementMessage(_calendar, _data, random, _log);
        }

        private IUserMessage PlaytestAnnouncementMessage { get; set; }

        /// <summary>
        ///     Starts the chain of events to post a new announcement message.
        ///     If a valid existing message can be used, it will be used instead.
        /// </summary>
        /// <returns></returns>
        public async Task PostOrUpdateAnnouncement()
        {
            //Get event, required for posting new / updating
            //Abort if the test isn't valid
            //Clean up old message if required
            //Check old message, required for fresh boot with empty collection in db
            if (!_calendar.GetTestEvent().IsValid)
            {
                if (_data.RSettings.ProgramSettings.Debug)
                    _ = _log.LogMessage("No test was found!", false, color: LOG_COLOR);

                if (PlaytestAnnouncementMessage != null)
                {
                    if (_data.RSettings.ProgramSettings.Debug)
                        _ = _log.LogMessage("Attempting to deleted outdated announcement", false, color: LOG_COLOR);
                    try
                    {
                        await _data.AnnouncementChannel.DeleteMessageAsync(PlaytestAnnouncementMessage);
                    }
                    catch
                    {
                        _ = _log.LogMessage(
                            "Failed to delete outdated playtest message. It may have been deleted manually",
                            false, color: LOG_COLOR);
                    }
                }

                PlaytestAnnouncementMessage = null;

                return;
            }


            if (_data.RSettings.ProgramSettings.Debug)
                _ = _log.LogMessage("Posting or updating playtest announcement", false, color: LOG_COLOR);


            if (PlaytestAnnouncementMessage == null)
                await PostNewAnnouncement();
            else
                await UpdateAnnouncementMessage();
        }

        /// <summary>
        ///     Attempts to update the existing announcement message.
        ///     If failure to update after
        ///     <value>_failedRetryCount</value>
        ///     (default 60) tries, the message is
        ///     assumed to be lost, and will be recreated. This may result in double announcement messages that require
        ///     manual cleanup.
        /// </summary>
        /// <returns></returns>
        private async Task UpdateAnnouncementMessage()
        {
            try
            {
                //Compare the current event edit time with the last know.
                //The current event edit time will be different from last known if the event has changed.
                var eventEditTime = _calendar.GetTestEventNoUpdate().EventEditTime;
                if (eventEditTime != null && eventEditTime.Value.Equals(_lastSeenEditTime))
                {
                    await PlaytestAnnouncementMessage.ModifyAsync(x =>
                    {
                        x.Embed = _announcementMessage.CreatePlaytestEmbed(
                            _calendar.GetTestEventNoUpdate().IsCasual);
                    });
                    _failedToFetch = 0;
                }
                else
                {
                    //Being in this else means we know the message is different, remake it.
                    await _data.AnnouncementChannel.DeleteMessageAsync(PlaytestAnnouncementMessage);
                    await PostNewAnnouncement();
                }

                var lastEditTime = _calendar.GetTestEventNoUpdate().LastEditTime;
                if (lastEditTime != null)
                    _lastSeenEditTime = lastEditTime.Value;
            }
            catch
            {
                //Have we failed enough to rebuild?
                if (_failedToFetch >= _failedRetryCount)
                {
                    _ = _log.LogMessage($"Tried to update announcement messages {_failedToFetch}, but failed." +
                                        "\nCreated a new message next time.", false, color: LOG_COLOR);
                    PlaytestAnnouncementMessage = null;
                }
                else
                {
                    //Have not failed enough, lets keep trying.
                    _failedToFetch++;
                    if (_data.RSettings.ProgramSettings.Debug)
                        _ = _log.LogMessage($"Failed to update playtest announcement {_failedToFetch} times", false,
                            color: LOG_COLOR);
                }
            }
        }

        /// <summary>
        ///     Posts a new playtest announcement
        /// </summary>
        /// <returns></returns>
        private async Task PostNewAnnouncement()
        {
            if (_data.RSettings.ProgramSettings.Debug)
                _ = _log.LogMessage("Posting new announcement", false, color: LOG_COLOR);

            //Stop asking server for player counts
            _data.IncludePlayerCount = false;
            _data.PlayerCount = "0";

            //Default the server password
            await _data.RconCommand(_calendar.GetTestEventNoUpdate().ServerLocation, $"sv_password {_data.RSettings.General.CasualPassword}");

            CanReserveServers = true;

            try
            {
                //Make the announcement and store to a variable
                PlaytestAnnouncementMessage = await _data.AnnouncementChannel.SendMessageAsync(
                    embed: _announcementMessage.CreatePlaytestEmbed(_calendar.GetTestEventNoUpdate().IsCasual));

                //Hand off the message and time to be stored in the DB for use on restarts
                var eventEditTime = _calendar.GetTestEventNoUpdate().EventEditTime;
                if (eventEditTime != null)
                    DatabaseHandler.StoreAnnouncement(PlaytestAnnouncementMessage,
                        eventEditTime.Value);

                var lastEditTime = _calendar.GetTestEventNoUpdate().LastEditTime;
                if (lastEditTime != null)
                    _lastSeenEditTime = lastEditTime.Value;

                SchedulePlaytestAnnouncements();
            }
            catch
            {
                _ = _log.LogMessage("Attempted to post new announcement, but failed", false, color: LOG_COLOR);
            }
        }

        /// <summary>
        ///     Attempts to get a previously created announcement message based on values that were stored in the DB.
        ///     If the located message does not match the current event it will be deleted.
        ///     If nothing can be located, it does nothing.
        /// </summary>
        /// <returns></returns>
        public async Task TryAttachPreviousAnnounceMessage()
        {
            var testEvent = _calendar.GetTestEvent();

            //Get the last known message
            _oldMessage = DatabaseHandler.GetAnnouncementMessage();

            //No message found in the DB, do nothing. Likely to happen when DB is new.
            if (_oldMessage == null)
            {
                if (_data.RSettings.ProgramSettings.Debug)
                    _ = _log.LogMessage("No message found in DB to reattach to", false, color: LOG_COLOR);

                return;
            }

            //Make sure a test is valid
            if (!testEvent.IsValid)
            {
                if (_data.RSettings.ProgramSettings.Debug)
                    _ = _log.LogMessage("No valid test found to post", false, color: LOG_COLOR);

                return;
            }

            _ = _log.LogMessage("Attempting to get old announcement message\n" +
                                $"{_oldMessage.AnnouncementId} that was created at {_oldMessage.AnnouncementDateTime}",
                false, color: LOG_COLOR);


            var eventEditTime = _calendar.GetTestEventNoUpdate().EventEditTime;
            if (eventEditTime != null && eventEditTime.Value.Equals(_oldMessage.AnnouncementDateTime))
            {
                try
                {
                    PlaytestAnnouncementMessage =
                        await _data.AnnouncementChannel.GetMessageAsync(_oldMessage.AnnouncementId) as IUserMessage;

                    if (PlaytestAnnouncementMessage != null)
                        _ = _log.LogMessage($"Retrieved old announcement! ID: {PlaytestAnnouncementMessage.Id}", false,
                            color: LOG_COLOR);

                    var lastEditTime = _calendar.GetTestEventNoUpdate().LastEditTime;
                    if (lastEditTime != null)
                        _lastSeenEditTime = lastEditTime.Value;
                }
                catch
                {
                    _ = _log.LogMessage("Unable to retrieve old announcement message!", false, color: LOG_COLOR);
                }
            }
            else
            {
                _ = _log.LogMessage("Messages do not match, deleting old message", false, color: LOG_COLOR);
                try
                {
                    await _data.AnnouncementChannel.DeleteMessageAsync(_oldMessage.AnnouncementId);
                    PlaytestAnnouncementMessage = null;
                }
                catch
                {
                    _ = _log.LogMessage("Could not delete old message - it was likely deleted manually",
                        false, color: LOG_COLOR);
                }
            }
        }

        public void ClearScheduledAnnouncements()
        {
            JobManager.RemoveJob("[Playtest1Hour]");
            JobManager.RemoveJob("[Playtest15Minute]");
            JobManager.RemoveJob("[PlaytestStarting]");
            JobManager.RemoveJob("[QueryPlayerCount]");
        }

        public void SchedulePlaytestAnnouncements()
        {
            //Clear old jobs, if any.
            ClearScheduledAnnouncements();

            if (PlaytestAnnouncementMessage != null && _calendar.GetTestEventNoUpdate().TestValid())
            {
                var singleHour = new TimeSpan(1, 0, 0);
                var fifteenMinutes = new TimeSpan(0, 15, 0);

                var adjusted = DateTime.Now.Add(singleHour);
                var startDateTime = _calendar.GetTestEventNoUpdate().StartDateTime;
                _ = _log.LogMessage($"Playtest scheduled for: {startDateTime}", false, color: LOG_COLOR);

                if (startDateTime != null && DateTime.Compare(adjusted, startDateTime.Value) < 0)
                {
                    //Have to set the alert to be -61 minutes from start time, otherwise the announcement says
                    //that the test starts in 59 minutes.
                    JobManager.AddJob(async () => await PlaytestStartingInTask(), s => s
                        .WithName("[Playtest1Hour]").ToRunOnceAt(startDateTime.Value.AddMinutes(-61)));

                    _ = _log.LogMessage("1 hour playtest announcement scheduled for:" +
                                        $"\n{JobManager.GetSchedule("[Playtest1Hour]").NextRun}", false,
                        color: LOG_COLOR);
                }

                adjusted = DateTime.Now.Add(fifteenMinutes);
                if (startDateTime != null && DateTime.Compare(adjusted, startDateTime.Value) < 0)
                {
                    JobManager.AddJob(async () => await PlaytestFifteenMinuteTask(), s => s
                        .WithName("[Playtest15Minute]").ToRunOnceAt(startDateTime.Value.AddMinutes(-15)));

                    _ = _log.LogMessage("15 minute playtest announcement scheduled for:" +
                                        $"\n{JobManager.GetSchedule("[Playtest15Minute]").NextRun}", false,
                        color: LOG_COLOR);
                }

                if (startDateTime != null && DateTime.Compare(DateTime.Now, startDateTime.Value) < 0)
                {
                    JobManager.AddJob(async () => await PlaytestStartingTask(), s => s
                        .WithName("[PlaytestStarting]").ToRunOnceAt(startDateTime.Value));

                    _ = _log.LogMessage("Starting playtest announcement scheduled for:" +
                                        $"\n{JobManager.GetSchedule("[PlaytestStarting]").NextRun}", false,
                        color: LOG_COLOR);
                }
            }
        }

        /// <summary>
        /// Posts a new announcement message and alerts playtester role
        /// </summary>
        /// <returns></returns>
        public async Task PlaytestStartingInTask()
        {
            if (_data.RSettings.ProgramSettings.Debug)
                _ = _log.LogMessage("Posting playtest announcement", false, color: LOG_COLOR);

            //Start asking the server for player counts.
            _data.IncludePlayerCount = true;

            //Start asking for player counts
            JobManager.AddJob(async () => await _data.GetPlayCountFromServer(_data.GetServerCode(_calendar.GetTestEventNoUpdate().ServerLocation)),
                s => s.WithName("[QueryPlayerCount]").ToRunEvery(60).Seconds());

            //Prevent new server reservations.
            CanReserveServers = false;

            //Figure out how long until the event starts
            var countdown = _calendar.GetTestEventNoUpdate().StartDateTime.GetValueOrDefault().Subtract(DateTime.Now);
            var countdownString =
                countdown.ToString("d'D 'h' Hour 'm' Minutes'").TrimStart(' ', 'D', 'H','o','u','r', '0').Replace(" 0 Minutes","");
            
            var mentionRole = _data.PlayTesterRole;
            string unsubInfo = "";

            //DM users about their test
            foreach (var creator in _calendar.GetTestEventNoUpdate().Creators)
            {
                try
                {
                    await creator.SendMessageAsync($"Don't forget that you have a playtest for __**{_calendar.GetTestEventNoUpdate().Title}**__ in __**{countdownString}**__");
                }
                catch
                {
                    //Could not DM creator about their test.
                }
            }

            //Handle comp or casual
            if (_calendar.GetTestEvent().IsCasual)
            {
                await _data.RconCommand(_calendar.GetTestEventNoUpdate().ServerLocation,
                    $"sv_password {_data.RSettings.General.CasualPassword}");
                unsubInfo = "\nType `>playtester` to stop getting these notifications.";
            }
            else
            {
                await _data.RconCommand(_calendar.GetTestEventNoUpdate().ServerLocation,
                    $"sv_password {_calendar.GetTestEventNoUpdate().CompPassword}");
                mentionRole = _data.CompetitiveTesterRole;
            }

            await mentionRole.ModifyAsync(x => { x.Mentionable = true; });

            await _data.TestingChannel.SendMessageAsync($"Heads up {mentionRole.Mention}! " +
                                                        $"There is a playtest starting in {countdownString}." +
                                                        $"{unsubInfo}",
                embed: _announcementMessage.CreatePlaytestEmbed(_calendar.GetTestEventNoUpdate().IsCasual,
                    true, PlaytestAnnouncementMessage.Id));

            await mentionRole.ModifyAsync(x => { x.Mentionable = false; });

            await _data.CompetitiveTestingChannel.SendMessageAsync($"**{_calendar.GetTestEventNoUpdate().Title}** Paste the following into console to join:" +
                                                                   $"```connect {_calendar.GetTestEventNoUpdate().ServerLocation}; password {_calendar.GetTestEventNoUpdate().CompPassword}```");
        }
        
        /// <summary>
        /// Server setup tasks for 15 minutes before a test
        /// </summary>
        /// <returns></returns>
        private async Task PlaytestFifteenMinuteTask()
        {
            if (_data.RSettings.ProgramSettings.Debug)
                _ = _log.LogMessage("Playtest 15 minute setup running...", false, color: LOG_COLOR);

            //Set password as needed, again just in case RCON wasn't listening / server wasn't ready.
            if (_calendar.GetTestEvent().IsCasual)
            {
                await _data.RconCommand(_calendar.GetTestEventNoUpdate().ServerLocation,
                    $"sv_password {_data.RSettings.General.CasualPassword}");
            }
            else
            {
                await _data.RconCommand(_calendar.GetTestEventNoUpdate().ServerLocation,
                    $"sv_password {_calendar.GetTestEventNoUpdate().CompPassword}");


                //Setup a casual server for people who aren't in the comp test group
                await _data.RconCommand(_data.GetServerCode(_calendar.GetTestEventNoUpdate().CompCasualServer),
                    $"host_workshop_map {_data.GetWorkshopIdFromFqdn(_calendar.GetTestEventNoUpdate().WorkshopLink.ToString())}");

                //Delay to make sure level has actually changed
                await Task.Delay(10000);
                await _data.RconCommand(_data.GetServerCode(_calendar.GetTestEventNoUpdate().CompCasualServer),
                    $"exec {_data.RSettings.General.PostgameConfig}; sv_cheats 1; sv_password {_data.RSettings.General.CasualPassword}; bot_stop 1");
            }

            //Delay to make sure password is set.
            await Task.Delay(1000);

            await _data.RconCommand(_data.GetServerCode(_calendar.GetTestEventNoUpdate().ServerLocation),
                $"host_workshop_map {_data.GetWorkshopIdFromFqdn(_calendar.GetTestEventNoUpdate().WorkshopLink.ToString())}");

            //Delay to make sure level has actually changed
            await Task.Delay(10000);
            await _data.RconCommand(_data.GetServerCode(_calendar.GetTestEventNoUpdate().ServerLocation),
                $"exec {_data.RSettings.General.PostgameConfig}; sv_cheats 1; bot_stop 1");

            var embed = new EmbedBuilder()
                .WithAuthor($"Settings up test server for {_calendar.GetTestEventNoUpdate().Title}")
                .WithTitle("Workshop Link")
                .WithUrl(_calendar.GetTestEventNoUpdate().WorkshopLink.ToString())
                .WithThumbnailUrl(_calendar.GetTestEventNoUpdate().CanUseGallery ? _calendar.GetTestEventNoUpdate().GalleryImages[0] : _data.RSettings.General.FallbackTestImageUrl)
                .WithDescription($"{DatabaseHandler.GetTestServer(_calendar.GetTestEventNoUpdate().ServerLocation).Description}" +
                                 $"\n{_calendar.GetTestEventNoUpdate().Description}")
                .WithColor(new Color(51,100,173));
            
            await _data.TestingChannel.SendMessageAsync(embed: embed.Build());
        }

        /// <summary>
        /// Announcement for playtest starting
        /// </summary>
        /// <returns></returns>
        private async Task PlaytestStartingTask()
        {
            if (_data.RSettings.ProgramSettings.Debug)
                _ = _log.LogMessage("Posting playtest start announcement", false, color: LOG_COLOR);

            var mentionRole = _data.PlayTesterRole;
            string unsubInfo = "";
            //Handle comp or casual
            if (_calendar.GetTestEvent().IsCasual)
            {
                await _data.RconCommand(_calendar.GetTestEventNoUpdate().ServerLocation,
                    $"sv_password {_data.RSettings.General.CasualPassword}");
                unsubInfo = "\nType `>playtester` to stop getting these notifications.";
            }
            else
            {
                await _data.RconCommand(_calendar.GetTestEventNoUpdate().ServerLocation,
                    $"sv_password {_calendar.GetTestEventNoUpdate().CompPassword}");
                mentionRole = _data.CompetitiveTesterRole;
            }

            await mentionRole.ModifyAsync(x => { x.Mentionable = true; });

            await _data.TestingChannel.SendMessageAsync($"Heads up {mentionRole.Mention}! " +
                                                        $"There is a playtest starting __now__! {unsubInfo}",
                embed: _announcementMessage.CreatePlaytestEmbed(_calendar.GetTestEventNoUpdate().IsCasual,
                    true, PlaytestAnnouncementMessage.Id));

            await mentionRole.ModifyAsync(x => { x.Mentionable = false; });
        }
    }
}