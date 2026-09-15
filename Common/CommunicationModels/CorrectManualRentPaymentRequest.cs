using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace Common.CommunicationModels
{
    public class CorrectManualRentPaymentRequest
    {
        public int TenancyId { get; set; }
        public Guid RequestId { get; set; }
        [Required, StringLength(1000, MinimumLength = 5)]
        public string Reason { get; set; } = string.Empty;
        public List<int> RentPeriodIds { get; set; } = new();
        // Binds the preview to the exact payments seen by the user (stale pages cannot undo a repayment).
        public Dictionary<int, int> ExpectedPaymentIds { get; set; } = new();
    }
}
