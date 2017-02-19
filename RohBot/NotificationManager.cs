﻿using System;
using RohBot.Packets;
using RohBot.Rooms;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RohBot
{
    public class NotificationManager
    {
        private class OneSignalNotificationPacket
        {
            [JsonProperty("app_id")]
            public string AppId => Program.Settings.NotificationAppID;

            [JsonProperty("contents")]
            public Dictionary<string, string> Contents { get; set; }

            [JsonProperty("include_player_ids")]
            public List<string> DeviceTokens { get; set; }

            [JsonIgnore]
            public string Sound { get; set; }

            [JsonProperty("data")]
            public Dictionary<string, string> Data { get; set; }

            // iOS
            [JsonProperty("ios_badgeType")]
            public string IosBadgeType => "Increase";

            [JsonProperty("ios_badgeCount")]
            public int IosBadgeCount => 1;

            [JsonProperty("ios_sound")]
            public string IosSound => Sound;

            // Android
            [JsonProperty("android_sound")]
            public string AndroidSound => Path.GetFileNameWithoutExtension(Sound);
        }

        private readonly object _sync = new object();
        private List<Notification> _notifications;

        public NotificationManager()
        {
            InvalidateNotificationCache();
        }

        // TODO: optimize these getters?
        public bool Exists(string deviceToken, out Notification subscription)
        {
            lock (_sync)
            {
                subscription = _notifications.FirstOrDefault(n => n.DeviceToken == deviceToken);
                return subscription != null;
            }
        }

        public IEnumerable<Notification> FindWithId(long userId)
        {
            lock (_sync)
            {
                return _notifications.Where(n => n.UserId == userId);
            }
        }

        public Notification Get(string deviceToken)
        {
            lock (_sync)
            {
                return _notifications.FirstOrDefault(n => n.DeviceToken == deviceToken);
            }
        }

        public async void HandleMessage(Room room, Message message)
        {
            var chatLine = message.Line as ChatLine;
            if (chatLine == null || chatLine.SenderId == "0")
                return;

            await Task.Yield();

            var senderId = long.Parse(chatLine.SenderId);

            List<string> recipientDevices;

            lock (_sync)
            {
                recipientDevices = _notifications
                    .Where(n => n.UserId != senderId && !room.IsBanned(n.Name) && n.Rooms.Contains(chatLine.Chat))
                    .Where(n => IsMatch(n.Regex, chatLine.Content))
                    .Select(n => n.DeviceToken)
                    .ToList();
            }

            if (recipientDevices.Count > 0)
            {
                var content = $"[{chatLine.Chat}] {WebUtility.HtmlDecode(chatLine.Sender)}: {WebUtility.HtmlDecode(chatLine.Content)}";
                await Notify(recipientDevices, content);
            }
        }
        
        public void InvalidateNotificationCache()
        {
            var cmd = new SqlCommand(@"
                SELECT notifications.*, accounts.name, accounts.rooms
                FROM rohbot.accounts
                INNER JOIN rohbot.notifications ON (rohbot.accounts.id = rohbot.notifications.userid)");

            var newNotifications = cmd.Execute().Select(row => new Notification(row)).ToList();

            lock (_sync)
            {
                _notifications = newNotifications;
            }
        }

        private static async Task Notify(List<string> deviceTokens, string message)
        {
            var notificationPacket = new OneSignalNotificationPacket();
            notificationPacket.Sound = "rohbotNotification.wav";
            notificationPacket.DeviceTokens = deviceTokens;
            notificationPacket.Contents = new Dictionary<string, string>()
            {
                { "en", message }
            };

            try
            {
                await PostNotificationRequest(notificationPacket);
            }
            catch (Exception e)
            {
                Program.Logger.Error("Failed to send push notification", e);
            }
        }

        private static async Task PostNotificationRequest(OneSignalNotificationPacket notificationPacket)
        {
            var httpClient = new HttpClient();
            var content = new StringContent(JsonConvert.SerializeObject(notificationPacket), Encoding.UTF8, "application/json");

            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Program.Settings.NotificationAPIKey);

            var responseMessage = await httpClient.PostAsync("https://onesignal.com/api/v1/notifications", content);
            var responseBody = await responseMessage.Content.ReadAsStringAsync();
            dynamic response = JsonConvert.DeserializeObject(responseBody);

            if (response.errors != null)
            {
                var errors = ((JArray)response.errors).ToObject<List<string>>();
                var errorMessage = $"Notification server returned following error(s): {string.Join(", ", errors)}";

                Program.Logger.Warn(errorMessage);
            }
        }

        private static bool IsMatch(Regex regex, string input)
        {
            try
            {
                return regex.IsMatch(input);
            }
            catch
            {
                return false;
            }
        }
    }
}
