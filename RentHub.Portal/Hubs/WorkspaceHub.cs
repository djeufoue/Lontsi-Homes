using Microsoft.AspNetCore.SignalR;

namespace RentHub.Portal.Hubs
{
    public class WorkspaceHub : Hub
    {
        public Task JoinPropertyGroup(int propertyId)
        {
            return Groups.AddToGroupAsync(Context.ConnectionId, GetPropertyGroup(propertyId));
        }

        public Task LeavePropertyGroup(int propertyId)
        {
            return Groups.RemoveFromGroupAsync(Context.ConnectionId, GetPropertyGroup(propertyId));
        }

        public static string GetPropertyGroup(int propertyId) => $"property-{propertyId}";
    }
}
