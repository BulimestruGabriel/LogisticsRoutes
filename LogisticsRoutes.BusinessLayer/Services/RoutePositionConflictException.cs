namespace LogisticsRoutes.BusinessLayer.Services;

public class RoutePositionConflictException(string message) : Exception(message);
