using LontsiHomes.API.Models.Entities;

namespace LontsiHomes.API.Services.Users;

public interface IManagerInvitationEmailService
{
    Task SendPropertyAccessEmailAsync(ApplicationUser manager, Property property, bool isNewUser);
}
