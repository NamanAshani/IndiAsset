
namespace IndiAsset.Models
{
    public enum BookingStatus
    {
        Pending,
        Approved,
        Rejected,
        Cancelled,
        Active,
        Overdue,
        Completed
    }

    public enum PaymentStatus
    {
        Pending,
        Paid,
        Failed,
        PartiallyRefunded,
        Refunded
    }
}