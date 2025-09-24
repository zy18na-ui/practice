using System;

namespace dataAccess.Entities
{
    public class Sales
    {
        public int OrderId { get; set; }
        public DateTime OrderDate { get; set; }
        public int ProductId { get; set; }
        public string ProductName { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public decimal UnitPrice { get; set; }
        public decimal Subtotal { get; set; }
        public decimal Revenue { get; set; }
        public decimal Profit { get; set; }
    }
}
