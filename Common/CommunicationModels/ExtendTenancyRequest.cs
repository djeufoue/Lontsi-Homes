using System;

namespace Common.CommunicationModels
{
    /// <summary>
    /// Used to specify a new end date for a tenancy when extending or renewing.
    /// </summary>
    public class ExtendTenancyRequest
    {
        public DateTimeOffset NewEndDate { get; set; }
    }
}