namespace RentHub.Portal.ViewModels.Auth
{
    public class SubscriptionPlanOptionVm
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public decimal Price { get; set; }
        public int DurationInDays { get; set; }
        public string? Description { get; set; }
    }
}
