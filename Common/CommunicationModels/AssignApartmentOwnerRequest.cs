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
        public string? CountryCode { get; set; }

        [Required]
        public PermissionLevelEnum Permission { get; set; }
    }
}
