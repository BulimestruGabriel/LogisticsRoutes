namespace LogisticsRoutes.Domain.Entities;

public enum DeliveryStatus
{
    Pending,
    Departed,
    Arrived,
    Delivered,
    Refused,
    PartialReturn
}
