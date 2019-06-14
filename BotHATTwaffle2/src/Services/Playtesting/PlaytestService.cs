﻿using System;
using System.Data;
using System.Threading.Tasks;
using BotHATTwaffle2.Services.Calendar;
using BotHATTwaffle2.src.Handlers;
using BotHATTwaffle2.src.Models.LiteDB;
using Discord;
using FluentScheduler;

namespace BotHATTwaffle2.Services.Playtesting
{
    public class PlaytestService
    {
        private const ConsoleColor LogColor = ConsoleColor.DarkYellow;
        private static AnnouncementMessage _announcementMessage;
        private readonly GoogleCalendar _calendar;
        private readonly DataService _data;
        private readonly LogHandler _log;
        private IUserMessage PlaytestAnnouncementMessage { get; set; }
        private int _failedToFetch = 0;
        private int _failedRetryCount = 60;
        private AnnounceMessage _oldMessage;
        private DateTime _lastSeenEditTime;

        public PlaytestService(DataService data, GoogleCalendar calendar, LogHandler log, Random random)
        {
            _data = data;
            _log = log;
            _calendar = calendar;

            PlaytestAnnouncementMessage = null;
            _oldMessage = null;

            _announcementMessage = new AnnouncementMessage(_calendar, _data, random, _log);
        }

        /// <summary>
        /// Starts the chain of events to post a new announcement message.
        /// If a valid existing message can be used, it will be used instead.
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
                if (_data.RootSettings.ProgramSettings.Debug)
                    _ = _log.LogMessage("No test was found!", false, color: LogColor);

                if (PlaytestAnnouncementMessage != null)
                {
                    if (_data.RootSettings.ProgramSettings.Debug)
                        _ = _log.LogMessage("Attempting to deleted outdated announcement", false, color: LogColor);
                    try
                    {
                        await _data.AnnouncementChannel.DeleteMessageAsync(PlaytestAnnouncementMessage);
                    }
                    catch
                    {
                        _ = _log.LogMessage(
                            "Failed to delete outdated playtest message. It may have been deleted manually",
                            false, color: LogColor);
                    }
                }

                PlaytestAnnouncementMessage = null;

                return;
            }


            if (_data.RootSettings.ProgramSettings.Debug)
                _ = _log.LogMessage("Posting or updating playtest announcement", false, color: LogColor);


