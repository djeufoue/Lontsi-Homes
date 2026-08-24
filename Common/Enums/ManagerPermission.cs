using System;

namespace Common.Enums;

[Flags]
public enum ManagerPermission : long
{
    None = 0,

    ViewProperty = 1L << 0,
    EditProperty = 1L << 1,
    DeleteProperty = 1L << 2,
    ViewPropertyFinancialInformation = 1L << 3,
    EditPropertyFinancialInformation = 1L << 4,

    ViewMembers = 1L << 5,
    AddMember = 1L << 6,
    EditMember = 1L << 7,
    RemoveMember = 1L << 8,
    ManageMemberRoles = 1L << 9,
    ViewManagers = 1L << 10,

    ViewApartments = 1L << 11,
    AddApartment = 1L << 12,
    EditApartment = 1L << 13,
    DeleteApartment = 1L << 14,
    ViewApartmentDetails = 1L << 15,
    ViewApartmentFinancialInformation = 1L << 16,
    ManageApartmentMembers = 1L << 17,
    ManageApartmentDocuments = 1L << 18,

    ViewTenancies = 1L << 19,
    AddTenancy = 1L << 20,
    EditTenancy = 1L << 21,
    DeleteTenancy = 1L << 22,
    TerminateTenancy = 1L << 23,
    RenewTenancy = 1L << 24,
    ViewTenancyDetails = 1L << 25,
    ViewTenancyMembers = 1L << 26,
    AddTenancyMember = 1L << 27,
    EditTenancyMember = 1L << 28,
    RemoveTenancyMember = 1L << 29,
    ViewLeaseDocuments = 1L << 30,
    UploadLeaseDocuments = 1L << 31,
    DeleteLeaseDocuments = 1L << 32,

    ViewRentInformation = 1L << 33,
    ViewRentPaymentHistory = 1L << 34,
    MarkRentAsPaid = 1L << 35,
    MarkRentAsUnpaid = 1L << 36,
    AddManualPayment = 1L << 37,
    EditPayment = 1L << 38,
    DeletePayment = 1L << 39,
    ViewOutstandingRent = 1L << 40,
    SendRentReminder = 1L << 41,

    ViewDocuments = 1L << 42,
    UploadDocuments = 1L << 43,
    DeleteDocuments = 1L << 44,
    ViewMessages = 1L << 45,
    SendMessages = 1L << 46,
    ViewDashboard = 1L << 47,
    AddProperty = 1L << 48,
    ViewPropertyOverview = 1L << 49,
    ManageRentReminderSettings = 1L << 50
}

public static class ManagerPermissionDefaults
{
    public const ManagerPermission ReadOnly =
        ManagerPermission.ViewProperty |
        ManagerPermission.ViewPropertyOverview |
        ManagerPermission.ViewPropertyFinancialInformation |
        ManagerPermission.ViewMembers |
        ManagerPermission.ViewManagers |
        ManagerPermission.ViewApartments |
        ManagerPermission.ViewApartmentDetails |
        ManagerPermission.ViewApartmentFinancialInformation |
        ManagerPermission.ViewTenancies |
        ManagerPermission.ViewTenancyDetails |
        ManagerPermission.ViewTenancyMembers |
        ManagerPermission.ViewLeaseDocuments |
        ManagerPermission.ViewRentInformation |
        ManagerPermission.ViewRentPaymentHistory |
        ManagerPermission.ViewOutstandingRent |
        ManagerPermission.ViewDocuments |
        ManagerPermission.ViewMessages |
        ManagerPermission.ViewDashboard;

    // New property managers can maintain unit information by default. The landlord
    // can still remove EditApartment from the granular permission screen.
    public const ManagerPermission Standard = ReadOnly | ManagerPermission.EditApartment;

    public const ManagerPermission All = (ManagerPermission)((1L << 51) - 1);
}
