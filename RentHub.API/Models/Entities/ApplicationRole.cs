using Microsoft.AspNetCore.Identity;

namespace RentHub.API.Models.Entities
{
    /// <summary>
    /// Custom role class to accompany ApplicationUser. Roles such as Landlord, Tenant or Admin
    /// can be defined in the database.
    /// </summary>
    public class ApplicationRole : IdentityRole
    {
    }
}