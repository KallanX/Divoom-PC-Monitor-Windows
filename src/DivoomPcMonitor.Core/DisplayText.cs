namespace DivoomPcMonitor;

public static class DisplayText
{
    public const char Degree = '\u00B0';

    public static string Percent(int? value)
    {
        return value is null ? "--" : $"{value.Value}%";
    }

    public static string Temperature(int? value)
    {
        return value is null ? "--" : $"{value.Value}{Degree}C";
    }
}
