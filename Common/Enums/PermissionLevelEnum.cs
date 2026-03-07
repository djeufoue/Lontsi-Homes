namespace Common.Enums
{
    /// <summary>
    /// Represents authorization levels for delegated users (owners and managers).
    /// </summary>
    public enum PermissionLevelEnum
    {
        /// <summary>
        /// Read-only access.  User may view information but cannot modify or add items.
        /// </summary>
        ReadOnly = 0,

        /// <summary>
        /// Read-write access.  User may create and modify items (e.g., tenancies) within their scope.
        /// </summary>
        ReadWrite = 1
    }
}