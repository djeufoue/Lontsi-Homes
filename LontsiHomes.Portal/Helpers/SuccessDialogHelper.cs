using Microsoft.AspNetCore.Http;

namespace LontsiHomes.Portal.Helpers
{
    public static class SuccessDialogHelper
    {
        private const int DefaultAutoCloseSeconds = 5;
        private const bool DefaultAutoCloseEnabled = false;
        private const bool DefaultShowSuccessMessages = true;
        public const string DefaultPosition = "bottom-center";
        private const int MinAutoCloseSeconds = 2;
        private const int MaxAutoCloseSeconds = 30;

        private static readonly HashSet<string> AllowedPositions = new(StringComparer.OrdinalIgnoreCase)
        {
            "top-start",
            "top-center",
            "top-end",
            "center-start",
            "center",
            "center-end",
            "bottom-start",
            DefaultPosition,
            "bottom-end"
        };

        private const string ActivePropertyIdKey = "SuccessDialog.ActivePropertyId";
        private const string ActiveShowCloseButtonKey = "SuccessDialog.ShowCloseButton";
        private const string ActiveAutoCloseEnabledKey = "SuccessDialog.AutoCloseEnabled";
        private const string ActiveAutoCloseSecondsKey = "SuccessDialog.AutoCloseSeconds";
        private const string ActiveShowSuccessMessagesKey = "SuccessDialog.ShowSuccessMessages";
        private const string ActivePositionKey = "SuccessDialog.Position";

        public static DialogPreferences GetForProperty(ISession session, int propertyId)
        {
            var showCloseRaw = session.GetString(PropertyShowCloseKey(propertyId));
            var autoCloseEnabledRaw = session.GetString(PropertyAutoCloseEnabledKey(propertyId));
            var autoCloseRaw = session.GetString(PropertyAutoCloseKey(propertyId));
            var showSuccessMessagesRaw = session.GetString(PropertyShowSuccessMessagesKey(propertyId));
            var positionRaw = session.GetString(PropertyPositionKey(propertyId));

            var showClose = showCloseRaw != "0";
            var autoCloseEnabled = autoCloseEnabledRaw == "1";
            var autoClose = ParseAutoClose(autoCloseRaw);
            var showSuccessMessages = showSuccessMessagesRaw == null
                ? DefaultShowSuccessMessages
                : showSuccessMessagesRaw == "1";

            return new DialogPreferences(
                showClose,
                autoCloseEnabled,
                autoClose,
                showSuccessMessages,
                NormalizePosition(positionRaw));
        }

        public static void SaveForProperty(
            ISession session,
            int propertyId,
            bool showCloseButton,
            bool autoCloseEnabled,
            int autoCloseSeconds,
            bool showSuccessMessages,
            string? position)
            => CacheForProperty(
                session,
                propertyId,
                showCloseButton,
                autoCloseEnabled,
                autoCloseSeconds,
                showSuccessMessages,
                position);

        public static void CacheForProperty(
            ISession session,
            int propertyId,
            bool showCloseButton,
            bool autoCloseEnabled,
            int autoCloseSeconds,
            bool showSuccessMessages,
            string? position)
        {
            var normalized = NormalizeAutoClose(autoCloseSeconds);
            var normalizedPosition = NormalizePosition(position);

            session.SetString(PropertyShowCloseKey(propertyId), showCloseButton ? "1" : "0");
            session.SetString(PropertyAutoCloseEnabledKey(propertyId), autoCloseEnabled ? "1" : "0");
            session.SetString(PropertyAutoCloseKey(propertyId), normalized.ToString());
            session.SetString(PropertyShowSuccessMessagesKey(propertyId), showSuccessMessages ? "1" : "0");
            session.SetString(PropertyPositionKey(propertyId), normalizedPosition);

            ActivateForProperty(session, propertyId);
        }

        public static void ActivateForProperty(ISession session, int propertyId)
        {
            var settings = GetForProperty(session, propertyId);

            session.SetString(ActivePropertyIdKey, propertyId.ToString());
            session.SetString(ActiveShowCloseButtonKey, settings.ShowCloseButton ? "1" : "0");
            session.SetString(ActiveAutoCloseEnabledKey, settings.AutoCloseEnabled ? "1" : "0");
            session.SetString(ActiveAutoCloseSecondsKey, settings.AutoCloseSeconds.ToString());
            session.SetString(ActiveShowSuccessMessagesKey, settings.ShowSuccessMessages ? "1" : "0");
            session.SetString(ActivePositionKey, settings.Position);
        }

        public static DialogPreferences GetActive(ISession session)
        {
            var showCloseRaw = session.GetString(ActiveShowCloseButtonKey);
            var autoCloseEnabledRaw = session.GetString(ActiveAutoCloseEnabledKey);
            var autoCloseRaw = session.GetString(ActiveAutoCloseSecondsKey);
            var showSuccessMessagesRaw = session.GetString(ActiveShowSuccessMessagesKey);
            var positionRaw = session.GetString(ActivePositionKey);

            var showClose = showCloseRaw != "0";
            var autoCloseEnabled = autoCloseEnabledRaw == "1";
            var autoClose = ParseAutoClose(autoCloseRaw);
            var showSuccessMessages = showSuccessMessagesRaw == null
                ? DefaultShowSuccessMessages
                : showSuccessMessagesRaw == "1";

            return new DialogPreferences(
                showClose,
                autoCloseEnabled,
                autoClose,
                showSuccessMessages,
                NormalizePosition(positionRaw));
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

        public static string NormalizePosition(string? position)
        {
            var normalized = position?.Trim().ToLowerInvariant();
            return !string.IsNullOrWhiteSpace(normalized) && AllowedPositions.Contains(normalized)
                ? normalized
                : DefaultPosition;
        }

        private static string PropertyShowCloseKey(int propertyId) => $"SuccessDialog.Property.{propertyId}.ShowCloseButton";
        private static string PropertyAutoCloseEnabledKey(int propertyId) => $"SuccessDialog.Property.{propertyId}.AutoCloseEnabled";
        private static string PropertyAutoCloseKey(int propertyId) => $"SuccessDialog.Property.{propertyId}.AutoCloseSeconds";
        private static string PropertyShowSuccessMessagesKey(int propertyId) => $"SuccessDialog.Property.{propertyId}.ShowSuccessMessages";
        private static string PropertyPositionKey(int propertyId) => $"SuccessDialog.Property.{propertyId}.Position";

        public sealed record DialogPreferences(
            bool ShowCloseButton,
            bool AutoCloseEnabled,
            int AutoCloseSeconds,
            bool ShowSuccessMessages,
            string Position);
    }
}
