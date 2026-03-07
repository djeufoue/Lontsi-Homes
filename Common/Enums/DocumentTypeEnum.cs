namespace Common.Enums
{
    /// <summary>
    /// Specifies the type of document stored in the system.  This helps determine
    /// which entity the document belongs to and what it represents.
    /// </summary>
    public enum DocumentTypeEnum
    {
        ApartmentImage = 1,
        PropertyImage = 2,
        PropertyRules = 3,
        TenancyContract = 4,
        Other = 99
    }
}