using System.ComponentModel.DataAnnotations;

namespace RentHub.Portal.ViewModels.Properties
{
    public class CreatePropertyVm
    {
        [Required] public string Name { get; set; } = "";
        public string? City { get; set; }
        public string? Address { get; set; }
    }
}
