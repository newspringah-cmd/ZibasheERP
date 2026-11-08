namespace ZibasheERP.Application.Services;

public static class SalesListPrice
{
    public static decimal RoundUpPerMl(decimal price) => price > 0
        ? decimal.Ceiling(price / 1000m) * 1000m
        : price;
}
