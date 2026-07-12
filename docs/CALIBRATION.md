# Calibration Notes

These notes capture what has been verified from the firmware, live gauge reads, LabVIEW screenshots, and the sample export.

## Verified Data Sources

The gauge host protocol can read sensor data through:

- `READ_SENSOR_SN`
- `READ_SENSOR_CAL`
- `READ_SENSOR_P_POLY`
- `READ_SENSOR_T_POLY`

The firmware forwards these commands to the attached pressure/temperature sensor.

## Sensor Header

Live header example:

```text
S: RefClk .0 Id 1777 Bias 12053700 PStartupMs 5000 PLLClk 169750000
=
```

Parsed fields:

- `RefClk`
- `Id`
- `Bias`
- `PStartupMs`
- `PLLClk`

The `Bias` value is important for reproducing legacy export count columns. The memory record stores 24-bit pressure and temperature count values. Adding the sensor `Bias` puts the count values into the same scale as the LabVIEW export.

## Polynomial Payloads

Pressure and temperature polynomial payloads are ASCII rows of 16-character hexadecimal IEEE-754 double values.

The LabVIEW Engineering Sensor screen shows:

- Pressure polynomial array size: 29
- Temperature polynomial array size: 6

This matches the payloads read through the new CLI.

## Temperature Polynomial Shape

The temperature polynomial has 6 doubles:

```text
[x_min, x_max]
[c0, c1, c2, c3]
```

The LabVIEW screen displays the same arrangement as a flattened array.

Convert full-scale temperature counts to crystal frequency:

```text
temperature_frequency_hz = pll_clock * 262000 / (temperature_counts * 10)
```

Normalize to the coefficient domain:

```text
x = ((2 * temperature_frequency_hz) - x_min - x_max) / (x_max - x_min)
```

Evaluate:

```text
temperature_c = c0 + c1*x + c2*x^2 + c3*x^3
```

## Pressure Polynomial Shape

The pressure polynomial has 29 doubles. From the row structure observed in live data:

```text
row 0: 2 values
row 1: 2 values
rows 2-6: 5 values each
```

This is likely a bivariate pressure compensation surface using pressure frequency/count and temperature, but the exact equation and normalization still need confirmation before engineering-unit pressure is implemented.

Confirmed from `XHTI-7-1000155 Report.pdf`: the pressure rows are:

```text
row 0: pressure frequency normalization domain [p_min, p_max]
row 1: temperature frequency normalization domain [t_min, t_max]
rows 2-6: LabVIEW reshapes the remaining 25 coefficients as a 5x5 matrix and indexes it as x-power rows by y-power columns
```

Convert full-scale pressure counts to crystal frequency:

```text
pressure_frequency_hz = pll_clock * 50000 / (pressure_counts * 10)
```

Normalize pressure and temperature frequency:

```text
x = ((2 * pressure_frequency_hz) - p_min - p_max) / (p_max - p_min)
y = ((2 * temperature_frequency_hz) - t_min - t_max) / (t_max - t_min)
```

Evaluate:

```text
pressure_psi = sum(coeff[i][j] * x^i * y^j), for i=0..4 and j=0..4
```

This is implemented in `Gauge.Calibration.QuartzCalibration` and tested against live sensor ID `1777` calibration payloads captured from the gauge.

## Next Calibration Task

Completed:

- `QuartzCalibration` is wired into the calibrated download/export workflow.
- Live gauge downloads have produced sane pressure and temperature values matching the expected LabVIEW range.

Next work:

- Keep adding known-good raw downloads and matching legacy outputs as regression fixtures.
- Confirm export metadata/format requirements for website upload.
