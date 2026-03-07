using System.Collections.Generic;

namespace Common.CommunicationModels
{
    public class PropertyListResponseDto
    {
        public List<PropertyDto> Items { get; set; } = new();

        public int Page { get; set; }
        public int PageSize { get; set; }
        public int TotalCount { get; set; }

        public string UserRole { get; set; } = string.Empty;
        public bool CanCreateProperty { get; set; }

        public List<PropertyCreationScopeDto> CreationScopes { get; set; } = new();
    }
}

