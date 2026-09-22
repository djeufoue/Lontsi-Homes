using Common.Enums;
using Microsoft.AspNetCore.Identity;
using LontsiHomes.API.Models.Entities;
using LontsiHomes.API.Services.Email;

namespace LontsiHomes.API.Services.Users;

public sealed class ManagerInvitationEmailService : IManagerInvitationEmailService
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IEmailService _emailService;
    private readonly IConfiguration _configuration;

    public ManagerInvitationEmailService(
        UserManager<ApplicationUser> userManager,
        IEmailService emailService,
        IConfiguration configuration)
    {
        _userManager = userManager;
        _emailService = emailService;
        _configuration = configuration;
    }

    public async Task SendPropertyAccessEmailAsync(ApplicationUser manager, Property property, bool isNewUser)
    {
        if (string.IsNullOrWhiteSpace(manager.Email))
        {
            return;
        }

        var baseUrl = (_configuration["Portal:BaseUrl"] ?? string.Empty).Trim().TrimEnd('/');
        var loginUrl = string.IsNullOrWhiteSpace(baseUrl) ? string.Empty : $"{baseUrl}/Auth/Login";
        var displayName = string.IsNullOrWhiteSpace(manager.FullName) ? manager.Email : manager.FullName;
        var isFrench = manager.EmailLanguage == PlatformLanguage.French;
        var lines = new List<string>
        {
            isFrench ? $"Bonjour {displayName}," : $"Hello {displayName},",
            string.Empty,
            isFrench
                ? $"Vous avez été ajouté comme gestionnaire de la propriété {property.Name} sur Lontsi Homes."
                : $"You have been added as a Manager for {property.Name} on Lontsi Homes.",
            isFrench ? $"Votre adresse de connexion est : {manager.Email}" : $"Your sign-in email is: {manager.Email}",
            string.Empty
        };

        if (isNewUser)
        {
            var token = await _userManager.GeneratePasswordResetTokenAsync(manager);
            var setPasswordUrl = string.IsNullOrWhiteSpace(baseUrl)
                ? string.Empty
                : $"{baseUrl}/Auth/SetPassword?email={Uri.EscapeDataString(manager.Email)}&token={Uri.EscapeDataString(token)}";

            lines.Add(isFrench
                ? "Votre compte est nouveau. Créez votre mot de passe privé avec ce lien sécurisé à usage unique :"
                : "Your account is new. Create a private password using this secure, single-use link:");
            if (!string.IsNullOrWhiteSpace(setPasswordUrl))
            {
                lines.Add(setPasswordUrl);
            }
            lines.Add(isFrench
                ? "Après avoir créé votre mot de passe, connectez-vous pour accéder à la propriété."
                : "After creating your password, sign in to access the property.");
        }
        else
        {
            lines.Add(isFrench
                ? "Utilisez votre mot de passe actuel. Il n’a pas été modifié ni réinitialisé."
                : "Use your existing password. Your password has not been changed or reset.");
        }

        if (!string.IsNullOrWhiteSpace(loginUrl))
        {
            lines.Add(string.Empty);
            lines.Add(isFrench ? "Se connecter :" : "Sign in:");
            lines.Add(loginUrl);
        }

        lines.Add(string.Empty);
        lines.Add(isFrench
            ? "L’accès d’un nouveau gestionnaire est en lecture seule jusqu’à ce que le bailleur accorde explicitement des permissions supplémentaires."
            : "New Manager access is read-only until the landlord explicitly grants additional permissions.");
        lines.Add(isFrench
            ? "Si vous ne vous attendiez pas à recevoir cet accès, contactez le bailleur de la propriété."
            : "If you did not expect this access, contact the property landlord.");

        await _emailService.SendEmailAsync(
            manager.Email,
            isFrench
                ? $"Accès gestionnaire à {property.Name} - Lontsi Homes"
                : $"Manager access to {property.Name} - Lontsi Homes",
            string.Join(Environment.NewLine, lines));
    }
}
