using System;
using Common.CommunicationModels;

namespace RentHub.Portal.ViewModels.Properties
{
    public class PropertyIndexVm
    {
        public string? Search { get; set; }
        public string? City { get; set; }
        public string? Access { get; set; }

        public int Page { get; set; } = 1;
        public int PageSize { get; set; } = 12;
        public int TotalCount { get; set; }

        public string UserRole { get; set; } = string.Empty;
        public bool CanCreateProperty { get; set; }
        public bool ShowCreateEntryPoint { get; set; }
        public bool RequiresSubscriptionCheckout { get; set; }

        public List<PropertyCreationScopeDto> CreationScopes { get; set; } = new();
        public List<SubscriptionPlanDto> AvailablePlans { get; set; } = new();
        public List<PropertyDto> Items { get; set; } = new();

        public int TotalPages => PageSize <= 0 ? 1 : (int)Math.Ceiling((double)TotalCount / PageSize);
    }
}

