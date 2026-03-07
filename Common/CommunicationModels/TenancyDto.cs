using System;

namespace Common.CommunicationModels
{
    /// <summary>
    /// Data transfer object representing a tenancy listing or detail.
    /// Includes references to related apartment and property names.
    /// </summary>
    public class TenancyDto
    {
        public int Id { get; set; }
        public string ApartmentName { get; set; } = string.Empty;
        public string PropertyName { get; set; } = string.Empty;
        public DateTimeOffset StartDate { get; set; }
        public DateTimeOffset? EndDate { get; set; }
        public decimal MonthlyRent { get; set; }
        /// <summary>
        /// Indicates whether the current authenticated user is the landlord/owner of the apartment.
        /// </summary>
        public bool IsOwner { get; set; }
    }
}