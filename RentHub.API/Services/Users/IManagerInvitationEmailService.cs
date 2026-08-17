using RentHub.API.Models.Entities;

namespace RentHub.API.Services.Users;

public interface IManagerInvitationEmailService
{
    Task SendPropertyAccessEmailAsync(ApplicationUser manager, Property property, bool isNewUser);
}
