namespace Common.CommunicationModels
{
    public sealed class UpdatePropertyDialogSettingsRequest
    {
        public bool ShowSuccessMessages { get; set; } = true;
        public bool AutoCloseEnabled { get; set; }
        public int AutoCloseSeconds { get; set; } = 5;
        public string DialogPosition { get; set; } = "bottom-center";
    }
}
