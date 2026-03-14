using Microsoft.AspNetCore.Http;

namespace RentHub.Portal.Helpers
{
    public static class SuccessDialogHelper
    {
        private const int DefaultAutoCloseSeconds = 5;
        private const bool DefaultAutoCloseEnabled = false;
        private const int MinAutoCloseSeconds = 2;
        private const int MaxAutoCloseSeconds = 30;

        private const string ActivePropertyIdKey = "SuccessDialog.ActivePropertyId";
        private const string ActiveShowCloseButtonKey = "SuccessDialog.ShowCloseButton";
        private const string ActiveAutoCloseEnabledKey = "SuccessDialog.AutoCloseEnabled";
        private const string ActiveAutoCloseSecondsKey = "SuccessDialog.AutoCloseSeconds";

        public static (bool ShowCloseButton, bool AutoCloseEnabled, int AutoCloseSeconds) GetForProperty(ISession session, int propertyId)
        {
            var showCloseRaw = session.GetString(PropertyShowCloseKey(propertyId));
            var autoCloseEnabledRaw = session.GetString(PropertyAutoCloseEnabledKey(propertyId));
            var autoCloseRaw = session.GetString(PropertyAutoCloseKey(propertyId));

            var showClose = showCloseRaw != "0";
            var autoCloseEnabled = autoCloseEnabledRaw == "1";
            var autoClose = ParseAutoClose(autoCloseRaw);

            return (showClose, autoCloseEnabled, autoClose);
        }

        public static void SaveForProperty(ISession session, int propertyId, bool showCloseButton, bool autoCloseEnabled, int autoCloseSeconds)
        {
            var normalized = NormalizeAutoClose(autoCloseSeconds);

            session.SetString(PropertyShowCloseKey(propertyId), showCloseButton ? "1" : "0");
            session.SetString(PropertyAutoCloseEnabledKey(propertyId), autoCloseEnabled ? "1" : "0");
            session.SetString(PropertyAutoCloseKey(propertyId), normalized.ToString());

            ActivateForProperty(session, propertyId);
        }

        public static void ActivateForProperty(ISession session, int propertyId)
        {
            var settings = GetForProperty(session, propertyId);

            session.SetString(ActivePropertyIdKey, propertyId.ToString());
            session.SetString(ActiveShowCloseButtonKey, settings.ShowCloseButton ? "1" : "0");
            session.SetString(ActiveAutoCloseEnabledKey, settings.AutoCloseEnabled ? "1" : "0");
            session.SetString(ActiveAutoCloseSecondsKey, settings.AutoCloseSeconds.ToString());
        }

        public static (bool ShowCloseButton, bool AutoCloseEnabled, int AutoCloseSeconds) GetActive(ISession session)
        {
            var showCloseRaw = session.GetString(ActiveShowCloseButtonKey);
            var autoCloseEnabledRaw = session.GetString(ActiveAutoCloseEnabledKey);
            var autoCloseRaw = session.GetString(ActiveAutoCloseSecondsKey);

            var showClose = showCloseRaw != "0";
            var autoCloseEnabled = autoCloseEnabledRaw == "1";
            var autoClose = ParseAutoClose(autoCloseRaw);

            return (showClose, autoCloseEnabled, autoClose);
        }

        private static int ParseAutoClose(string? value)
        {
            if (!int.TryParse(value, out var parsed))
                return DefaultAutoCloseSeconds;

            return NormalizeAutoClose(parsed);
        }

        private static int NormalizeAutoClose(int seconds)
        {
            if (seconds < MinAutoCloseSeconds) return MinAutoCloseSeconds;
            if (seconds > MaxAutoCloseSeconds) return MaxAutoCloseSeconds;
            return seconds;
        }

        private static string PropertyShowCloseKey(int propertyId) => $"SuccessDialog.Property.{propertyId}.ShowCloseButton";
        private static string PropertyAutoCloseEnabledKey(int propertyId) => $"SuccessDialog.Property.{propertyId}.AutoCloseEnabled";
        private static string PropertyAutoCloseKey(int propertyId) => $"SuccessDialog.Property.{propertyId}.AutoCloseSeconds";
    }
}
