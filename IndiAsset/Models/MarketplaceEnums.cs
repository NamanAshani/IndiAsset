
namespace IndiAsset.Models
{
    public enum BookingStatus
    {
        Pending,
        Approved,
        Rejected,
        Cancelled,
        Active,
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