            if (PlaytestAnnouncementMessage == null)
            {
                await PostNewAnnouncement();
            }
            else
                await UpdateAnnouncementMessage();
        }

        /// <summary>
        /// Attempts to update the existing announcement message.
        /// If failure to update after <value>_failedRetryCount</value> (default 60) tries, the message is
        /// assumed to be lost, and will be recreated. This may result in double announcement messages that require
        /// manual cleanup.
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
                                        $"\nCreated a new message next time.", false, color: LogColor);
                    PlaytestAnnouncementMessage = null;
                }
                else
                {
                    //Have not failed enough, lets keep trying.
                    _failedToFetch++;
                    if (_data.RootSettings.ProgramSettings.Debug)
                        _ = _log.LogMessage($"Failed to update playtest announcement {_failedToFetch} times", false,
                            color: LogColor);
                }
            }
        }

        public Embed thing()
        {
            return _announcementMessage.CreatePlaytestEmbed(_calendar.GetTestEventNoUpdate().IsCasual, true, PlaytestAnnouncementMessage.Id);
        }

        /// <summary>
        /// Posts a new playtest announcement
        /// </summary>
        /// <returns></returns>
        private async Task PostNewAnnouncement()
        {
            if (_data.RootSettings.ProgramSettings.Debug)
                _ = _log.LogMessage("Posting new announcement", false, color: LogColor);

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
                _ = _log.LogMessage($"Attempted to post new announcement, but failed", false, color: LogColor);
            }
        }

        /// <summary>
        /// Attempts to get a previously created announcement message based on values that were stored in the DB.
        /// If the located message does not match the current event it will be deleted.
        /// If nothing can be located, it does nothing.
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
                if (_data.RootSettings.ProgramSettings.Debug)
                    _ = _log.LogMessage("No message found in DB to reattach to", false, color: LogColor);

                return;
            }

            //Make sure a test is valid
            if (!testEvent.IsValid)
            {
                if (_data.RootSettings.ProgramSettings.Debug)
                    _ = _log.LogMessage("No valid test found to post", false, color: LogColor);

                return;
            }

            _ = _log.LogMessage("Attempting to get old announcement message\n" +
                                $"{_oldMessage.AnnouncementId} that was created at {_oldMessage.AnnouncementDateTime}",
                false, color: LogColor);


            var eventEditTime = _calendar.GetTestEventNoUpdate().EventEditTime;
            if (eventEditTime != null && eventEditTime.Value.Equals(_oldMessage.AnnouncementDateTime))
            {
                try
                {
                    PlaytestAnnouncementMessage =
                        await _data.AnnouncementChannel.GetMessageAsync(_oldMessage.AnnouncementId) as IUserMessage;

                    if (PlaytestAnnouncementMessage != null)
                        _ = _log.LogMessage($"Retrieved old announcement! ID: {PlaytestAnnouncementMessage.Id}", false,
                            color: LogColor);

                    var lastEditTime = _calendar.GetTestEventNoUpdate().LastEditTime;
                    if (lastEditTime != null)
                        _lastSeenEditTime = lastEditTime.Value;
                }
                catch
                {
                    _ = _log.LogMessage("Unable to retrieve old announcement message!", false, color: LogColor);
                }
            }
            else
            {
                _ = _log.LogMessage("Messages do not match, deleting old message", false, color: LogColor);
                try
                {
                    await _data.AnnouncementChannel.DeleteMessageAsync(_oldMessage.AnnouncementId);
                    PlaytestAnnouncementMessage = null;
                }
                catch
                {
                    _ = _log.LogMessage("Could not delete old message - it was likely deleted manually",
                        false, color: LogColor);
                }
            }
        }

        public void ClearScheduledAnnouncements()
        {
            JobManager.RemoveJob("[Playtest1Hour]");
            JobManager.RemoveJob("[Playtest15Minute]");
            JobManager.RemoveJob("[PlaytestStarting]");
        }

        public void SchedulePlaytestAnnouncements()
        {
            //Clear old jobs, if any.
            ClearScheduledAnnouncements();

            if (PlaytestAnnouncementMessage != null && _calendar.GetTestEventNoUpdate().TestValid())
            {
                TimeSpan singleHour = new TimeSpan(1, 0, 0);
                TimeSpan fifteenMinutes = new TimeSpan(0, 15, 0);

                DateTime adjusted = DateTime.Now.Add(singleHour);
                var startDateTime = _calendar.GetTestEventNoUpdate().StartDateTime;
                _ = _log.LogMessage($"Playtest scheduled for: {startDateTime}", false, color: LogColor);

                if (startDateTime != null && DateTime.Compare(adjusted, startDateTime.Value) < 0)
                {
                    JobManager.AddJob((async () => await PlaytestStartingInTask()), s => s
                        .WithName("[Playtest1Hour]").ToRunOnceAt(startDateTime.Value.AddMinutes(-60)));

                    _ = _log.LogMessage($"1 hour playtest announcement scheduled for:" +
                                        $"\n{JobManager.GetSchedule("[Playtest1Hour]").NextRun}", false, color: LogColor);
                }

                adjusted = DateTime.Now.Add(fifteenMinutes);
                if (startDateTime != null && DateTime.Compare(adjusted, startDateTime.Value) < 0)
                {
                    JobManager.AddJob((async () => await PlaytestFifteenMinuteTask()), s => s
                        .WithName("[Playtest15Minute]").ToRunOnceAt(startDateTime.Value.AddMinutes(-15)));

                    _ = _log.LogMessage($"15 minute playtest announcement scheduled for:" +
                                        $"\n{JobManager.GetSchedule("[Playtest15Minute]").NextRun}", false, color: LogColor);
                }

                if (startDateTime != null && DateTime.Compare(DateTime.Now, startDateTime.Value) < 0)
                {
                    JobManager.AddJob((async () => await PlaytestStartingTask()), s => s
                        .WithName("[PlaytestStarting]").ToRunOnceAt(startDateTime.Value));

                    _ = _log.LogMessage($"Starting playtest announcement scheduled for:" +
                                        $"\n{JobManager.GetSchedule("[PlaytestStarting]").NextRun}", false, color: LogColor);
                }
            }
        }

        private async Task PlaytestStartingInTask()
        {

        }

        private async Task PlaytestFifteenMinuteTask()
        {

        }

        private async Task PlaytestStartingTask()
        {

        }
    }
}