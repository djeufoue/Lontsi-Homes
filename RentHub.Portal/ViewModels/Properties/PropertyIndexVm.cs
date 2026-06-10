using System;
using Common.CommunicationModels;
using Common.Enums;

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
        public bool RequiresPayoutSetup { get; set; }
        public bool RequiresSubscriptionCheckout { get; set; }
        public bool RequiresComplianceAction { get; set; }
        public string PayoutSetupMessage { get; set; } = string.Empty;
        public string ComplianceMessage { get; set; } = string.Empty;
        public string NextOnboardingStep { get; set; } = LandlordOnboardingSteps.Complete;
        public string RegisteredPaymentNumber { get; set; } = string.Empty;
        public PayoutChannelEnum? RegisteredPaymentChannel { get; set; }
        public bool RegisteredPaymentVerified { get; set; }

        public List<PropertyCreationScopeDto> CreationScopes { get; set; } = new();
        public List<SubscriptionPlanDto> AvailablePlans { get; set; } = new();
        public List<PropertyDto> Items { get; set; } = new();

        public int TotalPages => PageSize <= 0 ? 1 : (int)Math.Ceiling((double)TotalCount / PageSize);
    }
}

