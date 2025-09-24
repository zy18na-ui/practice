﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Shared.DTOs
{
    public class DefectiveItemDto
    {
        [JsonPropertyName("defectiveitemid")]
        public int DefectiveItemId { get; set; }

        [JsonPropertyName("productid")]
        public int ProductId { get; set; }

        [JsonPropertyName("reporteddate")]
        public DateTime ReportedDate { get; set; }

        [JsonPropertyName("defectdescription")]
        public string DefectDescription { get; set; }

        [JsonPropertyName("quantity")]
        public int Quantity { get; set; }

        [JsonPropertyName("status")]
        public string Status { get; set; }

        [JsonPropertyName("createdat")]
        public DateTime CreatedAt { get; set; }

        [JsonPropertyName("updatedat")]
        public DateTime UpdatedAt { get; set; }

        [JsonPropertyName("reportedbyuserid")]
        public int? ReportedByUserId { get; set; }
    }
}
