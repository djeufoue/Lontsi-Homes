using Common.Enums;
using System.ComponentModel.DataAnnotations;

namespace Common.CommunicationModels
{
    public class UpdateTenancyMemberRequest
    {
        [Required]
        public TenancyMemberRoleEnum Role { get; set; }
    }
}
