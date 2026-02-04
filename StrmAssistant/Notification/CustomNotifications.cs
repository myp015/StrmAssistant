using Emby.Notifications;
using MediaBrowser.Controller;
using StrmAssistant.Properties;
using System.Collections.Generic;

namespace StrmAssistant.Notification
{
    public class CustomNotifications : INotificationTypeFactory
    {
        private readonly IServerApplicationHost _appHost;

        public CustomNotifications(IServerApplicationHost appHost) => _appHost = appHost;

        public List<NotificationTypeInfo> GetNotificationTypes(string language)
        {
            var notificationTypes = new List<NotificationTypeInfo>
            {
                new NotificationTypeInfo
                {
                    Id = "favorites.update",
                    Name = Resources.Notification_CatchupUpdate_EventName,
                    CategoryId = "strm.assistant",
                    CategoryName = Resources.PluginOptions_EditorTitle_Strm_Assistant
                },
                new NotificationTypeInfo
                {
                    Id = "introskip.update",
                    Name = Resources.Notification_IntroSkipUpdate_EventName,
                    CategoryId = "strm.assistant",
                    CategoryName = Resources.PluginOptions_EditorTitle_Strm_Assistant
                }
            };

            if (Plugin.Instance.ExperienceEnhanceStore.GetOptions().EnhanceNotificationSystem)
            {
                notificationTypes.Add(new NotificationTypeInfo
                {
                    Id = "deep.delete",
                    Name = Resources.Notification_DeepDelete_EventName,
                    CategoryId = "strm.assistant",
                    CategoryName = Resources.PluginOptions_EditorTitle_Strm_Assistant
                });
            }

            return notificationTypes;
        }
    }
}
