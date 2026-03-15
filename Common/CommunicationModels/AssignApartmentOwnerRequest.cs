using Common.Enums;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Common.CommunicationModels
{
    public class AssignApartmentOwnerRequest
    {
        [Required, EmailAddress]
        public string Email { get; set; } = string.Empty;

        public string? FullName { get; set; }

        [RegularExpression(@"^\+?\d+$", ErrorMessage = "Country code must contain only digits and may start with +.")]
        public string? CountryCode { get; set; }

        [RegularExpression(@"^\+?\d+$", ErrorMessage = "Phone number must contain only digits and may start with +.")]
        public string? PhoneNumber { get; set; }

        [Required]
        public PermissionLevelEnum Permission { get; set; }
    }
}
