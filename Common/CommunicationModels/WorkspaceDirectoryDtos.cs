using System;
using System.Collections.Generic;

namespace Common.CommunicationModels
{
    public class WorkspaceDirectoryResponseDto<T>
    {
        public List<T> Items { get; set; } = new();
        public List<WorkspaceFilterOptionDto> Properties { get; set; } = new();
        public List<string> Statuses { get; set; } = new();
        public List<string> Roles { get; set; } = new();
        public string Search { get; set; } = string.Empty;
        public int? PropertyId { get; set; }
        public string Status { get; set; } = string.Empty;
        public string Role { get; set; } = string.Empty;
        public int Page { get; set; } = 1;
        public int PageSize { get; set; } = 12;
        public int TotalCount { get; set; }
        public int ScopeTotalCount { get; set; }
        public int TotalPages => PageSize <= 0 ? 1 : Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));
    }

    public class WorkspaceFilterOptionDto
    {
        public int Id { get; set; }
        public string Label { get; set; } = string.Empty;
    }

    public class WorkspaceApartmentDto
    {
        public int Id { get; set; }
        public int PropertyId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string PropertyName { get; set; } = string.Empty;
        public string LandlordName { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public decimal Rent { get; set; }
        public double Area { get; set; }
        public int ActiveTenancies { get; set; }
        public int MemberCount { get; set; }
    }

    public class WorkspaceTenancyDto
    {
        public int Id { get; set; }
        public int ApartmentId { get; set; }
        public int PropertyId { get; set; }
        public string PropertyName { get; set; } = string.Empty;
        public string ApartmentName { get; set; } = string.Empty;
        public string LandlordName { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public DateTimeOffset StartDate { get; set; }
        public DateTimeOffset? EndDate { get; set; }
        public decimal MonthlyRent { get; set; }
        public int MemberCount { get; set; }
        public int MaxMembers { get; set; }
    }

    public class WorkspaceMemberDto
    {
        public string AssignmentKey { get; set; } = string.Empty;
        public string UserId { get; set; } = string.Empty;
        public string FullName { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string Role { get; set; } = string.Empty;
        public string Level { get; set; } = string.Empty;
        public string Permission { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public int PropertyId { get; set; }
        public string PropertyName { get; set; } = string.Empty;
        public int? ApartmentId { get; set; }
        public string ApartmentName { get; set; } = string.Empty;
        public int? TenancyId { get; set; }
        public string LandlordName { get; set; } = string.Empty;
        public DateTimeOffset AssignedAt { get; set; }
    }
}
