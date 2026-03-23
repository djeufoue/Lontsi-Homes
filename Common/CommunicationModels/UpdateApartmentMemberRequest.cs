using Common.Enums;
using System.ComponentModel.DataAnnotations;

namespace Common.CommunicationModels
{
    public class UpdateApartmentMemberRequest
    {
        [Required]
        public ApartmentMemberRoleEnum Role { get; set; }

        [Required]
        public PermissionLevelEnum Permission { get; set; }
    }
}
