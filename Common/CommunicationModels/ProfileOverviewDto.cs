using System;
using System.Collections.Generic;

namespace Common.CommunicationModels
{
    public class ProfileOverviewDto
    {
        public string UserId { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;
        public string FullName { get; set; } = string.Empty;
        public string? CountryCode { get; set; }
        public string? PhoneNumber { get; set; }
        public List<string> Roles { get; set; } = new();

        public int PropertyCount { get; set; }
        public int ApartmentCount { get; set; }

        public bool HasActiveSubscription { get; set; }
        public bool SubscriptionApproved { get; set; }
        public int? CurrentPlanId { get; set; }
        public string CurrentPlanName { get; set; } = string.Empty;
        public decimal? CurrentPlanPrice { get; set; }
        public int? CurrentPlanDurationInDays { get; set; }
        public DateTimeOffset? SubscriptionStartDate { get; set; }
        public DateTimeOffset? SubscriptionEndDate { get; set; }

        public List<ProfilePropertyDto> Properties { get; set; } = new();
        public List<SubscriptionPlanDto> AvailablePlans { get; set; } = new();
    }

    public class ProfilePropertyDto
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string City { get; set; } = string.Empty;
        public string Address { get; set; } = string.Empty;
        public int ApartmentCount { get; set; }
        public string AccessSource { get; set; } = string.Empty;
    }
}
