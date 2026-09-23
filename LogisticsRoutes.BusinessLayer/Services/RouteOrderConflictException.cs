namespace LogisticsRoutes.BusinessLayer.Services;

public class RouteOrderConflictException(string message) : Exception(message);
