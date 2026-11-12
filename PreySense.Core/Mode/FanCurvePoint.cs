namespace PreySense.Mode;

/// <summary>Single temperature-to-RPM point on an RPM-native fan curve.</summary>
public sealed class FanCurvePoint
{
    public int TempC { get; set; }
    public int Rpm { get; set; }

    public FanCurvePoint() { }

    public FanCurvePoint(int tempC, int rpm)
    {
        TempC = tempC;
        Rpm = rpm;
    }

    public FanCurvePoint Copy() => new(TempC, Rpm);
}
