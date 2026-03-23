namespace Common.Enums
{
    /// <summary>
    /// Defines roles for members within a tenancy.
    /// </summary>
    public enum TenancyMemberRoleEnum
    {
        MainTenant = 1,
        CoTenant = 2,
        Children = 3,
        Visitor = 4
    }
}
