namespace RaCore;

/// <summary>
/// The one place that answers "how fast is my mouse now?", because the driver answers it in a way
/// nobody expects.
///
/// Upstream computes the base multiplier as
///     output_dpi / 1000  ×  1000 / device_dpi      (input_dpi_normalization_factor)
/// which is just <c>output_dpi / device_dpi</c> when the device has a DPI, and
/// <c>output_dpi / 1000</c> when it does not (0 = normalisation off). So "Output DPI = 1000", which
/// looks like a harmless default, is 1.00× only while the device DPI is empty - fill in 600 and the
/// very same number becomes 1.67×. Both settings are individually reasonable and the relationship
/// between them was invisible in the UI, which is how a 600 DPI mouse ends up behaving like 1000.
///
/// Kept in RaCore next to the accel math so the readout, the tool tip and the self tests cannot
/// drift apart: this file is the only definition of the formula in the project.
/// </summary>
public static class Sensitivity
{
    /// <summary>Driver reference DPI: at this DPI one count per ms is one inch per second.</summary>
    public const double NormalizedDpi = 1000;

    /// <summary>What the driver divides by when the device has no DPI filled in.</summary>
    public static double ReferenceDpi(double deviceDpi) => deviceDpi > 0 ? deviceDpi : NormalizedDpi;

    /// <summary>Base multiplier before the acceleration curve: output counts per input count.</summary>
    public static double BaseRatio(double outputDpi, double deviceDpi) =>
        outputDpi / ReferenceDpi(deviceDpi);

    /// <summary>
    /// The DPI the pointer effectively has at low speed, i.e. before any acceleration. Equal to the
    /// output DPI whenever the device DPI is known, because that is exactly what the driver makes
    /// one inch of hand movement produce. Zero means "cannot be known": with normalisation off the
    /// reference is a unitless 1000, so no physical DPI can be derived from it.
    /// </summary>
    public static double EffectiveDpi(double outputDpi, double deviceDpi) =>
        deviceDpi > 0 ? outputDpi : 0;

    /// <summary>True when "1:1" holds, i.e. the number on the mouse box is the number you get.</summary>
    public static bool IsUnity(double outputDpi, double deviceDpi) =>
        Math.Abs(BaseRatio(outputDpi, deviceDpi) - 1) < 0.005;
}
